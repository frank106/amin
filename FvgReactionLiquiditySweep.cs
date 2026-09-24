using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Linq;

using ATAS.Indicators;

using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;

using Color = System.Drawing.Color;
using DashStyle = System.Drawing.Drawing2D.DashStyle;

namespace ATAS.Indicators.Technical
{
	// Custom ATAS indicator:
	//   1) Detects 3-candle Fair Value Gaps (imbalances)
	//   2) Flags a "good reaction" when price returns into a gap and closes back out
	//      in the direction of the original imbalance (rejection)
	//   3) Flags liquidity sweeps - a wick beyond a recent swing high/low that closes
	//      back inside it (stop hunt / grab before reversal)
	//   4) Paints an absorption heatmap - per-tick price levels inside each candle
	//      where one side traded unusually heavy volume (from footprint/cluster
	//      data) without price continuing through, i.e. it got absorbed.
	//   5) Turns 2) and 3) into BUY / SHORT signals with a fixed bracket (default
	//      80-tick take profit / 80-tick stop loss), follows every signal until one
	//      side of the bracket trades, and labels each new signal with the
	//      probability of hitting TP first vs SL first.
	//
	// How the TP / SL probability is estimated
	// ----------------------------------------
	// Every signal is tracked as a virtual trade: entry at the signal bar's close,
	// TP and SL a fixed number of ticks away, and whichever trades first decides it.
	// A signal's probability is computed ONLY from trades that had already finished
	// when it fired (walk-forward, no look-ahead), so the numbers on old signals are
	// exactly what the indicator would have shown live.
	//
	// Signals are grouped direction -> trigger (FVG / sweep / sweep then FVG) ->
	// number of confirmations (absorption, EMA trend, bar delta; 0-3). A group with
	// few trades is shrunk toward its parent group (Beta-Binomial smoothing):
	//     p = (wins + k * p_parent) / (trades + k)
	// and the top-level prior is SL / (TP + SL), the exact odds of a driftless
	// random walk (50% for a symmetric 80/80 bracket). With no history a signal
	// starts at 50% and only moves as real outcomes build up on the chart.
	//
	// Tuned defaults are set for NQ, but the logic itself is instrument-agnostic since
	// it reads InstrumentInfo.TickSize rather than hardcoded point values.
	//
	// NOTE on "heatmap": ATAS's own Heatmap module shows resting DOM liquidity
	// (limit orders), which is a different data source than absorption. Absorption
	// is read from executed-volume footprint data instead (GetAllPriceLevels on
	// the candle), then rendered here as heatmap-style colored cells - same visual
	// idea, correct data source for what "absorption" actually means.

	[DisplayName("FVG Reaction + Liquidity Sweep")]
	[Category("My Indicators")]
	public class FvgReactionLiquiditySweep : Indicator
	{
		#region Nested types

		public enum SignalMode
		{
			[Display(Name = "FVG reaction or liquidity sweep")]
			AnyTrigger,

			[Display(Name = "FVG reaction only")]
			FvgReactionOnly,

			[Display(Name = "Liquidity sweep only")]
			LiquiditySweepOnly,

			[Display(Name = "Sweep, then FVG reaction (confluence)")]
			SweepThenFvg
		}

		public enum SameBarHitRule
		{
			[Display(Name = "Stop loss first (conservative)")]
			StopLossFirst,

			[Display(Name = "Candle direction (O-L-H-C / O-H-L-C)")]
			CandleDirection
		}

		public enum PanelCorner
		{
			[Display(Name = "Top left")]
			TopLeft,

			[Display(Name = "Top right")]
			TopRight,

			[Display(Name = "Bottom left")]
			BottomLeft,

			[Display(Name = "Bottom right")]
			BottomRight
		}

		private enum TriggerType
		{
			Fvg = 0,
			Sweep = 1,
			SweepThenFvg = 2
		}

		private enum TradeOutcome
		{
			Open,
			TakeProfit,
			StopLoss,
			Expired
		}

		private class FvgZone
		{
			public int StartBar;      // bar index of the middle (impulse) candle
			public int ConfirmedBar;  // right-hand candle - the gap only exists once it has closed
			public decimal Top;
			public decimal Bottom;
			public bool IsBullish;
			public bool Filled;       // price has fully closed through the zone -> invalidated
			public bool ReactionMarked;
		}

		// A flagged footprint level, copied out of the candle so the render thread never
		// touches ATAS's live PriceVolumeInfo objects.
		private readonly struct AbsorptionCell
		{
			public AbsorptionCell(decimal price, decimal volume, bool askDominant)
			{
				Price = price;
				Volume = volume;
				AskDominant = askDominant;
			}

			public decimal Price { get; }
			public decimal Volume { get; }
			public bool AskDominant { get; } // aggressive buying got absorbed (else aggressive selling)
		}

		private class AbsorptionBar
		{
			public decimal High;
			public decimal Low;
			public AbsorptionCell[] Cells;
		}

		private class OutcomeCounter
		{
			public int Wins;
			public int Losses;
			public int Count => Wins + Losses;
		}

		// What the model knew at the moment a signal fired
		private readonly struct ProbabilityEstimate
		{
			public ProbabilityEstimate(double takeProfit, OutcomeCounter setup, OutcomeCounter trigger, OutcomeCounter direction)
			{
				TakeProfit = takeProfit;
				SetupWins = setup.Wins;
				SetupCount = setup.Count;
				TriggerWins = trigger.Wins;
				TriggerCount = trigger.Count;
				DirectionWins = direction.Wins;
				DirectionCount = direction.Count;
			}

			public double TakeProfit { get; } // P(TP trades before SL)
			public double StopLoss => 1 - TakeProfit;
			public int SetupWins { get; }
			public int SetupCount { get; }
			public int TriggerWins { get; }
			public int TriggerCount { get; }
			public int DirectionWins { get; }
			public int DirectionCount { get; }
		}

		// Walk-forward estimate of P(TP before SL), smoothed down the hierarchy
		// direction -> direction + trigger -> direction + trigger + confirmations.
		private class ProbabilityModel
		{
			private const int Directions = 2;
			private const int Triggers = 3;
			private const int ConfirmationLevels = MaxConfirmations + 1;

			private readonly OutcomeCounter[] _byDirection = new OutcomeCounter[Directions];
			private readonly OutcomeCounter[,] _byTrigger = new OutcomeCounter[Directions, Triggers];
			private readonly OutcomeCounter[,,] _bySetup = new OutcomeCounter[Directions, Triggers, ConfirmationLevels];

			public ProbabilityModel()
			{
				Reset();
			}

			public int Resolved { get; private set; }

			public void Reset()
			{
				Resolved = 0;

				for (var d = 0; d < Directions; d++)
				{
					_byDirection[d] = new OutcomeCounter();

					for (var t = 0; t < Triggers; t++)
					{
						_byTrigger[d, t] = new OutcomeCounter();

						for (var c = 0; c < ConfirmationLevels; c++)
							_bySetup[d, t, c] = new OutcomeCounter();
					}
				}
			}

			public void Record(bool isLong, TriggerType trigger, int confirmations, bool hitTakeProfit)
			{
				var d = isLong ? 0 : 1;
				var t = (int)trigger;

				Add(_byDirection[d], hitTakeProfit);
				Add(_byTrigger[d, t], hitTakeProfit);
				Add(_bySetup[d, t, confirmations], hitTakeProfit);
				Resolved++;
			}

			public ProbabilityEstimate Estimate(bool isLong, TriggerType trigger, int confirmations, double prior, double priorWeight)
			{
				var d = isLong ? 0 : 1;
				var t = (int)trigger;
				var direction = _byDirection[d];
				var triggerStats = _byTrigger[d, t];
				var setup = _bySetup[d, t, confirmations];

				var p = Smooth(direction, prior, priorWeight);
				p = Smooth(triggerStats, p, priorWeight);
				p = Smooth(setup, p, priorWeight);

				return new ProbabilityEstimate(p, setup, triggerStats, direction);
			}

			private static void Add(OutcomeCounter counter, bool win)
			{
				if (win)
					counter.Wins++;
				else
					counter.Losses++;
			}

			private static double Smooth(OutcomeCounter counter, double prior, double weight)
			{
				return (counter.Wins + weight * prior) / (counter.Count + weight);
			}
		}

		private class SignalTrade
		{
			public int EntryBar;
			public DateTime EntryTime;
			public bool IsLong;
			public decimal EntryPrice;
			public decimal TakeProfitPrice;
			public decimal StopLossPrice;
			public decimal SignalHigh;
			public decimal SignalLow;
			public TriggerType Trigger;
			public bool HasAbsorption;
			public bool WithTrend;
			public bool DeltaConfirms;
			public int Confirmations;
			public ProbabilityEstimate Estimate;
			public bool IsShown;          // false = tracked for the statistics only (filtered, or a position was already open)
			public TradeOutcome Outcome;
			public int ExitBar = -1;
			public decimal ExitPrice;
			public bool AmbiguousExit;    // TP and SL both inside one bar - SameBarRule decided it

			public SignalTrade Clone()
			{
				return (SignalTrade)MemberwiseClone();
			}
		}

		private class PanelStats
		{
			public int LongWins;
			public int LongLosses;
			public int ShortWins;
			public int ShortLosses;
			public int Open;
			public int Expired;
			public int Filtered;
			public int ModelResolved;
			public decimal LongNetTicks;
			public decimal ShortNetTicks;
			public int HighOddsWins;
			public int HighOddsCount;
			public int LowOddsWins;
			public int LowOddsCount;
			public SignalTrade OpenTrade;  // most recent open trade shown on the chart
			public decimal LastPrice;
		}

		private readonly struct PendingAlert
		{
			public PendingAlert(string message, Color background)
			{
				Message = message;
				Background = background;
			}

			public string Message { get; }
			public Color Background { get; }
		}

		#endregion

		#region Fields

		private const int MaxConfirmations = 3;

		// the panel's track record: how signals labelled at least / at most this TP % did
		private const int HighOddsPercent = 60;
		private const int LowOddsPercent = 40;

		// Share of the bar's range, measured from the low (buys) or high (shorts), that
		// counts as "at the extreme" for the absorption confirmation.
		private const decimal AbsorptionEdgeFraction = 0.35m;

		private const int MinHeatmapCellHeight = 3;

		// signal and trigger arrows sit this many ticks beyond the bar's low / high
		private const int ArrowOffsetTicks = 2;

		// OnRender runs on a different thread than OnCalculate; everything below that
		// both touch is guarded by this lock.
		private readonly object _sync = new object();

		private readonly List<FvgZone> _zones = new List<FvgZone>();

		// Absorption heatmap: bar index -> flagged high-volume, one-side-dominant
		// price levels within that bar's footprint data.
		private readonly Dictionary<int, AbsorptionBar> _absorptionByBar = new Dictionary<int, AbsorptionBar>();

		private readonly List<SignalTrade> _trades = new List<SignalTrade>();
		private readonly List<SignalTrade> _openTrades = new List<SignalTrade>();
		private readonly List<decimal> _ema = new List<decimal>();
		private readonly ProbabilityModel _model = new ProbabilityModel();

		private int _lastClosedBar = -1;
		private int _lastLowSweepBar = -1;
		private int _lastHighSweepBar = -1;
		private int _lastLongSignalBar = -1;
		private int _lastShortSignalBar = -1;
		private bool _realtime;
		private decimal _lastPrice;

		// results of the signals shown on the chart (the model also learns from filtered ones)
		private int _longWins;
		private int _longLosses;
		private int _shortWins;
		private int _shortLosses;
		private int _expired;
		private int _filtered;
		private decimal _longNetTicks;
		private decimal _shortNetTicks;

		// every settled signal, shown or not, by the TP % it was labelled with
		private int _highOddsWins;
		private int _highOddsCount;
		private int _lowOddsWins;
		private int _lowOddsCount;

		// Arrow series shown on the price panel. ValueDataSeries.Color is ATAS's
		// CrossColor (WPF Color on Windows, System.Drawing.Color on ATAS X), so the
		// colors go through .Convert() - that compiles on both platforms, unlike
		// System.Windows.Media.Colors. The ids keep the original series names so
		// saved chart templates still find them.
		private readonly ValueDataSeries _bullReaction = new ValueDataSeries("FVG Bull Reaction", "FVG Bull Reaction")
		{
			VisualType = VisualMode.UpArrow,
			Color = Color.Lime.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _bearReaction = new ValueDataSeries("FVG Bear Reaction", "FVG Bear Reaction")
		{
			VisualType = VisualMode.DownArrow,
			Color = Color.Red.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _bullSweep = new ValueDataSeries("Liquidity Sweep Low", "Liquidity Sweep Low")
		{
			VisualType = VisualMode.UpArrow,
			Color = Color.Aqua.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _bearSweep = new ValueDataSeries("Liquidity Sweep High", "Liquidity Sweep High")
		{
			VisualType = VisualMode.DownArrow,
			Color = Color.Magenta.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _buySignal = new ValueDataSeries("Buy Signal", "Buy Signal")
		{
			VisualType = VisualMode.UpArrow,
			Color = Color.FromArgb(255, 0, 200, 83).Convert(),
			Width = 5,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _shortSignal = new ValueDataSeries("Short Signal", "Short Signal")
		{
			VisualType = VisualMode.DownArrow,
			Color = Color.FromArgb(255, 255, 45, 85).Convert(),
			Width = 5,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private int _swingLookback = 10;
		private int _minFvgTicks = 4;
		private int _maxZoneAgeBars = 150;
		private bool _drawZones = true;
		private bool _requireCloseThroughZone = true;
		private Color _bullishZoneColor = Color.FromArgb(50, 0, 200, 0);
		private Color _bearishZoneColor = Color.FromArgb(50, 200, 0, 0);

		private bool _showAbsorptionHeatmap = true;
		private int _absorptionMinVolume = 150;
		private double _absorptionVolumeMultiplier = 3.0;
		private double _absorptionDominanceRatio = 0.6;
		private int _absorptionMaxAgeBars = 100;

		private bool _enableBuySignals = true;
		private bool _enableShortSignals = true;
		private SignalMode _signalSource = SignalMode.AnyTrigger;
		private int _confluenceBars = 10;
		private int _trendEmaPeriod = 50;
		private bool _onlyWithTrend;
		private bool _requireDeltaConfirmation;
		private bool _requireAbsorption;
		private int _signalCooldownBars = 3;
		private bool _oneTradeAtATime = true;
		private int _minProbabilityPercent;

		private int _takeProfitTicks = 80;
		private int _stopLossTicks = 80;
		private int _maxBarsInTrade;
		private bool _expireAtSessionEnd;
		private SameBarHitRule _sameBarRule = SameBarHitRule.StopLossFirst;
		private int _probabilitySmoothing = 10;

		#endregion

		#region Properties (settings panel in ATAS)

		[Display(Name = "Swing Lookback (bars)", GroupName = "Liquidity Sweep", Order = 10)]
		[Range(2, 1000)]
		public int SwingLookback
		{
			get => _swingLookback;
			set { _swingLookback = Math.Max(2, value); RecalculateValues(); }
		}

		[Display(Name = "Min FVG Size (ticks)", GroupName = "Fair Value Gap", Order = 20)]
		[Range(0, 10000)]
		public int MinFvgTicks
		{
			get => _minFvgTicks;
			set { _minFvgTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Zone Max Age (bars)", GroupName = "Fair Value Gap", Order = 21)]
		[Range(5, 100000)]
		public int MaxZoneAgeBars
		{
			get => _maxZoneAgeBars;
			set { _maxZoneAgeBars = Math.Max(5, value); RecalculateValues(); }
		}

		[Display(Name = "Require reaction close beyond zone", GroupName = "Fair Value Gap", Order = 22,
			Description = "If true, a reaction candle must CLOSE outside the zone, not just wick into it and close inside.")]
		public bool RequireCloseThroughZone
		{
			get => _requireCloseThroughZone;
			set { _requireCloseThroughZone = value; RecalculateValues(); }
		}

		[Display(Name = "Draw FVG Zones", GroupName = "Fair Value Gap", Order = 23)]
		public bool DrawZones
		{
			get => _drawZones;
			set { _drawZones = value; RedrawChart(); }
		}

		[Display(Name = "Bullish zone color", GroupName = "Fair Value Gap", Order = 24)]
		public Color BullishZoneColor
		{
			get => _bullishZoneColor;
			set { _bullishZoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Bearish zone color", GroupName = "Fair Value Gap", Order = 25)]
		public Color BearishZoneColor
		{
			get => _bearishZoneColor;
			set { _bearishZoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Show Absorption Heatmap", GroupName = "Absorption", Order = 40,
			Description = "Colors price levels inside candles where one side traded heavy volume relative to the bar, per that bar's footprint/cluster data.")]
		public bool ShowAbsorptionHeatmap
		{
			get => _showAbsorptionHeatmap;
			set { _showAbsorptionHeatmap = value; RedrawChart(); }
		}

		[Display(Name = "Min Level Volume", GroupName = "Absorption", Order = 41,
			Description = "A price level needs at least this many contracts traded before it's considered for absorption at all.")]
		[Range(1, 100000000)]
		public int AbsorptionMinVolume
		{
			get => _absorptionMinVolume;
			set { _absorptionMinVolume = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Volume Multiplier (vs bar avg)", GroupName = "Absorption", Order = 42,
			Description = "Level volume must be at least this many times the bar's average per-level volume - flags 'unusually high volume at one level'.")]
		[Range(1.0, 1000.0)]
		public double AbsorptionVolumeMultiplier
		{
			get => _absorptionVolumeMultiplier;
			set { _absorptionVolumeMultiplier = Math.Max(1.0, value); RecalculateValues(); }
		}

		[Display(Name = "Dominant Side Ratio", GroupName = "Absorption", Order = 43,
			Description = "Min share (0.5-1) of a level's volume that Bid or Ask must have for it to count as one-sided absorption, e.g. 0.6 = 60%.")]
		[Range(0.5, 1.0)]
		public double AbsorptionDominanceRatio
		{
			get => _absorptionDominanceRatio;
			set { _absorptionDominanceRatio = Math.Min(1.0, Math.Max(0.5, value)); RecalculateValues(); }
		}

		[Display(Name = "Heatmap Max Age (bars)", GroupName = "Absorption", Order = 44)]
		[Range(5, 100000)]
		public int AbsorptionMaxAgeBars
		{
			get => _absorptionMaxAgeBars;
			set { _absorptionMaxAgeBars = Math.Max(5, value); RecalculateValues(); }
		}

		[Display(Name = "Buy signals", GroupName = "Signals", Order = 100)]
		public bool EnableBuySignals
		{
			get => _enableBuySignals;
			set { _enableBuySignals = value; RecalculateValues(); }
		}

		[Display(Name = "Short signals", GroupName = "Signals", Order = 101)]
		public bool EnableShortSignals
		{
			get => _enableShortSignals;
			set { _enableShortSignals = value; RecalculateValues(); }
		}

		[Display(Name = "Signal source", GroupName = "Signals", Order = 102,
			Description = "Which setups produce signals. Sweep-then-FVG only fires on an FVG reaction that follows a liquidity sweep on the same side.")]
		public SignalMode SignalSource
		{
			get => _signalSource;
			set { _signalSource = value; RecalculateValues(); }
		}

		[Display(Name = "Sweep -> FVG window (bars)", GroupName = "Signals", Order = 103,
			Description = "An FVG reaction within this many bars after a same-side liquidity sweep counts as the Sweep+FVG confluence setup.")]
		[Range(0, 1000)]
		public int ConfluenceBars
		{
			get => _confluenceBars;
			set { _confluenceBars = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Trend EMA period (0 = off)", GroupName = "Signals", Order = 104,
			Description = "Close above the EMA confirms buys, below confirms shorts. Counts as one of the three confirmations.")]
		[Range(0, 10000)]
		public int TrendEmaPeriod
		{
			get => _trendEmaPeriod;
			set { _trendEmaPeriod = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Only trade with the trend", GroupName = "Signals", Order = 105)]
		public bool OnlyWithTrend
		{
			get => _onlyWithTrend;
			set { _onlyWithTrend = value; RecalculateValues(); }
		}

		[Display(Name = "Require delta confirmation", GroupName = "Signals", Order = 106,
			Description = "Buys need a positive bar delta, shorts a negative one.")]
		public bool RequireDeltaConfirmation
		{
			get => _requireDeltaConfirmation;
			set { _requireDeltaConfirmation = value; RecalculateValues(); }
		}

		[Display(Name = "Require absorption confirmation", GroupName = "Signals", Order = 107,
			Description = "Buys need sell-side absorption near the low of the signal bar (or the bar before), shorts buy-side absorption near the high.")]
		public bool RequireAbsorption
		{
			get => _requireAbsorption;
			set { _requireAbsorption = value; RecalculateValues(); }
		}

		[Display(Name = "Cooldown between signals (bars)", GroupName = "Signals", Order = 108,
			Description = "Minimum bars before another signal in the same direction, so one move isn't counted several times.")]
		[Range(0, 1000)]
		public int SignalCooldownBars
		{
			get => _signalCooldownBars;
			set { _signalCooldownBars = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "One trade at a time", GroupName = "Signals", Order = 109,
			Description = "While a shown signal's trade is still open, new signals are not shown (they are still tracked for the statistics).")]
		public bool OneTradeAtATime
		{
			get => _oneTradeAtATime;
			set { _oneTradeAtATime = value; RecalculateValues(); }
		}

		[Display(Name = "Min TP probability to show (%)", GroupName = "Signals", Order = 110,
			Description = "Hide signals whose estimated take-profit probability is below this. Hidden signals are still tracked so the statistics keep learning.")]
		[Range(0, 100)]
		public int MinProbabilityPercent
		{
			get => _minProbabilityPercent;
			set { _minProbabilityPercent = Math.Min(100, Math.Max(0, value)); RecalculateValues(); }
		}

		[Display(Name = "Take profit (ticks)", GroupName = "Take Profit / Stop Loss", Order = 200)]
		[Range(1, 100000)]
		public int TakeProfitTicks
		{
			get => _takeProfitTicks;
			set { _takeProfitTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Stop loss (ticks)", GroupName = "Take Profit / Stop Loss", Order = 201)]
		[Range(1, 100000)]
		public int StopLossTicks
		{
			get => _stopLossTicks;
			set { _stopLossTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Max bars in trade (0 = no limit)", GroupName = "Take Profit / Stop Loss", Order = 202,
			Description = "Trades that hit neither level within this many bars expire and are left out of the probabilities.")]
		[Range(0, 100000)]
		public int MaxBarsInTrade
		{
			get => _maxBarsInTrade;
			set { _maxBarsInTrade = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Close trades at session end", GroupName = "Take Profit / Stop Loss", Order = 203,
			Description = "Expire open trades on the last bar of each session and take no new signals on it.")]
		public bool ExpireAtSessionEnd
		{
			get => _expireAtSessionEnd;
			set { _expireAtSessionEnd = value; RecalculateValues(); }
		}

		[Display(Name = "TP and SL inside one bar", GroupName = "Take Profit / Stop Loss", Order = 204,
			Description = "On historical bars the order of the high and the low is unknown. Live bars are resolved tick by tick.")]
		public SameBarHitRule SameBarRule
		{
			get => _sameBarRule;
			set { _sameBarRule = value; RecalculateValues(); }
		}

		[Display(Name = "Probability smoothing (virtual trades)", GroupName = "Take Profit / Stop Loss", Order = 205,
			Description = "How many trades' worth of weight the parent group gets. Higher = steadier probabilities that need more history to move.")]
		[Range(1, 1000)]
		public int ProbabilitySmoothing
		{
			get => _probabilitySmoothing;
			set { _probabilitySmoothing = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Show signal labels", GroupName = "Display", Order = 300)]
		public bool ShowSignalLabels { get; set; } = true;

		[Display(Name = "Show TP / SL levels", GroupName = "Display", Order = 301)]
		public bool ShowTradeLevels { get; set; } = true;

		[Display(Name = "Show statistics panel", GroupName = "Display", Order = 302)]
		public bool ShowStatsPanel { get; set; } = true;

		[Display(Name = "Statistics panel position", GroupName = "Display", Order = 303)]
		public PanelCorner StatsPanelLocation { get; set; } = PanelCorner.TopRight;

		[Display(Name = "Label offset (px)", GroupName = "Display", Order = 304,
			Description = "Distance between the tip of the signal arrow and its label, so the label clears the arrow.")]
		[Range(0, 500)]
		public int LabelOffset { get; set; } = 24;

		[Display(Name = "Label font", GroupName = "Display", Order = 305)]
		public FontSetting LabelFont { get; set; } = new FontSetting("Arial", 9);

		[Display(Name = "Take profit line", GroupName = "Display", Order = 306)]
		public PenSettings TakeProfitPen { get; set; } = new PenSettings { Color = Color.FromArgb(255, 0, 200, 83).Convert(), Width = 1 };

		[Display(Name = "Stop loss line", GroupName = "Display", Order = 307)]
		public PenSettings StopLossPen { get; set; } = new PenSettings { Color = Color.FromArgb(255, 255, 45, 85).Convert(), Width = 1 };

		[Display(Name = "Alert on new signal", GroupName = "Alerts", Order = 400)]
		public bool UseAlerts { get; set; }

		[Display(Name = "Alert when TP / SL is hit", GroupName = "Alerts", Order = 401)]
		public bool AlertOnTradeResult { get; set; }

		[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 402)]
		public string AlertFile { get; set; } = "alert1";

		#endregion

		#region ctor

		public FvgReactionLiquiditySweep() : base(true)
		{
			DenyToChangePanel = true;

			// Required for OnRender to be invoked at all. Final = drawn on every chart
			// render (new ticks, scrolling, mouse moves), which the hover tooltip needs.
			EnableCustomDrawing = true;
			SubscribeToDrawingEvents(DrawingLayouts.Final);

			// hide the base (invisible) data series that Indicator ships with
			((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
			DataSeries[0].IsHidden = true;

			DataSeries.Add(_bullReaction);
			DataSeries.Add(_bearReaction);
			DataSeries.Add(_bullSweep);
			DataSeries.Add(_bearSweep);
			DataSeries.Add(_buySignal);
			DataSeries.Add(_shortSignal);
		}

		#endregion

		#region Calculation

		protected override void OnRecalculate()
		{
			lock (_sync)
				ResetState();
		}

		protected override void OnCalculate(int bar, decimal value)
		{
			List<PendingAlert> alerts = null;

			lock (_sync)
			{
				if (bar == 0)
					ResetState();

				// a closed bar is final - ATAS only repeats the last (forming) one, but if an
				// older bar is ever sent again it must not clear that bar's signal
				if (bar <= _lastClosedBar)
					return;

				// Everything that decides a signal runs exactly once per bar, on the CLOSED
				// bar: when the first tick of a new bar arrives, the previous one is final.
				// Running it on every tick of the forming bar (as before) added duplicate
				// FVG zones and painted arrows that could still change by the close.
				for (var closedBar = _lastClosedBar + 1; closedBar < bar; closedBar++)
				{
					ProcessClosedBar(closedBar, ref alerts);
					_lastClosedBar = closedBar;
				}

				var candle = GetCandle(bar);
				_lastPrice = candle.Close;

				// the forming bar never carries a signal
				ClearMarkers(bar);

				// A TP/SL touch is final the moment it trades (a bar's high/low can only
				// extend), so open trades are resolved tick by tick on the forming bar.
				ResolveOpenTrades(bar, candle, ref alerts);

				// Absorption is read straight off this bar's own footprint data, so the
				// heatmap keeps updating live as the bar forms.
				UpdateAbsorption(bar, candle);

				// History is done once the last bar has been calculated; from here on
				// signals and TP/SL hits happen in real time and may raise alerts.
				if (bar == CurrentBar - 1)
					_realtime = true;
			}

			if (alerts != null)
				FireAlerts(alerts);
		}

		private void ResetState()
		{
			_zones.Clear();
			_absorptionByBar.Clear();
			_trades.Clear();
			_openTrades.Clear();
			_ema.Clear();
			_model.Reset();

			_lastClosedBar = -1;
			_lastLowSweepBar = -1;
			_lastHighSweepBar = -1;
			_lastLongSignalBar = -1;
			_lastShortSignalBar = -1;
			_realtime = false;
			_lastPrice = 0;

			_longWins = 0;
			_longLosses = 0;
			_shortWins = 0;
			_shortLosses = 0;
			_expired = 0;
			_filtered = 0;
			_longNetTicks = 0;
			_shortNetTicks = 0;
			_highOddsWins = 0;
			_highOddsCount = 0;
			_lowOddsWins = 0;
			_lowOddsCount = 0;
		}

		private void ProcessClosedBar(int bar, ref List<PendingAlert> alerts)
		{
			var candle = GetCandle(bar);

			ClearMarkers(bar);

			// final footprint of the bar - the last intrabar update can be a tick behind
			UpdateAbsorption(bar, candle);
			PruneOldAbsorption(bar);

			ResolveOpenTrades(bar, candle, ref alerts);
			ExpireStaleTrades(bar, candle, ref alerts);
			UpdateEma(bar, candle.Close);

			// bar + 1 exists (that's why this bar is closed), so we already know whether
			// it opens a new session
			var sessionEnds = ExpireAtSessionEnd && IsNewSession(bar + 1);

			if (sessionEnds)
				ExpireAllOpenTrades(bar, candle, ref alerts);

			if (bar < SwingLookback + 3)
				return;

			DetectNewFvg(bar);
			CheckReactions(bar, candle, out var bullReaction, out var bearReaction);
			CheckLiquiditySweep(bar, candle, out var sweptLows, out var sweptHighs);
			PruneOldZones(bar);

			if (sweptLows)
				_lastLowSweepBar = bar;

			if (sweptHighs)
				_lastHighSweepBar = bar;

			// no new trades on the last bar of a session when trades close at session end
			if (!sessionEnds)
				GenerateSignals(bar, candle, bullReaction, bearReaction, sweptLows, sweptHighs, ref alerts);
		}

		private void ClearMarkers(int bar)
		{
			_bullReaction[bar] = 0;
			_bearReaction[bar] = 0;
			_bullSweep[bar] = 0;
			_bearSweep[bar] = 0;
			_buySignal[bar] = 0;
			_shortSignal[bar] = 0;
		}

		// Absorption = a price level within this bar traded unusually high volume
		// (relative to the bar's own other levels) with one side (Bid or Ask)
		// clearly dominant, i.e. aggressive flow that a passive counterparty kept
		// soaking up at that price. This reads directly from ATAS's footprint/
		// cluster data (GetAllPriceLevels), so it needs a feed that provides that
		// (Rithmic order-flow data, same as your footprint charts already use).
		private void UpdateAbsorption(int bar, IndicatorCandle candle)
		{
			var levels = candle.GetAllPriceLevels()?.ToList();

			if (levels == null || levels.Count == 0)
			{
				_absorptionByBar.Remove(bar);
				return;
			}

			var avgVolume = levels.Average(l => l.Volume);

			if (avgVolume <= 0)
			{
				_absorptionByBar.Remove(bar);
				return;
			}

			var volumeThreshold = Math.Max(AbsorptionMinVolume, (decimal)AbsorptionVolumeMultiplier * avgVolume);
			var dominance = (decimal)AbsorptionDominanceRatio;

			var flagged = levels
				.Where(l => l.Volume >= volumeThreshold)
				.Where(l => l.Volume > 0 && Math.Max(l.Bid, l.Ask) / l.Volume >= dominance)
				.Select(l => new AbsorptionCell(l.Price, l.Volume, l.Ask >= l.Bid))
				.ToArray();

			if (flagged.Length > 0)
				_absorptionByBar[bar] = new AbsorptionBar { High = candle.High, Low = candle.Low, Cells = flagged };
			else
				_absorptionByBar.Remove(bar);
		}

		private void PruneOldAbsorption(int bar)
		{
			var cutoff = bar - AbsorptionMaxAgeBars;
			var staleBars = _absorptionByBar.Keys.Where(b => b < cutoff).ToList();

			foreach (var staleBar in staleBars)
				_absorptionByBar.Remove(staleBar);
		}

		// Absorption on the side that supports the trade: for a buy, heavy one-sided
		// SELLING (bid-dominant) soaked up near the low of the signal bar or the bar
		// before it; for a short, heavy BUYING (ask-dominant) absorbed near the high.
		private bool HasSupportiveAbsorption(int bar, bool isLong)
		{
			for (var b = bar; b >= Math.Max(0, bar - 1); b--)
			{
				if (!_absorptionByBar.TryGetValue(b, out var info))
					continue;

				var edge = (info.High - info.Low) * AbsorptionEdgeFraction;

				foreach (var cell in info.Cells)
				{
					if (isLong && !cell.AskDominant && cell.Price <= info.Low + edge)
						return true;

					if (!isLong && cell.AskDominant && cell.Price >= info.High - edge)
						return true;
				}
			}

			return false;
		}

		private void UpdateEma(int bar, decimal close)
		{
			while (_ema.Count <= bar)
				_ema.Add(close);

			if (bar == 0 || TrendEmaPeriod <= 0)
			{
				_ema[bar] = close;
				return;
			}

			var alpha = 2m / (TrendEmaPeriod + 1);
			_ema[bar] = _ema[bar - 1] + alpha * (close - _ema[bar - 1]);
		}

		// Classic 3-candle imbalance: gap between candle[bar-2] and candle[bar],
		// created by the large range of the middle candle[bar-1].
		private void DetectNewFvg(int bar)
		{
			var left = GetCandle(bar - 2);
			var right = GetCandle(bar);

			// at least one tick of empty space, even with Min FVG Size = 0
			var minGap = Math.Max(MinFvgTicks, 1) * TickSize;

			// Bullish FVG: left.High sits below right.Low
			if (right.Low - left.High >= minGap)
			{
				_zones.Add(new FvgZone
				{
					StartBar = bar - 1,
					ConfirmedBar = bar,
					Top = right.Low,
					Bottom = left.High,
					IsBullish = true
				});
			}

			// Bearish FVG: left.Low sits above right.High
			if (left.Low - right.High >= minGap)
			{
				_zones.Add(new FvgZone
				{
					StartBar = bar - 1,
					ConfirmedBar = bar,
					Top = left.Low,
					Bottom = right.High,
					IsBullish = false
				});
			}
		}

		// A "good reaction" = price trades back into an unfilled zone and rejects:
		// closes back beyond the zone in the direction the gap originally implied.
		private void CheckReactions(int bar, IndicatorCandle candle, out bool bullReaction, out bool bearReaction)
		{
			bullReaction = false;
			bearReaction = false;

			foreach (var zone in _zones)
			{
				// The candle that completes the gap sits right on its edge, so it would
				// always look like a "touch and reject" - retests start on the next bar.
				if (zone.Filled || bar <= zone.ConfirmedBar)
					continue;

				if (zone.IsBullish)
				{
					if (!zone.ReactionMarked)
					{
						var touched = candle.Low <= zone.Top && candle.Low >= zone.Bottom;
						var rejected = RequireCloseThroughZone
							? candle.Close > zone.Top
							: candle.Close >= zone.Bottom;

						if (touched && rejected && candle.Close > candle.Open)
						{
							bullReaction = true;
							zone.ReactionMarked = true;
							continue;
						}
					}

					// traded straight through -> zone no longer valid (also after a reaction)
					if (candle.Close < zone.Bottom)
						zone.Filled = true;
				}
				else
				{
					if (!zone.ReactionMarked)
					{
						var touched = candle.High >= zone.Bottom && candle.High <= zone.Top;
						var rejected = RequireCloseThroughZone
							? candle.Close < zone.Bottom
							: candle.Close <= zone.Top;

						if (touched && rejected && candle.Close < candle.Open)
						{
							bearReaction = true;
							zone.ReactionMarked = true;
							continue;
						}
					}

					if (candle.Close > zone.Top)
						zone.Filled = true;
				}
			}

			var tickSize = TickSize;

			if (bullReaction)
				_bullReaction[bar] = candle.Low - ArrowOffsetTicks * tickSize;

			if (bearReaction)
				_bearReaction[bar] = candle.High + ArrowOffsetTicks * tickSize;
		}

		// Liquidity sweep = current bar wicks beyond the prior N-bar high/low,
		// then closes back inside it -> stop run / liquidity grab.
		private void CheckLiquiditySweep(int bar, IndicatorCandle candle, out bool sweptLows, out bool sweptHighs)
		{
			var tickSize = TickSize;

			var highestHigh = decimal.MinValue;
			var lowestLow = decimal.MaxValue;

			for (var i = Math.Max(0, bar - SwingLookback); i < bar; i++)
			{
				var c = GetCandle(i);

				if (c.High > highestHigh)
					highestHigh = c.High;

				if (c.Low < lowestLow)
					lowestLow = c.Low;
			}

			// Swept the highs (sell-side reaction expected)
			sweptHighs = candle.High > highestHigh && candle.Close < highestHigh;

			// Swept the lows (buy-side reaction expected)
			sweptLows = candle.Low < lowestLow && candle.Close > lowestLow;

			if (sweptHighs)
				_bearSweep[bar] = candle.High + ArrowOffsetTicks * tickSize;

			if (sweptLows)
				_bullSweep[bar] = candle.Low - ArrowOffsetTicks * tickSize;
		}

		private void PruneOldZones(int bar)
		{
			_zones.RemoveAll(z => z.Filled || bar - z.StartBar > MaxZoneAgeBars);
		}

		#endregion

		#region Signals and TP / SL tracking

		private void GenerateSignals(int bar, IndicatorCandle candle, bool bullReaction, bool bearReaction,
			bool sweptLows, bool sweptHighs, ref List<PendingAlert> alerts)
		{
			var longTrade = EnableBuySignals ? TryBuildSignal(bar, candle, true, bullReaction, sweptLows) : null;
			var shortTrade = EnableShortSignals ? TryBuildSignal(bar, candle, false, bearReaction, sweptHighs) : null;

			var candidates = new List<SignalTrade>(2);

			if (longTrade != null)
				candidates.Add(longTrade);

			if (shortTrade != null)
				candidates.Add(shortTrade);

			if (candidates.Count == 0)
				return;

			var canShow = !OneTradeAtATime || !_openTrades.Any(t => t.IsShown);

			// An outside bar can trigger both sides at once. With one position at a time
			// only the side with the better odds is shown - neither on an exact tie.
			if (OneTradeAtATime && candidates.Count == 2)
			{
				var edge = longTrade.Estimate.TakeProfit - shortTrade.Estimate.TakeProfit;

				if (Math.Abs(edge) < 1e-9)
					canShow = false;
				else if (edge < 0)
					candidates.Reverse();
			}

			foreach (var trade in candidates)
			{
				// compared as labelled, so a signal shown as "60%" passes a 60% filter
				trade.IsShown = canShow && Percent(trade.Estimate.TakeProfit) >= MinProbabilityPercent;

				if (trade.IsShown && OneTradeAtATime)
					canShow = false;

				RegisterSignal(trade, candle, ref alerts);
			}
		}

		private SignalTrade TryBuildSignal(int bar, IndicatorCandle candle, bool isLong, bool fvgReaction, bool sweepNow)
		{
			var lastSweep = isLong ? _lastLowSweepBar : _lastHighSweepBar;
			var sweepBeforeReaction = lastSweep >= 0 && bar - lastSweep <= ConfluenceBars;

			TriggerType trigger;

			if (fvgReaction)
				trigger = sweepBeforeReaction ? TriggerType.SweepThenFvg : TriggerType.Fvg;
			else if (sweepNow)
				trigger = TriggerType.Sweep;
			else
				return null;

			switch (SignalSource)
			{
				case SignalMode.FvgReactionOnly when !fvgReaction:
				case SignalMode.LiquiditySweepOnly when !sweepNow:
				case SignalMode.SweepThenFvg when trigger != TriggerType.SweepThenFvg:
					return null;
			}

			// don't count the same move twice
			var lastSignal = isLong ? _lastLongSignalBar : _lastShortSignalBar;

			if (lastSignal >= 0 && bar - lastSignal <= SignalCooldownBars)
				return null;

			var absorption = HasSupportiveAbsorption(bar, isLong);
			var withTrend = TrendEmaPeriod > 0 && bar >= TrendEmaPeriod
				&& (isLong ? candle.Close > _ema[bar] : candle.Close < _ema[bar]);
			var deltaConfirms = isLong ? candle.Delta > 0 : candle.Delta < 0;

			if ((OnlyWithTrend && !withTrend)
				|| (RequireDeltaConfirmation && !deltaConfirms)
				|| (RequireAbsorption && !absorption))
				return null;

			var confirmations = (absorption ? 1 : 0) + (withTrend ? 1 : 0) + (deltaConfirms ? 1 : 0);
			var tickSize = TickSize;
			var entry = candle.Close;
			var direction = isLong ? 1 : -1;

			return new SignalTrade
			{
				EntryBar = bar,
				EntryTime = candle.Time,
				IsLong = isLong,
				EntryPrice = entry,
				TakeProfitPrice = entry + direction * TakeProfitTicks * tickSize,
				StopLossPrice = entry - direction * StopLossTicks * tickSize,
				SignalHigh = candle.High,
				SignalLow = candle.Low,
				Trigger = trigger,
				HasAbsorption = absorption,
				WithTrend = withTrend,
				DeltaConfirms = deltaConfirms,
				Confirmations = confirmations,
				Estimate = _model.Estimate(isLong, trigger, confirmations, TakeProfitPrior, ProbabilitySmoothing)
			};
		}

		private void RegisterSignal(SignalTrade trade, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			var bar = trade.EntryBar;

			_trades.Add(trade);
			_openTrades.Add(trade);

			if (trade.IsLong)
				_lastLongSignalBar = bar;
			else
				_lastShortSignalBar = bar;

			if (!trade.IsShown)
			{
				_filtered++;
				return;
			}

			var tickSize = TickSize;

			// the signal arrow takes the place of the trigger arrows it was built from
			if (trade.IsLong)
			{
				_bullReaction[bar] = 0;
				_bullSweep[bar] = 0;
				_buySignal[bar] = candle.Low - ArrowOffsetTicks * tickSize;
			}
			else
			{
				_bearReaction[bar] = 0;
				_bearSweep[bar] = 0;
				_shortSignal[bar] = candle.High + ArrowOffsetTicks * tickSize;
			}

			if (_realtime && UseAlerts)
			{
				var pTp = Percent(trade.Estimate.TakeProfit);
				var message = $"{Side(trade)} @ {FormatPrice(trade.EntryPrice)} ({TriggerLabel(trade.Trigger)}): "
					+ $"TP {pTp}% / SL {100 - pTp}%  -  TP {FormatPrice(trade.TakeProfitPrice)}, SL {FormatPrice(trade.StopLossPrice)}";

				QueueAlert(ref alerts, message, trade.IsLong ? BuyColor : ShortColor);
			}
		}

		private void ResolveOpenTrades(int bar, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			for (var i = _openTrades.Count - 1; i >= 0; i--)
			{
				var trade = _openTrades[i];

				// entry is the signal bar's close, so the first bar that can hit TP/SL is the next one
				if (bar <= trade.EntryBar)
					continue;

				var outcome = EvaluateBar(trade.IsLong, trade.TakeProfitPrice, trade.StopLossPrice,
					candle.Open, candle.High, candle.Low, candle.Close, SameBarRule, out var ambiguous);

				if (outcome == TradeOutcome.Open)
					continue;

				trade.AmbiguousExit = ambiguous;

				var exitPrice = outcome == TradeOutcome.TakeProfit ? trade.TakeProfitPrice : trade.StopLossPrice;
				CloseTrade(trade, outcome, bar, exitPrice, ref alerts);
				_openTrades.RemoveAt(i);
			}
		}

		private void ExpireStaleTrades(int bar, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			if (MaxBarsInTrade <= 0)
				return;

			for (var i = _openTrades.Count - 1; i >= 0; i--)
			{
				var trade = _openTrades[i];

				if (bar - trade.EntryBar < MaxBarsInTrade)
					continue;

				CloseTrade(trade, TradeOutcome.Expired, bar, candle.Close, ref alerts);
				_openTrades.RemoveAt(i);
			}
		}

		private void ExpireAllOpenTrades(int bar, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			foreach (var trade in _openTrades)
				CloseTrade(trade, TradeOutcome.Expired, bar, candle.Close, ref alerts);

			_openTrades.Clear();
		}

		private void CloseTrade(SignalTrade trade, TradeOutcome outcome, int bar, decimal exitPrice, ref List<PendingAlert> alerts)
		{
			trade.Outcome = outcome;
			trade.ExitBar = bar;
			trade.ExitPrice = exitPrice;

			// only a real TP / SL teaches the model - an expired trade hit neither
			if (outcome != TradeOutcome.Expired)
			{
				var hitTakeProfit = outcome == TradeOutcome.TakeProfit;
				var labelled = Percent(trade.Estimate.TakeProfit);

				_model.Record(trade.IsLong, trade.Trigger, trade.Confirmations, hitTakeProfit);

				if (labelled >= HighOddsPercent)
				{
					_highOddsCount++;
					_highOddsWins += hitTakeProfit ? 1 : 0;
				}
				else if (labelled <= LowOddsPercent)
				{
					_lowOddsCount++;
					_lowOddsWins += hitTakeProfit ? 1 : 0;
				}
			}

			if (!trade.IsShown)
				return;

			var ticks = ResultTicks(trade);

			if (trade.IsLong)
				_longNetTicks += ticks;
			else
				_shortNetTicks += ticks;

			switch (outcome)
			{
				case TradeOutcome.TakeProfit:
					if (trade.IsLong)
						_longWins++;
					else
						_shortWins++;
					break;

				case TradeOutcome.StopLoss:
					if (trade.IsLong)
						_longLosses++;
					else
						_shortLosses++;
					break;

				default:
					_expired++;
					break;
			}

			if (_realtime && AlertOnTradeResult)
			{
				var message = $"{Side(trade)} from {FormatPrice(trade.EntryPrice)}: {ResultText(trade)}";
				QueueAlert(ref alerts, message, OutcomeColor(outcome));
			}
		}

		// Which bracket level a bar hits. A bar that spans both levels is ambiguous on
		// historical data (we only know O/H/L/C, not their order) unless the open
		// already gapped through one of them.
		private static TradeOutcome EvaluateBar(bool isLong, decimal takeProfit, decimal stopLoss,
			decimal open, decimal high, decimal low, decimal close, SameBarHitRule rule, out bool ambiguous)
		{
			ambiguous = false;

			var tpHit = isLong ? high >= takeProfit : low <= takeProfit;
			var slHit = isLong ? low <= stopLoss : high >= stopLoss;

			if (!tpHit && !slHit)
				return TradeOutcome.Open;

			if (tpHit && slHit)
			{
				if (isLong ? open >= takeProfit : open <= takeProfit)
					return TradeOutcome.TakeProfit;

				if (isLong ? open <= stopLoss : open >= stopLoss)
					return TradeOutcome.StopLoss;

				ambiguous = true;

				if (rule == SameBarHitRule.StopLossFirst)
					return TradeOutcome.StopLoss;

				// a bullish bar is assumed to trade O-L-H-C, a bearish one O-H-L-C
				var lowFirst = close >= open;

				if (isLong)
					return lowFirst ? TradeOutcome.StopLoss : TradeOutcome.TakeProfit;

				return lowFirst ? TradeOutcome.TakeProfit : TradeOutcome.StopLoss;
			}

			return tpHit ? TradeOutcome.TakeProfit : TradeOutcome.StopLoss;
		}

		// Probability of reaching +takeProfit before -stopLoss (both in ticks) from the
		// current excursion, for a random walk with the drift implied by the entry
		// probability. theta = 2 * drift / variance; theta = 0 is the driftless walk,
		// where P = (excursion + SL) / (TP + SL).
		private static double LiveProbability(double entryProbability, double takeProfit, double stopLoss, double excursion)
		{
			if (excursion >= takeProfit)
				return 1;

			if (excursion <= -stopLoss)
				return 0;

			var target = Math.Min(0.999, Math.Max(0.001, entryProbability));

			// keep theta * distance small enough for Math.Exp to stay finite
			var bound = Math.Min(40 / Math.Min(takeProfit, stopLoss), 600 / Math.Max(takeProfit, stopLoss));
			var lo = -bound;
			var hi = bound;

			for (var i = 0; i < 100; i++)
			{
				var mid = (lo + hi) / 2;

				if (HitProbability(mid, 0, takeProfit, stopLoss) < target)
					lo = mid;
				else
					hi = mid;
			}

			return HitProbability((lo + hi) / 2, excursion, takeProfit, stopLoss);
		}

		private static double HitProbability(double theta, double excursion, double takeProfit, double stopLoss)
		{
			if (Math.Abs(theta) < 1e-12)
				return (excursion + stopLoss) / (takeProfit + stopLoss);

			var p = (Math.Exp(-theta * excursion) - Math.Exp(theta * stopLoss))
				/ (Math.Exp(-theta * takeProfit) - Math.Exp(theta * stopLoss));

			return Math.Min(1, Math.Max(0, p));
		}

		private void QueueAlert(ref List<PendingAlert> alerts, string message, Color background)
		{
			if (alerts == null)
				alerts = new List<PendingAlert>();

			alerts.Add(new PendingAlert(message, background));
		}

		// raised outside the lock, after the calculation step is done
		private void FireAlerts(List<PendingAlert> alerts)
		{
			var instrument = InstrumentInfo?.Instrument ?? string.Empty;

			foreach (var alert in alerts)
				AddAlert(AlertFile, instrument, alert.Message, alert.Background.Convert(), Color.White.Convert());
		}

		#endregion

		#region Rendering

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			if (ChartInfo == null || InstrumentInfo == null)
				return;

			var firstBar = FirstVisibleBarNumber;
			var lastBar = LastVisibleBarNumber;

			List<FvgZone> zones = null;
			List<KeyValuePair<int, AbsorptionBar>> absorption = null;
			List<SignalTrade> trades;
			PanelStats stats = null;

			// copy what's visible under the lock (OnCalculate may be mid-update on its
			// own thread), then draw from the copies
			lock (_sync)
			{
				if (DrawZones)
					zones = _zones.Where(z => !z.Filled && z.StartBar <= lastBar).ToList();

				if (ShowAbsorptionHeatmap)
					absorption = _absorptionByBar.Where(kv => kv.Key >= firstBar && kv.Key <= lastBar).ToList();

				trades = _trades
					.Where(t => t.IsShown && t.EntryBar <= lastBar && (t.Outcome == TradeOutcome.Open || t.ExitBar >= firstBar))
					.Select(t => t.Clone())
					.ToList();

				if (ShowStatsPanel)
					stats = BuildPanelStats();
			}

			if (zones != null)
				RenderFvgZones(context, zones);

			if (absorption != null)
				RenderAbsorptionHeatmap(context, absorption);

			if (ShowTradeLevels)
				RenderTradeLevels(context, trades, firstBar, lastBar);

			var hovered = ShowSignalLabels ? RenderSignalLabels(context, trades, firstBar, lastBar) : null;

			if (stats != null)
				RenderStatsPanel(context, stats);

			if (hovered != null)
				RenderTooltip(context, hovered);
		}

		private PanelStats BuildPanelStats()
		{
			var openTrade = _openTrades.Where(t => t.IsShown).OrderByDescending(t => t.EntryBar).FirstOrDefault();

			return new PanelStats
			{
				LongWins = _longWins,
				LongLosses = _longLosses,
				ShortWins = _shortWins,
				ShortLosses = _shortLosses,
				Open = _openTrades.Count(t => t.IsShown),
				Expired = _expired,
				Filtered = _filtered,
				ModelResolved = _model.Resolved,
				LongNetTicks = _longNetTicks,
				ShortNetTicks = _shortNetTicks,
				HighOddsWins = _highOddsWins,
				HighOddsCount = _highOddsCount,
				LowOddsWins = _lowOddsWins,
				LowOddsCount = _lowOddsCount,
				OpenTrade = openTrade?.Clone(),
				LastPrice = _lastPrice
			};
		}

		private void RenderFvgZones(RenderContext context, List<FvgZone> zones)
		{
			// in cluster mode each price is a row: start below the edge candle's own row
			var isClusterMode = ChartInfo.ChartVisualMode == ChartVisualModes.Clusters;
			var rowHeight = isClusterMode ? (int)ChartInfo.PriceChartContainer.PriceRowHeight : 0;

			// an unfilled zone is still live, so it projects to the right edge
			var right = ChartInfo.Region.Width;

			var bullPen = new RenderPen(WithAlpha(BullishZoneColor, 140));
			var bearPen = new RenderPen(WithAlpha(BearishZoneColor, 140));

			foreach (var zone in zones)
			{
				var x1 = ChartInfo.GetXByBar(zone.StartBar);
				var yTop = ChartInfo.GetYByPrice(zone.Top, isClusterMode) + rowHeight;
				var yBottom = ChartInfo.GetYByPrice(zone.Bottom, isClusterMode);

				if (right <= x1 || yBottom <= yTop)
					continue;

				var rect = new Rectangle(x1, yTop, right - x1, yBottom - yTop);

				context.FillRectangle(zone.IsBullish ? BullishZoneColor : BearishZoneColor, rect);
				context.DrawRectangle(zone.IsBullish ? bullPen : bearPen, rect);
			}
		}

		// Paints one cell per flagged absorption price level: cell height = one
		// price row, cell width = that bar's column. Opacity scales with volume
		// (relative to the strongest flagged level currently on screen); color
		// marks which side was absorbed - orange where aggressive buying got
		// soaked up (often resistance), blue where aggressive selling got soaked
		// up (often support).
		private void RenderAbsorptionHeatmap(RenderContext context, List<KeyValuePair<int, AbsorptionBar>> bars)
		{
			var maxVolume = 0m;

			foreach (var kvp in bars)
			{
				foreach (var cell in kvp.Value.Cells)
				{
					if (cell.Volume > maxVolume)
						maxVolume = cell.Volume;
				}
			}

			if (maxVolume <= 0)
				return;

			var tickSize = TickSize;

			foreach (var kvp in bars)
			{
				// the whole bar column, from this bar's start to the next bar's start
				var x1 = ChartInfo.GetXByBar(kvp.Key);
				var width = ChartInfo.GetXByBar(kvp.Key + 1) - x1;

				if (width <= 0)
					width = Math.Max(1, (int)ChartInfo.PriceChartContainer.BarsWidth);

				foreach (var cell in kvp.Value.Cells)
				{
					// one price row: from this level's start down to the next level's start,
					// kept a few pixels tall so it stays visible on a zoomed-out candle chart
					var yTop = ChartInfo.GetYByPrice(cell.Price, true);
					var height = ChartInfo.GetYByPrice(cell.Price - tickSize, true) - yTop;

					if (height < MinHeatmapCellHeight)
					{
						yTop -= (MinHeatmapCellHeight - height) / 2;
						height = MinHeatmapCellHeight;
					}

					var intensity = (double)(cell.Volume / maxVolume);
					var alpha = (int)Math.Clamp(60 + intensity * 150, 60, 210);

					var fill = cell.AskDominant
						? Color.FromArgb(alpha, 255, 120, 0)
						: Color.FromArgb(alpha, 0, 160, 255);

					context.FillRectangle(fill, new Rectangle(x1, yTop, width, height));
				}
			}
		}

		// Long/short-position style boxes: entry -> TP shaded green, entry -> SL red,
		// running from the signal bar to the bar that settled the trade.
		private void RenderTradeLevels(RenderContext context, List<SignalTrade> trades, int firstBar, int lastBar)
		{
			var lastIndex = CurrentBar - 1;
			var tpColor = TakeProfitPen.Color.Convert();
			var slColor = StopLossPen.Color.Convert();
			var tpFill = WithAlpha(tpColor, 38);
			var slFill = WithAlpha(slColor, 38);
			var entryPen = new RenderPen(Color.Gray, 1) { DashStyle = DashStyle.Dash };
			var font = LabelFont.RenderObject;

			foreach (var trade in trades)
			{
				var endBar = trade.Outcome == TradeOutcome.Open ? lastIndex : trade.ExitBar;

				if (trade.EntryBar > lastBar || endBar < firstBar)
					continue;

				var x1 = ChartInfo.GetXByBar(trade.EntryBar, false);
				var x2 = Math.Max(ChartInfo.GetXByBar(endBar, false), x1 + 4);
				var yEntry = ChartInfo.GetYByPrice(trade.EntryPrice, false);
				var yTp = ChartInfo.GetYByPrice(trade.TakeProfitPrice, false);
				var ySl = ChartInfo.GetYByPrice(trade.StopLossPrice, false);

				context.FillRectangle(tpFill, VerticalSpan(x1, x2, yEntry, yTp));
				context.FillRectangle(slFill, VerticalSpan(x1, x2, yEntry, ySl));
				context.DrawLine(TakeProfitPen.RenderObject, x1, yTp, x2, yTp);
				context.DrawLine(StopLossPen.RenderObject, x1, ySl, x2, ySl);
				context.DrawLine(entryPen, x1, yEntry, x2, yEntry);

				if (trade.Outcome != TradeOutcome.Open)
					continue;

				// live trade: price tags at the end of its TP / SL lines
				DrawPriceTag(context, $"TP {FormatPrice(trade.TakeProfitPrice)}", x2 + 4, yTp, tpColor, font);
				DrawPriceTag(context, $"SL {FormatPrice(trade.StopLossPrice)}", x2 + 4, ySl, slColor, font);
			}
		}

		// Two-line label under a buy / above a short:
		//   BUY  TP 62% | SL 38%          [OPEN / TP / SL / EXP]
		//   Sweep+FVG | conf 2/3 | n=14
		// n = past signals with exactly this setup. Returns the label under the mouse.
		private SignalTrade RenderSignalLabels(RenderContext context, List<SignalTrade> trades, int firstBar, int lastBar)
		{
			const int pad = 4;
			const int stripe = 3;

			var font = LabelFont.RenderObject;
			var mouse = MouseLocationInfo.LastPosition;
			var arrowOffset = ArrowOffsetTicks * TickSize;
			var region = ChartInfo.PriceChartContainer.Region;
			var placed = new List<Rectangle>();
			var background = Color.FromArgb(215, 24, 26, 32);
			SignalTrade hovered = null;

			foreach (var trade in trades.OrderBy(t => t.EntryBar))
			{
				if (trade.EntryBar < firstBar || trade.EntryBar > lastBar)
					continue;

				var pTp = Percent(trade.Estimate.TakeProfit);
				var line1 = $"{Side(trade)}  TP {pTp}% | SL {100 - pTp}%";
				var line2 = $"{TriggerLabel(trade.Trigger)} | conf {trade.Confirmations}/{MaxConfirmations} | n={trade.Estimate.SetupCount}";
				var badge = OutcomeBadge(trade.Outcome);

				var size1 = context.MeasureString(line1, font);
				var size2 = context.MeasureString(line2, font);
				var badgeSize = context.MeasureString(badge, font);

				var badgeWidth = badgeSize.Width + pad * 2;
				var width = stripe + pad + Math.Max(size1.Width, size2.Width) + pad + badgeWidth;
				var height = size1.Height + size2.Height + pad * 2;

				// centred on the bar but kept inside the chart, measured vertically from the
				// arrow tip (which moves away from the bar as you zoom in)
				var x = ChartInfo.GetXByBar(trade.EntryBar, false) - width / 2;
				x = Math.Max(region.X, Math.Min(x, region.X + region.Width - width));
				var y = trade.IsLong
					? ChartInfo.GetYByPrice(trade.SignalLow - arrowOffset, false) + LabelOffset
					: ChartInfo.GetYByPrice(trade.SignalHigh + arrowOffset, false) - LabelOffset - height;

				var rect = new Rectangle(x, y, width, height);

				// step away from labels already drawn this frame
				for (var attempt = 0; attempt < 20 && placed.Any(r => r.IntersectsWith(rect)); attempt++)
					rect.Y += trade.IsLong ? height + 2 : -(height + 2);

				placed.Add(rect);

				context.FillRectangle(background, rect);
				context.FillRectangle(trade.IsLong ? BuyColor : ShortColor, new Rectangle(rect.X, rect.Y, stripe, rect.Height));
				context.DrawString(line1, font, Color.White, rect.X + stripe + pad, rect.Y + pad);
				context.DrawString(line2, font, Color.Silver, rect.X + stripe + pad, rect.Y + pad + size1.Height);

				var badgeRect = new Rectangle(rect.Right - badgeWidth, rect.Y, badgeWidth, rect.Height);
				context.FillRectangle(OutcomeColor(trade.Outcome), badgeRect);
				context.DrawString(badge, font, Color.White, badgeRect.X + pad, badgeRect.Y + (badgeRect.Height - badgeSize.Height) / 2);

				if (rect.Contains(mouse))
					hovered = trade;
			}

			return hovered;
		}

		private void RenderStatsPanel(RenderContext context, PanelStats stats)
		{
			var lines = new List<(string Text, Color Color)>
			{
				($"FVG / Sweep signals   TP {TakeProfitTicks}t | SL {StopLossTicks}t", Color.White),
				(StatsLine("Longs ", stats.LongWins, stats.LongLosses, stats.LongNetTicks), BuyColor),
				(StatsLine("Shorts", stats.ShortWins, stats.ShortLosses, stats.ShortNetTicks), ShortColor),
				(StatsLine("Total ", stats.LongWins + stats.ShortWins, stats.LongLosses + stats.ShortLosses,
					stats.LongNetTicks + stats.ShortNetTicks), Color.White),
				($"Open {stats.Open}   Expired {stats.Expired}   Hidden {stats.Filtered}   Model n={stats.ModelResolved}", Color.Silver),

				// were the labels right? (all settled signals, including filtered ones)
				($"Labelled >={HighOddsPercent}%: {TrackRecord(stats.HighOddsWins, stats.HighOddsCount)}   "
					+ $"<={LowOddsPercent}%: {TrackRecord(stats.LowOddsWins, stats.LowOddsCount)}", Color.Silver)
			};

			var open = stats.OpenTrade;

			if (open != null)
			{
				// how far the open trade has moved, and the odds from here
				var excursion = (stats.LastPrice - open.EntryPrice) / TickSize * (open.IsLong ? 1 : -1);
				var live = Percent(LiveProbability(open.Estimate.TakeProfit, TakeProfitTicks, StopLossTicks, (double)excursion));

				lines.Add(($"Live {Side(open)} {excursion.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t:  TP {live}% | SL {100 - live}%",
					open.IsLong ? BuyColor : ShortColor));
			}

			var region = ChartInfo.PriceChartContainer.Region;
			var size = MeasureLines(context, lines);
			const int margin = 10;

			var left = StatsPanelLocation == PanelCorner.TopLeft || StatsPanelLocation == PanelCorner.BottomLeft;
			var top = StatsPanelLocation == PanelCorner.TopLeft || StatsPanelLocation == PanelCorner.TopRight;

			var x = left ? region.X + margin : region.X + region.Width - size.Width - margin;
			var y = top ? region.Y + margin : region.Y + region.Height - size.Height - margin;

			DrawTextBox(context, lines, new Rectangle(x, y, size.Width, size.Height));
		}

		// hover details: what the probability was built from and how the trade ended
		private void RenderTooltip(RenderContext context, SignalTrade trade)
		{
			var estimate = trade.Estimate;
			var pTp = Percent(estimate.TakeProfit);
			var side = trade.IsLong ? "longs" : "shorts";
			var time = trade.EntryTime.Add(InstrumentInfo.TimeZoneOffset).ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

			var lines = new List<(string Text, Color Color)>
			{
				($"{Side(trade)} @ {FormatPrice(trade.EntryPrice)}   {time}", trade.IsLong ? BuyColor : ShortColor),
				($"TP {FormatPrice(trade.TakeProfitPrice)} (+{TakeProfitTicks}t)   SL {FormatPrice(trade.StopLossPrice)} (-{StopLossTicks}t)", Color.White),
				($"P(TP first) {pTp}%   P(SL first) {100 - pTp}%", Color.White),
				($"This setup: {WinsText(estimate.SetupWins, estimate.SetupCount)}", Color.Silver),
				($"All {TriggerLabel(trade.Trigger)} {side}: {WinsText(estimate.TriggerWins, estimate.TriggerCount)}", Color.Silver),
				($"All {side}: {WinsText(estimate.DirectionWins, estimate.DirectionCount)}", Color.Silver),
				($"Absorption {YesNo(trade.HasAbsorption)}   Trend {YesNo(trade.WithTrend)}   Delta {YesNo(trade.DeltaConfirms)}", Color.Silver),
				(ResultText(trade), OutcomeColor(trade.Outcome))
			};

			var size = MeasureLines(context, lines);
			var region = ChartInfo.PriceChartContainer.Region;
			var mouse = MouseLocationInfo.LastPosition;

			var x = Math.Max(region.X, Math.Min(mouse.X + 16, region.X + region.Width - size.Width));
			var y = Math.Max(region.Y, Math.Min(mouse.Y + 16, region.Y + region.Height - size.Height));

			DrawTextBox(context, lines, new Rectangle(x, y, size.Width, size.Height));
		}

		private Size MeasureLines(RenderContext context, List<(string Text, Color Color)> lines)
		{
			const int pad = 6;
			var font = LabelFont.RenderObject;
			var width = 0;
			var height = 0;

			foreach (var line in lines)
			{
				var size = context.MeasureString(line.Text, font);
				width = Math.Max(width, size.Width);
				height += size.Height;
			}

			return new Size(width + pad * 2, height + pad * 2);
		}

		private void DrawTextBox(RenderContext context, List<(string Text, Color Color)> lines, Rectangle rect)
		{
			const int pad = 6;
			var font = LabelFont.RenderObject;

			context.FillRectangle(Color.FromArgb(225, 24, 26, 32), rect);
			context.DrawRectangle(new RenderPen(Color.FromArgb(255, 70, 74, 84)), rect);

			var y = rect.Y + pad;

			foreach (var line in lines)
			{
				context.DrawString(line.Text, font, line.Color, rect.X + pad, y);
				y += context.MeasureString(line.Text, font).Height;
			}
		}

		private void DrawPriceTag(RenderContext context, string text, int x, int y, Color color, RenderFont font)
		{
			var size = context.MeasureString(text, font);
			var rect = new Rectangle(x, y - size.Height / 2 - 1, size.Width + 6, size.Height + 2);

			context.FillRectangle(color, rect);
			context.DrawString(text, font, Color.White, rect.X + 3, rect.Y + 1);
		}

		#endregion

		#region Helpers

		private decimal TickSize => InstrumentInfo != null && InstrumentInfo.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m;

		// driftless random walk: P(TP before SL) = SL / (TP + SL)
		private double TakeProfitPrior => (double)StopLossTicks / (TakeProfitTicks + StopLossTicks);

		private Color BuyColor => _buySignal.Color.Convert();

		private Color ShortColor => _shortSignal.Color.Convert();

		private decimal ResultTicks(SignalTrade trade)
		{
			return (trade.ExitPrice - trade.EntryPrice) / TickSize * (trade.IsLong ? 1 : -1);
		}

		private string ResultText(SignalTrade trade)
		{
			var ticks = ResultTicks(trade).ToString("+0;-0;0", CultureInfo.InvariantCulture);
			var assumed = trade.AmbiguousExit ? " (TP and SL in one bar - rule applied)" : string.Empty;

			switch (trade.Outcome)
			{
				case TradeOutcome.TakeProfit:
					return $"Take profit hit on bar {trade.ExitBar} ({ticks}t){assumed}";

				case TradeOutcome.StopLoss:
					return $"Stop loss hit on bar {trade.ExitBar} ({ticks}t){assumed}";

				case TradeOutcome.Expired:
					return $"Expired on bar {trade.ExitBar} at {FormatPrice(trade.ExitPrice)} ({ticks}t)";

				default:
					return "Open - waiting for TP or SL";
			}
		}

		private Color OutcomeColor(TradeOutcome outcome)
		{
			switch (outcome)
			{
				case TradeOutcome.TakeProfit:
					return TakeProfitPen.Color.Convert();

				case TradeOutcome.StopLoss:
					return StopLossPen.Color.Convert();

				case TradeOutcome.Expired:
					return Color.Gray;

				default:
					return Color.FromArgb(255, 230, 150, 0);
			}
		}

		private static string OutcomeBadge(TradeOutcome outcome)
		{
			switch (outcome)
			{
				case TradeOutcome.TakeProfit:
					return "TP";

				case TradeOutcome.StopLoss:
					return "SL";

				case TradeOutcome.Expired:
					return "EXP";

				default:
					return "OPEN";
			}
		}

		private static string TriggerLabel(TriggerType trigger)
		{
			switch (trigger)
			{
				case TriggerType.Sweep:
					return "Sweep";

				case TriggerType.SweepThenFvg:
					return "Sweep+FVG";

				default:
					return "FVG";
			}
		}

		private static string Side(SignalTrade trade)
		{
			return trade.IsLong ? "BUY" : "SHORT";
		}

		private static int Percent(double probability)
		{
			return (int)Math.Round(probability * 100, MidpointRounding.AwayFromZero);
		}

		private static string StatsLine(string name, int wins, int losses, decimal netTicks)
		{
			var count = wins + losses;
			var winRate = count > 0 ? (100.0 * wins / count).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "--";

			return $"{name}  {wins}W {losses}L  {winRate}  {netTicks.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t";
		}

		private static string TrackRecord(int wins, int count)
		{
			return count > 0
				? $"{wins}/{count} TP ({(100.0 * wins / count).ToString("0", CultureInfo.InvariantCulture)}%)"
				: "none yet";
		}

		private static string WinsText(int wins, int count)
		{
			return count > 0
				? $"{wins}/{count} hit TP ({(100.0 * wins / count).ToString("0", CultureInfo.InvariantCulture)}%)"
				: "no history yet";
		}

		private static string YesNo(bool value)
		{
			return value ? "yes" : "no";
		}

		private static Rectangle VerticalSpan(int x1, int x2, int yA, int yB)
		{
			return new Rectangle(x1, Math.Min(yA, yB), x2 - x1, Math.Abs(yB - yA));
		}

		private static Color WithAlpha(Color color, int alpha)
		{
			return Color.FromArgb(alpha, color.R, color.G, color.B);
		}

		private string FormatPrice(decimal price)
		{
			return ChartInfo != null
				? ChartInfo.GetPriceString(price)
				: price.ToString(CultureInfo.InvariantCulture);
		}

		#endregion
	}
}

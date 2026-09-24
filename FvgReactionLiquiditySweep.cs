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
	//      80-tick take profit / 80-tick stop loss, with the stop moving to +20 ticks
	//      once the trade is 40 ticks in profit), follows every signal until it ends,
	//      and labels each new signal with the odds of ending at TP, at the
	//      break-even stop or at SL.
	//   6) Checks each signal bar for the candlestick patterns of TraderLion's cheat
	//      sheet (hammer, engulfing, morning star, three soldiers, ...) that point the
	//      signal's way - one of them counts as a confirmation.
	//
	// How the probabilities are estimated
	// -----------------------------------
	// Every signal is tracked as a virtual trade: entry at the signal bar's close,
	// TP and SL a fixed number of ticks away, the stop moved to break-even once the
	// trigger trades, following the order in which price traded. A signal's odds come
	// ONLY from trades that had already finished when it fired (walk-forward, no
	// look-ahead), so the numbers on old signals are exactly what the indicator
	// would have shown live.
	//
	// Signals are grouped direction -> trigger (FVG / sweep / sweep then FVG) ->
	// number of confirmations (absorption, EMA trend, bar delta, candlestick pattern;
	// 0-4). A group with few trades is shrunk toward its parent group (Dirichlet
	// smoothing, per ending):
	//     p = (count + k * p_parent) / (trades + k)
	// and the top-level prior is the exact odds of a driftless random walk: 50 / 50
	// for a plain symmetric bracket, TP 2/9 / BE 4/9 / SL 1/3 for 80 / 80 with the
	// stop moving to +20 at +40 - an expected 0 ticks. With no history every signal
	// starts there and only moves as real outcomes build up on the chart.
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

		// How to order a bar's high and low when only O/H/L/C are known
		public enum SameBarHitRule
		{
			[Display(Name = "Worst case for the trade (stop first)")]
			StopLossFirst,

			[Display(Name = "Candle direction (O-L-H-C / O-H-L-C)")]
			CandleDirection,

			[Display(Name = "Open to the nearer extreme first")]
			NearestExtremeFirst
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
			BreakEven,    // stopped at the break-even stop after the trigger was reached
			StopLoss,
			Expired
		}

		// The candlestick cheat sheet's patterns, as found on a signal bar. Each one points one
		// way, so a buy only ever carries bullish ones and a short bearish ones.
		[Flags]
		private enum CandlePattern
		{
			None = 0,
			Hammer = 1 << 0,
			DragonflyDoji = 1 << 1,
			ShootingStar = 1 << 2,
			GravestoneDoji = 1 << 3,
			InvertedHammer = 1 << 4,
			HangingMan = 1 << 5,
			BullishEngulfing = 1 << 6,
			BearishEngulfing = 1 << 7,
			PiercingLine = 1 << 8,
			DarkCloudCover = 1 << 9,
			BullishHarami = 1 << 10,
			BearishHarami = 1 << 11,
			TweezerBottom = 1 << 12,
			TweezerTop = 1 << 13,
			MorningStar = 1 << 14,
			EveningStar = 1 << 15,
			ThreeWhiteSoldiers = 1 << 16,
			ThreeBlackCrows = 1 << 17,
			BullishMarubozu = 1 << 18,
			BearishMarubozu = 1 << 19
		}

		// the parts of a candle the patterns are made of
		private readonly struct CandleShape
		{
			public CandleShape(IndicatorCandle candle)
			{
				Open = candle.Open;
				High = candle.High;
				Low = candle.Low;
				Close = candle.Close;
			}

			public decimal Open { get; }
			public decimal High { get; }
			public decimal Low { get; }
			public decimal Close { get; }
			public decimal Range => High - Low;
			public decimal Body => Math.Abs(Close - Open);
			public decimal Top => Math.Max(Open, Close);        // top of the real body
			public decimal Bottom => Math.Min(Open, Close);     // bottom of the real body
			public decimal Middle => (Open + Close) / 2;        // middle of the real body
			public decimal UpperWick => High - Top;
			public decimal LowerWick => Bottom - Low;
			public bool IsBullish => Close > Open;
			public bool IsBearish => Close < Open;
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
			public int Wins;        // take profit
			public int BreakEvens;  // break-even stop
			public int Losses;      // stop loss
			public int Count => Wins + BreakEvens + Losses;
		}

		// Counts copied out of a counter, so an estimate keeps what was known when it was made
		private readonly struct OutcomeTally
		{
			public OutcomeTally(OutcomeCounter counter)
			{
				Wins = counter.Wins;
				BreakEvens = counter.BreakEvens;
				Losses = counter.Losses;
			}

			public int Wins { get; }
			public int BreakEvens { get; }
			public int Losses { get; }
			public int Count => Wins + BreakEvens + Losses;
		}

		// How likely each ending is; BreakEven stays 0 while the break-even stop is off
		private readonly struct OutcomeOdds
		{
			public OutcomeOdds(double takeProfit, double breakEven, double stopLoss)
			{
				TakeProfit = takeProfit;
				BreakEven = breakEven;
				StopLoss = stopLoss;
			}

			public double TakeProfit { get; }
			public double BreakEven { get; }
			public double StopLoss { get; }
		}

		// What the model knew at the moment a signal fired
		private readonly struct ProbabilityEstimate
		{
			public ProbabilityEstimate(OutcomeOdds odds, double expectedTicks, OutcomeCounter setup, OutcomeCounter trigger, OutcomeCounter direction)
			{
				Odds = odds;
				ExpectedTicks = expectedTicks;
				Setup = new OutcomeTally(setup);
				Trigger = new OutcomeTally(trigger);
				Direction = new OutcomeTally(direction);
			}

			public OutcomeOdds Odds { get; }
			public double TakeProfit => Odds.TakeProfit;   // P(the trade ends at TP)
			public double BreakEven => Odds.BreakEven;     // P(it ends at the break-even stop)
			public double StopLoss => Odds.StopLoss;       // P(it ends at the full stop loss)
			public double ExpectedTicks { get; }           // TP, BE and SL ticks weighted by those odds
			public OutcomeTally Setup { get; }
			public OutcomeTally Trigger { get; }
			public OutcomeTally Direction { get; }
		}

		// Walk-forward estimate of how a trade ends (TP / break-even / SL), smoothed down the
		// hierarchy direction -> direction + trigger -> direction + trigger + confirmations.
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

			public void Record(bool isLong, TriggerType trigger, int confirmations, TradeOutcome outcome)
			{
				var d = isLong ? 0 : 1;
				var t = (int)trigger;

				Add(_byDirection[d], outcome);
				Add(_byTrigger[d, t], outcome);
				Add(_bySetup[d, t, confirmations], outcome);
				Resolved++;
			}

			public ProbabilityEstimate Estimate(bool isLong, TriggerType trigger, int confirmations, OutcomeOdds prior, double priorWeight,
				double takeProfitTicks, double breakEvenTicks, double stopLossTicks)
			{
				var d = isLong ? 0 : 1;
				var t = (int)trigger;
				var direction = _byDirection[d];
				var triggerStats = _byTrigger[d, t];
				var setup = _bySetup[d, t, confirmations];

				var odds = Smooth(direction, prior, priorWeight);
				odds = Smooth(triggerStats, odds, priorWeight);
				odds = Smooth(setup, odds, priorWeight);

				var expectedTicks = odds.TakeProfit * takeProfitTicks + odds.BreakEven * breakEvenTicks - odds.StopLoss * stopLossTicks;
				return new ProbabilityEstimate(odds, expectedTicks, setup, triggerStats, direction);
			}

			private static void Add(OutcomeCounter counter, TradeOutcome outcome)
			{
				switch (outcome)
				{
					case TradeOutcome.TakeProfit:
						counter.Wins++;
						break;

					case TradeOutcome.BreakEven:
						counter.BreakEvens++;
						break;

					case TradeOutcome.StopLoss:
						counter.Losses++;
						break;
				}
			}

			// Dirichlet smoothing: each ending's share, pulled toward the parent's odds by
			// `weight` virtual trades
			private static OutcomeOdds Smooth(OutcomeCounter counter, OutcomeOdds prior, double weight)
			{
				var total = counter.Count + weight;

				return new OutcomeOdds(
					(counter.Wins + weight * prior.TakeProfit) / total,
					(counter.BreakEvens + weight * prior.BreakEven) / total,
					(counter.Losses + weight * prior.StopLoss) / total);
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
			public CandlePattern CandlePatterns;   // cheat-sheet patterns completed by the signal bar
			public int Confirmations;
			public ProbabilityEstimate Estimate;
			public bool IsShown;          // false = tracked for the statistics only (filtered, or a position was already open)
			public bool HasBreakEven;     // the break-even stop was on when the signal fired
			public decimal TriggerPrice;  // reaching this price moves the stop...
			public decimal BreakEvenPrice;// ...to this one
			public bool BreakEvenActive;
			public int BreakEvenBar = -1;
			public TradeOutcome Outcome;
			public int ExitBar = -1;
			public decimal ExitPrice;
			public bool AmbiguousExit;    // the order of the bar's high and low decided it - SameBarRule applied

			public SignalTrade Clone()
			{
				return (SignalTrade)MemberwiseClone();
			}
		}

		// Where a trade stands after price has traded along a path
		private readonly struct PathResult
		{
			public PathResult(TradeOutcome outcome, bool breakEvenActive, decimal exitPrice)
			{
				Outcome = outcome;
				BreakEvenActive = breakEvenActive;
				ExitPrice = exitPrice;
			}

			public TradeOutcome Outcome { get; }
			public bool BreakEvenActive { get; }
			public decimal ExitPrice { get; }

			// worst (0) to best (4) for the trade: SL, still open without break-even,
			// break-even hit, still open with the stop in profit, TP
			public int Rank => Outcome == TradeOutcome.StopLoss ? 0
				: Outcome == TradeOutcome.BreakEven ? 2
				: Outcome == TradeOutcome.TakeProfit ? 4
				: BreakEvenActive ? 3 : 1;

			public bool SameAs(PathResult other)
			{
				return Outcome == other.Outcome && BreakEvenActive == other.BreakEvenActive;
			}
		}

		private class PanelStats
		{
			public int LongWins;
			public int LongBreakEvens;
			public int LongLosses;
			public int ShortWins;
			public int ShortBreakEvens;
			public int ShortLosses;
			public int Open;
			public int Expired;
			public int Filtered;
			public int ModelResolved;
			public decimal LongNetTicks;
			public decimal ShortNetTicks;
			public int PositiveEvCount;
			public decimal PositiveEvTicks;
			public int NegativeEvCount;
			public decimal NegativeEvTicks;
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

		// absorption, EMA trend, bar delta and a candlestick pattern
		private const int MaxConfirmations = 4;

		// Share of the bar's range, measured from the low (buys) or high (shorts), that
		// counts as "at the extreme" for the absorption confirmation.
		private const decimal AbsorptionEdgeFraction = 0.35m;

		// Candlestick pattern shapes, as shares of the candle's range (high - low):
		// the "little or no" wick opposite a hammer's or shooting star's long one,
		private const decimal PinBarMaxOtherWick = 0.1m;

		// a doji's body - open and close "virtually equal",
		private const decimal DojiMaxBody = 0.1m;

		// how far from its high (low) each of three white soldiers (black crows) may close,
		private const decimal SoldierMaxCloseGap = 0.25m;

		// and each of a marubozu's (nearly absent) wicks.
		private const decimal MarubozuMaxWick = 0.05m;

		// Pattern names, in the order a label lists them: three-candle patterns first,
		// then two-candle, then single candles.
		private static readonly (CandlePattern Pattern, string Name)[] CandlePatternNames =
		{
			(CandlePattern.MorningStar, "Morning star"),
			(CandlePattern.EveningStar, "Evening star"),
			(CandlePattern.ThreeWhiteSoldiers, "Three white soldiers"),
			(CandlePattern.ThreeBlackCrows, "Three black crows"),
			(CandlePattern.BullishEngulfing, "Bullish engulfing"),
			(CandlePattern.BearishEngulfing, "Bearish engulfing"),
			(CandlePattern.PiercingLine, "Piercing line"),
			(CandlePattern.DarkCloudCover, "Dark cloud cover"),
			(CandlePattern.TweezerBottom, "Tweezer bottom"),
			(CandlePattern.TweezerTop, "Tweezer top"),
			(CandlePattern.BullishHarami, "Bullish harami"),
			(CandlePattern.BearishHarami, "Bearish harami"),
			(CandlePattern.Hammer, "Hammer"),
			(CandlePattern.ShootingStar, "Shooting star"),
			(CandlePattern.DragonflyDoji, "Dragonfly doji"),
			(CandlePattern.GravestoneDoji, "Gravestone doji"),
			(CandlePattern.InvertedHammer, "Inverted hammer"),
			(CandlePattern.HangingMan, "Hanging man"),
			(CandlePattern.BullishMarubozu, "Bullish marubozu"),
			(CandlePattern.BearishMarubozu, "Bearish marubozu")
		};

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

		// what the bar being followed had traded when we last looked at it, so each update
		// only walks the trades through what is new
		private int _trackedBar = -1;
		private decimal _trackedHigh;
		private decimal _trackedLow;
		private decimal _trackedClose;

		// results of the signals shown on the chart (the model also learns from filtered ones)
		private int _longWins;
		private int _longBreakEvens;
		private int _longLosses;
		private int _shortWins;
		private int _shortBreakEvens;
		private int _shortLosses;
		private int _expired;
		private int _filtered;
		private decimal _longNetTicks;
		private decimal _shortNetTicks;

		// the panel's track record: every settled signal, shown or not, by the sign of the
		// expected ticks it was labelled with
		private int _positiveEvCount;
		private decimal _positiveEvTicks;
		private int _negativeEvCount;
		private decimal _negativeEvTicks;

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
		private bool _requireCandlePattern;
		private int _signalCooldownBars = 3;
		private bool _oneTradeAtATime = true;
		private int _minProbabilityPercent;
		private int _minExpectedTicks;

		private bool _useCandlePatterns = true;
		private bool _patternHammer = true;
		private bool _patternInvertedHammer = true;
		private bool _patternEngulfing = true;
		private bool _patternPiercingLine = true;
		private bool _patternHarami = true;
		private bool _patternTweezers = true;
		private bool _patternStars = true;
		private bool _patternThreeSoldiers = true;
		private bool _patternMarubozu = true;
		private double _pinBarWickRatio = 2.0;
		private int _patternAverageBars = 14;
		private int _tweezerToleranceTicks = 1;

		private int _takeProfitTicks = 80;
		private int _stopLossTicks = 80;
		private int _breakEvenTriggerTicks = 40;
		private int _breakEvenStopTicks = 20;
		private int _maxBarsInTrade;
		private bool _expireAtSessionEnd;
		private SameBarHitRule _sameBarRule = SameBarHitRule.NearestExtremeFirst;
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
			Description = "Close above the EMA confirms buys, below confirms shorts. Counts as one of the four confirmations.")]
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

		[Display(Name = "Require candlestick pattern", GroupName = "Signals", Order = 108,
			Description = "Only signal when the signal bar completes one of the candlestick patterns switched on under Candlestick Patterns, pointing the signal's way.")]
		public bool RequireCandlePattern
		{
			get => _requireCandlePattern;
			set { _requireCandlePattern = value; RecalculateValues(); }
		}

		[Display(Name = "Cooldown between signals (bars)", GroupName = "Signals", Order = 109,
			Description = "Minimum bars before another signal in the same direction, so one move isn't counted several times.")]
		[Range(0, 1000)]
		public int SignalCooldownBars
		{
			get => _signalCooldownBars;
			set { _signalCooldownBars = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "One trade at a time", GroupName = "Signals", Order = 110,
			Description = "While a shown signal's trade is still open, new signals are not shown (they are still tracked for the statistics).")]
		public bool OneTradeAtATime
		{
			get => _oneTradeAtATime;
			set { _oneTradeAtATime = value; RecalculateValues(); }
		}

		[Display(Name = "Min TP probability to show (%)", GroupName = "Signals", Order = 111,
			Description = "Hide signals whose estimated take-profit probability is below this. Hidden signals are still tracked so the statistics keep learning.")]
		[Range(0, 100)]
		public int MinProbabilityPercent
		{
			get => _minProbabilityPercent;
			set { _minProbabilityPercent = Math.Min(100, Math.Max(0, value)); RecalculateValues(); }
		}

		[Display(Name = "Min expected ticks to show (0 = off)", GroupName = "Signals", Order = 112,
			Description = "Hide signals whose expected result - the TP, break-even and SL ticks weighted by their odds - is below this. Hidden signals are still tracked.")]
		[Range(0, 100000)]
		public int MinExpectedTicks
		{
			get => _minExpectedTicks;
			set { _minExpectedTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Use candlestick patterns", GroupName = "Candlestick Patterns", Order = 150,
			Description = "A pattern from the candlestick cheat sheet that completes on the signal bar, pointing the signal's way, counts as one of the four confirmations.")]
		public bool UseCandlePatterns
		{
			get => _useCandlePatterns;
			set { _useCandlePatterns = value; RecalculateValues(); }
		}

		[Display(Name = "Hammer / Shooting star", GroupName = "Candlestick Patterns", Order = 151,
			Description = "Buys: a small body at the top of the candle, a lower wick at least 'wick / body' times the body and little or no upper wick (a dragonfly doji when the body is almost nil). Shorts: the same upside down - shooting star, gravestone doji.")]
		public bool PatternHammer
		{
			get => _patternHammer;
			set { _patternHammer = value; RecalculateValues(); }
		}

		[Display(Name = "Inverted hammer / Hanging man", GroupName = "Candlestick Patterns", Order = 152,
			Description = "The same shapes the other way up. Buys: an inverted hammer (long upper wick) after the dip. Shorts: a hanging man (long lower wick) after the rally.")]
		public bool PatternInvertedHammer
		{
			get => _patternInvertedHammer;
			set { _patternInvertedHammer = value; RecalculateValues(); }
		}

		[Display(Name = "Engulfing", GroupName = "Candlestick Patterns", Order = 153,
			Description = "A candle whose body covers the whole body of the opposite-colored candle before it, and is at least an average body.")]
		public bool PatternEngulfing
		{
			get => _patternEngulfing;
			set { _patternEngulfing = value; RecalculateValues(); }
		}

		[Display(Name = "Piercing line / Dark cloud cover", GroupName = "Candlestick Patterns", Order = 154,
			Description = "After a long bearish candle, a bullish one from its close or lower that closes above the middle of its body, but not above its open (piercing line). Shorts: the mirror image (dark cloud cover).")]
		public bool PatternPiercingLine
		{
			get => _patternPiercingLine;
			set { _patternPiercingLine = value; RecalculateValues(); }
		}

		[Display(Name = "Harami", GroupName = "Candlestick Patterns", Order = 155,
			Description = "A long candle, then a small candle of the other color whose body stays inside the first candle's body.")]
		public bool PatternHarami
		{
			get => _patternHarami;
			set { _patternHarami = value; RecalculateValues(); }
		}

		[Display(Name = "Tweezer bottom / top", GroupName = "Candlestick Patterns", Order = 156,
			Description = "A bearish then a bullish candle with the same low (buys), or a bullish then a bearish candle with the same high (shorts).")]
		public bool PatternTweezers
		{
			get => _patternTweezers;
			set { _patternTweezers = value; RecalculateValues(); }
		}

		[Display(Name = "Morning star / Evening star", GroupName = "Candlestick Patterns", Order = 157,
			Description = "A long bearish candle, a small one (the star, often a doji) no higher than the middle of its body, then a long bullish candle closing above that middle. Shorts: the mirror image.")]
		public bool PatternStars
		{
			get => _patternStars;
			set { _patternStars = value; RecalculateValues(); }
		}

		[Display(Name = "Three white soldiers / black crows", GroupName = "Candlestick Patterns", Order = 158,
			Description = "Three long bullish candles, each opening inside the body before it, closing higher and near its high. Shorts: three long bearish candles the other way.")]
		public bool PatternThreeSoldiers
		{
			get => _patternThreeSoldiers;
			set { _patternThreeSoldiers = value; RecalculateValues(); }
		}

		[Display(Name = "Marubozu", GroupName = "Candlestick Patterns", Order = 159,
			Description = "A long candle with (almost) no wicks: it opened at one end of its range and closed at the other.")]
		public bool PatternMarubozu
		{
			get => _patternMarubozu;
			set { _patternMarubozu = value; RecalculateValues(); }
		}

		[Display(Name = "Hammer wick / body (min)", GroupName = "Candlestick Patterns", Order = 160,
			Description = "How many times the body the long wick of a hammer, shooting star, inverted hammer or hanging man must be. The cheat sheet says at least 2.")]
		[Range(1.0, 100.0)]
		public double PinBarWickRatio
		{
			get => _pinBarWickRatio;
			set { _pinBarWickRatio = Math.Max(1.0, value); RecalculateValues(); }
		}

		[Display(Name = "Average body (bars)", GroupName = "Candlestick Patterns", Order = 161,
			Description = "Long and small bodies are measured against the average body of this many candles before the pattern.")]
		[Range(1, 1000)]
		public int PatternAverageBars
		{
			get => _patternAverageBars;
			set { _patternAverageBars = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Tweezer match (ticks)", GroupName = "Candlestick Patterns", Order = 162,
			Description = "How far apart the two lows of a tweezer bottom (or highs of a tweezer top) may be and still count as the same price.")]
		[Range(0, 1000)]
		public int TweezerToleranceTicks
		{
			get => _tweezerToleranceTicks;
			set { _tweezerToleranceTicks = Math.Max(0, value); RecalculateValues(); }
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

		[Display(Name = "Break-even trigger (ticks, 0 = off)", GroupName = "Take Profit / Stop Loss", Order = 202,
			Description = "Once a trade is this many ticks in profit, its stop moves to the break-even stop. Must be below the take profit.")]
		[Range(0, 100000)]
		public int BreakEvenTriggerTicks
		{
			get => _breakEvenTriggerTicks;
			set { _breakEvenTriggerTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Break-even stop (ticks in profit)", GroupName = "Take Profit / Stop Loss", Order = 203,
			Description = "Where the stop moves once the trigger is reached, in ticks of profit from the entry (0 = the entry price). Kept below the trigger.")]
		[Range(0, 100000)]
		public int BreakEvenStopTicks
		{
			get => _breakEvenStopTicks;
			set { _breakEvenStopTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Max bars in trade (0 = no limit)", GroupName = "Take Profit / Stop Loss", Order = 204,
			Description = "Trades that hit neither level within this many bars expire and are left out of the probabilities.")]
		[Range(0, 100000)]
		public int MaxBarsInTrade
		{
			get => _maxBarsInTrade;
			set { _maxBarsInTrade = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Close trades at session end", GroupName = "Take Profit / Stop Loss", Order = 205,
			Description = "Expire open trades on the last bar of each session and take no new signals on it.")]
		public bool ExpireAtSessionEnd
		{
			get => _expireAtSessionEnd;
			set { _expireAtSessionEnd = value; RecalculateValues(); }
		}

		[Display(Name = "Order of high and low inside a bar", GroupName = "Take Profit / Stop Loss", Order = 206,
			Description = "On historical bars only O/H/L/C are known, so when the order decides a trade (TP vs SL, or whether the break-even stop was hit after the trigger) this rule picks it. Live bars follow the trades as they happen.")]
		public SameBarHitRule SameBarRule
		{
			get => _sameBarRule;
			set { _sameBarRule = value; RecalculateValues(); }
		}

		[Display(Name = "Probability smoothing (virtual trades)", GroupName = "Take Profit / Stop Loss", Order = 207,
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

		[Display(Name = "Break-even line", GroupName = "Display", Order = 308)]
		public PenSettings BreakEvenPen { get; set; } = new PenSettings { Color = Color.FromArgb(255, 255, 179, 0).Convert(), Width = 1 };

		[Display(Name = "Alert on new signal", GroupName = "Alerts", Order = 400)]
		public bool UseAlerts { get; set; }

		[Display(Name = "Alert on TP / SL / break-even", GroupName = "Alerts", Order = 401,
			Description = "When a shown trade hits its take profit, stop loss or break-even stop, and when its stop moves to break-even.")]
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

				// Open trades follow the forming bar as it trades, so the order of events
				// (trigger, break-even stop, TP, SL) is known live, not guessed.
				AdvanceOpenTrades(bar, candle, ref alerts);

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
			_trackedBar = -1;
			_lastLowSweepBar = -1;
			_lastHighSweepBar = -1;
			_lastLongSignalBar = -1;
			_lastShortSignalBar = -1;
			_realtime = false;
			_lastPrice = 0;

			_longWins = 0;
			_longBreakEvens = 0;
			_longLosses = 0;
			_shortWins = 0;
			_shortBreakEvens = 0;
			_shortLosses = 0;
			_expired = 0;
			_filtered = 0;
			_longNetTicks = 0;
			_shortNetTicks = 0;
			_positiveEvCount = 0;
			_positiveEvTicks = 0;
			_negativeEvCount = 0;
			_negativeEvTicks = 0;
		}

		private void ProcessClosedBar(int bar, ref List<PendingAlert> alerts)
		{
			var candle = GetCandle(bar);

			ClearMarkers(bar);

			// final footprint of the bar - the last intrabar update can be a tick behind
			UpdateAbsorption(bar, candle);
			PruneOldAbsorption(bar);

			AdvanceOpenTrades(bar, candle, ref alerts);
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

		#region Candlestick patterns

		// The patterns of TraderLion's candlestick cheat sheet that complete on the signal bar
		// and point the trade's way. The cheat sheet reads them after a decline (bullish ones)
		// or a rally (bearish ones); the signal supplies that context, since a buy comes after a
		// dip into an FVG or through a swing low and a short after a rally. It is also what
		// tells a hammer from a hanging man, and an inverted hammer from a shooting star: the
		// same shapes, read by where they appear. Only closed bars are read.
		private CandlePattern FindCandlePatterns(int bar, bool isLong)
		{
			if (!UseCandlePatterns)
				return CandlePattern.None;

			var c = new CandleShape(GetCandle(bar));
			var found = SingleCandlePatterns(c, AverageBody(bar), isLong);

			if (bar < 1)
				return found;

			var p = new CandleShape(GetCandle(bar - 1));
			found |= TwoCandlePatterns(p, c, AverageBody(bar - 1), isLong);

			if (bar < 2)
				return found;

			var a = new CandleShape(GetCandle(bar - 2));
			return found | ThreeCandlePatterns(a, p, c, AverageBody(bar - 2), isLong);
		}

		// Average real body of the candles before a pattern's first candle, the yardstick for
		// "long" and "small" bodies (0 when there are none)
		private decimal AverageBody(int firstBar)
		{
			var from = Math.Max(0, firstBar - PatternAverageBars);

			if (from >= firstBar)
				return 0;

			var sum = 0m;

			for (var i = from; i < firstBar; i++)
			{
				var candle = GetCandle(i);
				sum += Math.Abs(candle.Close - candle.Open);
			}

			return sum / (firstBar - from);
		}

		private static bool IsLongBody(CandleShape candle, decimal averageBody)
		{
			return candle.Body > 0 && candle.Body >= averageBody;
		}

		private static bool IsSmallBody(CandleShape candle, decimal averageBody)
		{
			return candle.Body < averageBody;
		}

		private CandlePattern SingleCandlePatterns(CandleShape c, decimal averageBody, bool isLong)
		{
			if (c.Range <= 0)
				return CandlePattern.None;

			var ratio = (decimal)PinBarWickRatio;
			var doji = c.Body <= DojiMaxBody * c.Range;

			// the hammer shape: long wick below a small body, little or no wick above - and upside down
			var longLowerWick = c.LowerWick >= ratio * c.Body && c.UpperWick <= PinBarMaxOtherWick * c.Range;
			var longUpperWick = c.UpperWick >= ratio * c.Body && c.LowerWick <= PinBarMaxOtherWick * c.Range;
			var marubozu = IsLongBody(c, averageBody)
				&& c.UpperWick <= MarubozuMaxWick * c.Range && c.LowerWick <= MarubozuMaxWick * c.Range;

			var found = CandlePattern.None;

			if (isLong)
			{
				if (PatternHammer && longLowerWick)
					found |= doji ? CandlePattern.DragonflyDoji : CandlePattern.Hammer;

				if (PatternInvertedHammer && longUpperWick)
					found |= CandlePattern.InvertedHammer;

				if (PatternMarubozu && marubozu && c.IsBullish)
					found |= CandlePattern.BullishMarubozu;
			}
			else
			{
				if (PatternHammer && longUpperWick)
					found |= doji ? CandlePattern.GravestoneDoji : CandlePattern.ShootingStar;

				if (PatternInvertedHammer && longLowerWick)
					found |= CandlePattern.HangingMan;

				if (PatternMarubozu && marubozu && c.IsBearish)
					found |= CandlePattern.BearishMarubozu;
			}

			return found;
		}

		// p = the candle before the signal bar c. Futures rarely gap between bars, so "opens
		// below the previous close" is taken as "at or below".
		private CandlePattern TwoCandlePatterns(CandleShape p, CandleShape c, decimal averageBody, bool isLong)
		{
			var tolerance = TweezerToleranceTicks * TickSize;
			var found = CandlePattern.None;

			if (isLong)
			{
				// a bearish candle, then a bigger bullish one whose body covers all of it
				if (PatternEngulfing && p.IsBearish && c.IsBullish && c.Open <= p.Close && c.Close >= p.Open
					&& c.Body > p.Body && IsLongBody(c, averageBody))
					found |= CandlePattern.BullishEngulfing;

				// a long bearish candle, then a bullish one back above the middle of its body
				if (PatternPiercingLine && p.IsBearish && IsLongBody(p, averageBody) && c.IsBullish
					&& c.Open <= p.Close && c.Close > p.Middle && c.Close < p.Open)
					found |= CandlePattern.PiercingLine;

				// a long bearish candle, then a small bullish one inside its body
				if (PatternHarami && p.IsBearish && IsLongBody(p, averageBody) && c.IsBullish && IsSmallBody(c, averageBody)
					&& c.Open >= p.Close && c.Close <= p.Open)
					found |= CandlePattern.BullishHarami;

				// a bearish and a bullish candle bottoming at the same price
				if (PatternTweezers && p.IsBearish && c.IsBullish && Math.Abs(c.Low - p.Low) <= tolerance)
					found |= CandlePattern.TweezerBottom;
			}
			else
			{
				if (PatternEngulfing && p.IsBullish && c.IsBearish && c.Open >= p.Close && c.Close <= p.Open
					&& c.Body > p.Body && IsLongBody(c, averageBody))
					found |= CandlePattern.BearishEngulfing;

				if (PatternPiercingLine && p.IsBullish && IsLongBody(p, averageBody) && c.IsBearish
					&& c.Open >= p.Close && c.Close < p.Middle && c.Close > p.Open)
					found |= CandlePattern.DarkCloudCover;

				if (PatternHarami && p.IsBullish && IsLongBody(p, averageBody) && c.IsBearish && IsSmallBody(c, averageBody)
					&& c.Open <= p.Close && c.Close >= p.Open)
					found |= CandlePattern.BearishHarami;

				if (PatternTweezers && p.IsBullish && c.IsBearish && Math.Abs(c.High - p.High) <= tolerance)
					found |= CandlePattern.TweezerTop;
			}

			return found;
		}

		// a, p = the two candles before the signal bar c
		private CandlePattern ThreeCandlePatterns(CandleShape a, CandleShape p, CandleShape c, decimal averageBody, bool isLong)
		{
			var found = CandlePattern.None;

			// each candle opens inside the previous body
			var opensInside = p.Open >= a.Bottom && p.Open <= a.Top && c.Open >= p.Bottom && c.Open <= p.Top;

			if (isLong)
			{
				// a long bearish candle, a small star no higher than the middle of its body, then a
				// long bullish candle closing above that middle
				if (PatternStars && a.IsBearish && IsLongBody(a, averageBody) && IsSmallBody(p, averageBody) && p.Top <= a.Middle
					&& c.IsBullish && IsLongBody(c, averageBody) && c.Close > a.Middle)
					found |= CandlePattern.MorningStar;

				if (PatternThreeSoldiers && IsSoldier(a, averageBody) && IsSoldier(p, averageBody) && IsSoldier(c, averageBody)
					&& opensInside && p.Close > a.Close && c.Close > p.Close)
					found |= CandlePattern.ThreeWhiteSoldiers;
			}
			else
			{
				if (PatternStars && a.IsBullish && IsLongBody(a, averageBody) && IsSmallBody(p, averageBody) && p.Bottom >= a.Middle
					&& c.IsBearish && IsLongBody(c, averageBody) && c.Close < a.Middle)
					found |= CandlePattern.EveningStar;

				if (PatternThreeSoldiers && IsCrow(a, averageBody) && IsCrow(p, averageBody) && IsCrow(c, averageBody)
					&& opensInside && p.Close < a.Close && c.Close < p.Close)
					found |= CandlePattern.ThreeBlackCrows;
			}

			return found;
		}

		// a long bullish candle closing near its high
		private static bool IsSoldier(CandleShape candle, decimal averageBody)
		{
			return candle.IsBullish && IsLongBody(candle, averageBody) && candle.High - candle.Close <= SoldierMaxCloseGap * candle.Range;
		}

		// a long bearish candle closing near its low
		private static bool IsCrow(CandleShape candle, decimal averageBody)
		{
			return candle.IsBearish && IsLongBody(candle, averageBody) && candle.Close - candle.Low <= SoldierMaxCloseGap * candle.Range;
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
			// only the side with the better expected result is shown - neither on a tie.
			if (OneTradeAtATime && candidates.Count == 2)
			{
				var edge = longTrade.Estimate.ExpectedTicks - shortTrade.Estimate.ExpectedTicks;

				if (Math.Abs(edge) < 1e-9)
					canShow = false;
				else if (edge < 0)
					candidates.Reverse();
			}

			foreach (var trade in candidates)
			{
				// compared as labelled, so a signal shown as "60%" passes a 60% filter
				trade.IsShown = canShow
					&& LabelPercents(trade)[0] >= MinProbabilityPercent
					&& (MinExpectedTicks == 0 || LabelExpectedTicks(trade) >= MinExpectedTicks);

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
			var patterns = FindCandlePatterns(bar, isLong);
			var hasPattern = patterns != CandlePattern.None;

			if ((OnlyWithTrend && !withTrend)
				|| (RequireDeltaConfirmation && !deltaConfirms)
				|| (RequireAbsorption && !absorption)
				|| (RequireCandlePattern && !hasPattern))
				return null;

			var confirmations = (absorption ? 1 : 0) + (withTrend ? 1 : 0) + (deltaConfirms ? 1 : 0) + (hasPattern ? 1 : 0);
			var tickSize = TickSize;
			var entry = candle.Close;
			var direction = isLong ? 1 : -1;
			var breakEven = BreakEvenEnabled;

			return new SignalTrade
			{
				EntryBar = bar,
				EntryTime = candle.Time,
				IsLong = isLong,
				EntryPrice = entry,
				TakeProfitPrice = entry + direction * TakeProfitTicks * tickSize,
				StopLossPrice = entry - direction * StopLossTicks * tickSize,
				HasBreakEven = breakEven,
				TriggerPrice = entry + direction * BreakEvenTriggerTicks * tickSize,
				BreakEvenPrice = entry + direction * EffectiveBreakEvenStopTicks * tickSize,
				SignalHigh = candle.High,
				SignalLow = candle.Low,
				Trigger = trigger,
				HasAbsorption = absorption,
				WithTrend = withTrend,
				DeltaConfirms = deltaConfirms,
				CandlePatterns = patterns,
				Confirmations = confirmations,
				Estimate = _model.Estimate(isLong, trigger, confirmations, PriorOdds(), ProbabilitySmoothing,
					TakeProfitTicks, breakEven ? EffectiveBreakEvenStopTicks : 0, StopLossTicks)
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
				var setup = string.Join(", ", PatternNames(trade.CandlePatterns).Prepend(TriggerLabel(trade.Trigger)));
				var message = $"{Side(trade)} @ {FormatPrice(trade.EntryPrice)} ({setup}): {OddsText(trade, " / ")}"
					+ $"  -  TP {FormatPrice(trade.TakeProfitPrice)}, SL {FormatPrice(trade.StopLossPrice)}";

				QueueAlert(ref alerts, message, trade.IsLong ? BuyColor : ShortColor);
			}
		}

		// Walks every open trade through what the bar has traded since we last looked at it:
		// the whole bar (open -> high / low -> close) the first time, then only its new highs,
		// new lows and latest price. Live that is usually a tick or two, so the order of events
		// is known; on a historical bar SameBarRule has to order the high and the low.
		private void AdvanceOpenTrades(int bar, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			decimal start;
			decimal? newHigh = null;
			decimal? newLow = null;

			if (bar != _trackedBar)
			{
				// first look at this bar: either extreme may have come first - and one equal to
				// the open may still have been traded again later, which the worst case allows for
				start = candle.Open;
				newHigh = candle.High;
				newLow = candle.Low;
			}
			else
			{
				start = _trackedClose;

				if (candle.High > _trackedHigh)
					newHigh = candle.High;

				if (candle.Low < _trackedLow)
					newLow = candle.Low;
			}

			_trackedBar = bar;
			_trackedHigh = candle.High;
			_trackedLow = candle.Low;
			_trackedClose = candle.Close;

			for (var i = _openTrades.Count - 1; i >= 0; i--)
			{
				var trade = _openTrades[i];

				// entry is the signal bar's close, so the first bar that can move it is the next one
				if (bar <= trade.EntryBar)
					continue;

				var result = SettleStretch(trade, start, newHigh, newLow, candle.Close, SameBarRule, out var ambiguous);

				if (result.BreakEvenActive && !trade.BreakEvenActive)
				{
					trade.BreakEvenActive = true;
					trade.BreakEvenBar = bar;

					if (_realtime && AlertOnTradeResult && trade.IsShown && result.Outcome == TradeOutcome.Open)
					{
						var message = $"{Side(trade)} from {FormatPrice(trade.EntryPrice)}: +{BreakEvenTriggerTicks}t reached, "
							+ $"stop moved to {FormatPrice(trade.BreakEvenPrice)} (+{EffectiveBreakEvenStopTicks}t)";

						QueueAlert(ref alerts, message, BreakEvenPen.Color.Convert());
					}
				}

				if (result.Outcome == TradeOutcome.Open)
					continue;

				trade.AmbiguousExit = ambiguous;
				CloseTrade(trade, result.Outcome, bar, result.ExitPrice, ref alerts);
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

			var ticks = ResultTicks(trade);

			// only a real ending (TP, break-even or SL) teaches the model - an expired trade reached none
			if (outcome != TradeOutcome.Expired)
			{
				_model.Record(trade.IsLong, trade.Trigger, trade.Confirmations, outcome);

				if (LabelExpectedTicks(trade) > 0)
				{
					_positiveEvCount++;
					_positiveEvTicks += ticks;
				}
				else
				{
					_negativeEvCount++;
					_negativeEvTicks += ticks;
				}
			}

			if (!trade.IsShown)
				return;

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

				case TradeOutcome.BreakEven:
					if (trade.IsLong)
						_longBreakEvens++;
					else
						_shortBreakEvens++;
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

		// One stretch of trading for one trade: from `start` through a new high and/or a new
		// low to `close`. When both extremes are new their order is unknown; if the two
		// orders would end differently for the trade, `rule` picks one.
		private static PathResult SettleStretch(SignalTrade trade, decimal start, decimal? newHigh, decimal? newLow, decimal close,
			SameBarHitRule rule, out bool ambiguous)
		{
			ambiguous = false;

			if (newHigh == null || newLow == null)
			{
				var extreme = newHigh ?? newLow;
				return WalkPath(trade, extreme.HasValue ? new[] { start, extreme.Value, close } : new[] { start, close });
			}

			var high = newHigh.Value;
			var low = newLow.Value;
			var lowFirst = WalkPath(trade, new[] { start, low, high, close });
			var highFirst = WalkPath(trade, new[] { start, high, low, close });

			if (lowFirst.SameAs(highFirst))
				return lowFirst;

			ambiguous = true;

			switch (rule)
			{
				case SameBarHitRule.CandleDirection:
					// a bullish stretch is assumed to trade its low first, a bearish one its high
					return close >= start ? lowFirst : highFirst;

				case SameBarHitRule.NearestExtremeFirst when high - start != start - low:
					return high - start < start - low ? highFirst : lowFirst;

				default:
					// the order that is worse for the trade (also settles a nearest-extreme tie)
					return lowFirst.Rank <= highFirst.Rank ? lowFirst : highFirst;
			}
		}

		// Follows one trade along a price path. Moving in its favour can reach the break-even
		// trigger (which moves the stop) and then the TP; moving against it can reach whichever
		// stop is active. The first price is checked both ways, which also settles a gap
		// through a level at the open.
		private static PathResult WalkPath(SignalTrade trade, IReadOnlyList<decimal> path)
		{
			var direction = trade.IsLong ? 1 : -1;
			var takeProfit = (trade.TakeProfitPrice - trade.EntryPrice) * direction;
			var trigger = (trade.TriggerPrice - trade.EntryPrice) * direction;
			var active = trade.BreakEvenActive;
			var previous = 0m;

			for (var i = 0; i < path.Count; i++)
			{
				// signed distance from the entry, positive = in the trade's favour
				var excursion = (path[i] - trade.EntryPrice) * direction;

				if (i == 0 || excursion > previous)
				{
					if (trade.HasBreakEven && !active && excursion >= trigger)
						active = true;

					if (excursion >= takeProfit)
						return new PathResult(TradeOutcome.TakeProfit, active, trade.TakeProfitPrice);
				}

				if (i == 0 || excursion < previous)
				{
					var stopPrice = active ? trade.BreakEvenPrice : trade.StopLossPrice;

					if (excursion <= (stopPrice - trade.EntryPrice) * direction)
						return new PathResult(active ? TradeOutcome.BreakEven : TradeOutcome.StopLoss, active, stopPrice);
				}

				previous = excursion;
			}

			return new PathResult(TradeOutcome.Open, active, 0);
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
				LongBreakEvens = _longBreakEvens,
				LongLosses = _longLosses,
				ShortWins = _shortWins,
				ShortBreakEvens = _shortBreakEvens,
				ShortLosses = _shortLosses,
				Open = _openTrades.Count(t => t.IsShown),
				Expired = _expired,
				Filtered = _filtered,
				ModelResolved = _model.Resolved,
				LongNetTicks = _longNetTicks,
				ShortNetTicks = _shortNetTicks,
				PositiveEvCount = _positiveEvCount,
				PositiveEvTicks = _positiveEvTicks,
				NegativeEvCount = _negativeEvCount,
				NegativeEvTicks = _negativeEvTicks,
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

		// Long/short-position style boxes: entry -> TP shaded green, entry -> SL red, from the
		// signal bar to the bar that settled the trade. With break-even on, a dotted line marks
		// the trigger; once it is reached the stop line carries on at the break-even price.
		private void RenderTradeLevels(RenderContext context, List<SignalTrade> trades, int firstBar, int lastBar)
		{
			var lastIndex = CurrentBar - 1;
			var tpColor = TakeProfitPen.Color.Convert();
			var slColor = StopLossPen.Color.Convert();
			var beColor = BreakEvenPen.Color.Convert();
			var tpFill = WithAlpha(tpColor, 38);
			var slFill = WithAlpha(slColor, 38);
			var entryPen = new RenderPen(Color.Gray, 1) { DashStyle = DashStyle.Dash };
			var triggerPen = new RenderPen(WithAlpha(beColor, 170), 1) { DashStyle = DashStyle.Dot };
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

				// the full stop only applies until the stop moves to break-even
				var xStopMoved = trade.BreakEvenActive
					? Math.Min(x2, Math.Max(x1, ChartInfo.GetXByBar(trade.BreakEvenBar, false)))
					: x2;

				context.FillRectangle(tpFill, VerticalSpan(x1, x2, yEntry, yTp));
				context.FillRectangle(slFill, VerticalSpan(x1, xStopMoved, yEntry, ySl));
				context.DrawLine(TakeProfitPen.RenderObject, x1, yTp, x2, yTp);
				context.DrawLine(StopLossPen.RenderObject, x1, ySl, xStopMoved, ySl);
				context.DrawLine(entryPen, x1, yEntry, x2, yEntry);

				if (trade.HasBreakEven)
				{
					var yTrigger = ChartInfo.GetYByPrice(trade.TriggerPrice, false);
					context.DrawLine(triggerPen, x1, yTrigger, xStopMoved, yTrigger);

					if (trade.BreakEvenActive)
					{
						var yBreakEven = ChartInfo.GetYByPrice(trade.BreakEvenPrice, false);
						context.DrawLine(BreakEvenPen.RenderObject, xStopMoved, yBreakEven, x2, yBreakEven);
					}
				}

				if (trade.Outcome != TradeOutcome.Open)
					continue;

				// live trade: price tags at the end of its TP line and of the stop that applies now
				DrawPriceTag(context, $"TP {FormatPrice(trade.TakeProfitPrice)}", x2 + 4, yTp, tpColor, font);

				if (trade.BreakEvenActive)
					DrawPriceTag(context, $"BE {FormatPrice(trade.BreakEvenPrice)}", x2 + 4, ChartInfo.GetYByPrice(trade.BreakEvenPrice, false), beColor, font);
				else
					DrawPriceTag(context, $"SL {FormatPrice(trade.StopLossPrice)}", x2 + 4, ySl, slColor, font);
			}
		}

		// Two-line label under a buy / above a short:
		//   BUY  TP 31% | BE 41% | SL 28%                    [OPEN / TP / BE / SL / EXP]
		//   Sweep+FVG | Hammer | conf 3/4 | n=14 | EV +6t
		// n = past signals with exactly this setup, EV = the ticks those odds are worth. The
		// candlestick pattern, when the bar completed one, is the first in label order ("+1"
		// when there are more). Returns the label under the mouse.
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

				var line1 = $"{Side(trade)}  {OddsText(trade.HasBreakEven, trade.Estimate.Odds, " | ")}";
				var patterns = PatternNames(trade.CandlePatterns);
				var pattern = patterns.Count == 0 ? string.Empty
					: patterns.Count == 1 ? $" | {patterns[0]}"
					: $" | {patterns[0]} +{patterns.Count - 1}";
				var line2 = $"{TriggerLabel(trade.Trigger)}{pattern} | conf {trade.Confirmations}/{MaxConfirmations} | n={trade.Estimate.Setup.Count}"
					+ $" | EV {SignedTicks(LabelExpectedTicks(trade))}t";
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
			var breakEven = BreakEvenEnabled;
			var bracket = $"TP {TakeProfitTicks}t | SL {StopLossTicks}t";

			if (breakEven)
				bracket += $" | BE +{BreakEvenTriggerTicks}t -> +{EffectiveBreakEvenStopTicks}t";

			var lines = new List<(string Text, Color Color)>
			{
				($"FVG / Sweep signals   {bracket}", Color.White),
				(StatsLine("Longs ", stats.LongWins, stats.LongBreakEvens, stats.LongLosses, stats.LongNetTicks, breakEven), BuyColor),
				(StatsLine("Shorts", stats.ShortWins, stats.ShortBreakEvens, stats.ShortLosses, stats.ShortNetTicks, breakEven), ShortColor),
				(StatsLine("Total ", stats.LongWins + stats.ShortWins, stats.LongBreakEvens + stats.ShortBreakEvens,
					stats.LongLosses + stats.ShortLosses, stats.LongNetTicks + stats.ShortNetTicks, breakEven), Color.White),
				($"Open {stats.Open}   Expired {stats.Expired}   Hidden {stats.Filtered}   Model n={stats.ModelResolved}", Color.Silver),

				// were the labels right? what signals labelled with a positive / not positive
				// expected result really made (every settled signal, hidden ones included)
				($"Labelled EV>0: {TrackRecord(stats.PositiveEvTicks, stats.PositiveEvCount)}   "
					+ $"EV<=0: {TrackRecord(stats.NegativeEvTicks, stats.NegativeEvCount)}", Color.Silver)
			};

			var open = stats.OpenTrade;

			if (open != null)
			{
				// how far the open trade has moved, and the odds from here
				var excursion = (stats.LastPrice - open.EntryPrice) / TickSize * (open.IsLong ? 1 : -1);
				var stop = open.BreakEvenActive ? $", stop +{EffectiveBreakEvenStopTicks}t" : string.Empty;
				var odds = OddsText(open.HasBreakEven, LiveOdds(open, stats.LastPrice), " | ");

				lines.Add(($"Live {Side(open)} {excursion.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t{stop}:  {odds}",
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

		// hover details: what the probabilities were built from and how the trade ended
		private void RenderTooltip(RenderContext context, SignalTrade trade)
		{
			var estimate = trade.Estimate;
			var percents = LabelPercents(trade.HasBreakEven, estimate.Odds);
			var side = trade.IsLong ? "longs" : "shorts";
			var time = trade.EntryTime.Add(InstrumentInfo.TimeZoneOffset).ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
			var expected = estimate.ExpectedTicks.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

			var odds = trade.HasBreakEven
				? $"P(TP) {percents[0]}%   P(BE) {percents[1]}%   P(SL) {percents[2]}%   EV {expected}t"
				: $"P(TP) {percents[0]}%   P(SL) {percents[1]}%   EV {expected}t";

			var lines = new List<(string Text, Color Color)>
			{
				($"{Side(trade)} @ {FormatPrice(trade.EntryPrice)}   {time}", trade.IsLong ? BuyColor : ShortColor),
				($"TP {FormatPrice(trade.TakeProfitPrice)} (+{TakeProfitTicks}t)   SL {FormatPrice(trade.StopLossPrice)} (-{StopLossTicks}t)", Color.White)
			};

			if (trade.HasBreakEven)
			{
				lines.Add(($"Break-even: at +{BreakEvenTriggerTicks}t the stop moves to {FormatPrice(trade.BreakEvenPrice)} (+{EffectiveBreakEvenStopTicks}t)",
					Color.White));
			}

			lines.Add((odds, Color.White));
			lines.Add(($"This setup: {TallyText(estimate.Setup, trade.HasBreakEven)}", Color.Silver));
			lines.Add(($"All {TriggerLabel(trade.Trigger)} {side}: {TallyText(estimate.Trigger, trade.HasBreakEven)}", Color.Silver));
			lines.Add(($"All {side}: {TallyText(estimate.Direction, trade.HasBreakEven)}", Color.Silver));
			var patterns = PatternNames(trade.CandlePatterns);
			lines.Add(($"Absorption {YesNo(trade.HasAbsorption)}   Trend {YesNo(trade.WithTrend)}   Delta {YesNo(trade.DeltaConfirms)}"
				+ $"   Pattern {YesNo(patterns.Count > 0)}", Color.Silver));

			if (patterns.Count > 0)
				lines.Add(($"Candlestick pattern{(patterns.Count > 1 ? "s" : string.Empty)}: {string.Join(", ", patterns)}", Color.Silver));

			if (trade.BreakEvenActive)
				lines.Add(($"Stop moved to +{EffectiveBreakEvenStopTicks}t on bar {trade.BreakEvenBar}", BreakEvenPen.Color.Convert()));

			lines.Add((ResultText(trade), OutcomeColor(trade.Outcome)));

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

		// the stop only moves if the trigger sits between the entry and the take profit
		private bool BreakEvenEnabled => BreakEvenTriggerTicks > 0 && BreakEvenTriggerTicks < TakeProfitTicks;

		// the break-even stop has to stay below its trigger
		private int EffectiveBreakEvenStopTicks => Math.Min(BreakEvenStopTicks, Math.Max(0, BreakEvenTriggerTicks - 1));

		// Odds of a driftless random walk - no edge at all - which every estimate starts from.
		// Without break-even, P(TP) = SL / (TP + SL). With it, price first has to reach the
		// trigger before the stop, P = SL / (trigger + SL), and from there the TP before the
		// break-even stop, P = (trigger - stop) / (TP - stop). For 80 / 80 with the stop
		// moving to +20 at +40 that is TP 2/9, BE 4/9, SL 1/3: an expected 0 ticks.
		private OutcomeOdds PriorOdds()
		{
			double tp = TakeProfitTicks;
			double sl = StopLossTicks;

			if (!BreakEvenEnabled)
				return new OutcomeOdds(sl / (tp + sl), 0, tp / (tp + sl));

			double trigger = BreakEvenTriggerTicks;
			double stop = EffectiveBreakEvenStopTicks;
			var reachTrigger = sl / (trigger + sl);
			var takeProfitAfter = (trigger - stop) / (tp - stop);

			return new OutcomeOdds(reachTrigger * takeProfitAfter, reachTrigger * (1 - takeProfitAfter), 1 - reachTrigger);
		}

		// Live odds of an open trade from the current price. Each leg - reaching the trigger
		// before the stop, then the TP before the break-even stop - is a random walk whose
		// drift makes the odds at entry come out exactly as labelled.
		private OutcomeOdds LiveOdds(SignalTrade trade, decimal price)
		{
			var excursion = (double)((price - trade.EntryPrice) / TickSize * (trade.IsLong ? 1 : -1));
			var odds = trade.Estimate.Odds;
			double tp = TakeProfitTicks;
			double sl = StopLossTicks;

			if (!trade.HasBreakEven)
			{
				var p = LiveProbability(odds.TakeProfit, tp, sl, excursion);
				return new OutcomeOdds(p, 0, 1 - p);
			}

			double trigger = BreakEvenTriggerTicks;
			double stop = EffectiveBreakEvenStopTicks;
			var reached = odds.TakeProfit + odds.BreakEven;
			var takeProfitAfter = reached > 0 ? odds.TakeProfit / reached : 0;

			if (trade.BreakEvenActive)
			{
				var q = LiveProbability(takeProfitAfter, tp - trigger, trigger - stop, excursion - trigger);
				return new OutcomeOdds(q, 1 - q, 0);
			}

			var reach = LiveProbability(1 - odds.StopLoss, trigger, sl, excursion);
			return new OutcomeOdds(reach * takeProfitAfter, reach * (1 - takeProfitAfter), 1 - reach);
		}

		private Color BuyColor => _buySignal.Color.Convert();

		private Color ShortColor => _shortSignal.Color.Convert();

		private decimal ResultTicks(SignalTrade trade)
		{
			return (trade.ExitPrice - trade.EntryPrice) / TickSize * (trade.IsLong ? 1 : -1);
		}

		private string ResultText(SignalTrade trade)
		{
			var ticks = ResultTicks(trade).ToString("+0;-0;0", CultureInfo.InvariantCulture);
			var assumed = trade.AmbiguousExit ? " (high / low order assumed)" : string.Empty;

			switch (trade.Outcome)
			{
				case TradeOutcome.TakeProfit:
					return $"Take profit hit on bar {trade.ExitBar} ({ticks}t){assumed}";

				case TradeOutcome.BreakEven:
					return $"Break-even stop hit on bar {trade.ExitBar} ({ticks}t){assumed}";

				case TradeOutcome.StopLoss:
					return $"Stop loss hit on bar {trade.ExitBar} ({ticks}t){assumed}";

				case TradeOutcome.Expired:
					return $"Expired on bar {trade.ExitBar} at {FormatPrice(trade.ExitPrice)} ({ticks}t)";

				default:
					return trade.BreakEvenActive ? "Open - stop at break-even, waiting for TP or BE" : "Open - waiting for TP or SL";
			}
		}

		private Color OutcomeColor(TradeOutcome outcome)
		{
			switch (outcome)
			{
				case TradeOutcome.TakeProfit:
					return TakeProfitPen.Color.Convert();

				case TradeOutcome.BreakEven:
					return BreakEvenPen.Color.Convert();

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

				case TradeOutcome.BreakEven:
					return "BE";

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

		// the names of the patterns in `patterns`, in label order
		private static List<string> PatternNames(CandlePattern patterns)
		{
			return CandlePatternNames.Where(p => (patterns & p.Pattern) != 0).Select(p => p.Name).ToList();
		}

		// whole percentages that add up to 100: the leftover points go to the largest remainders
		private static int[] Percentages(IReadOnlyList<double> probabilities)
		{
			var result = new int[probabilities.Count];
			var remainders = new double[probabilities.Count];
			var total = 0;

			for (var i = 0; i < probabilities.Count; i++)
			{
				var scaled = probabilities[i] * 100;
				result[i] = (int)Math.Floor(scaled);
				remainders[i] = scaled - result[i];
				total += result[i];
			}

			for (var left = 100 - total; left > 0; left--)
			{
				var best = 0;

				for (var i = 1; i < remainders.Length; i++)
				{
					if (remainders[i] > remainders[best])
						best = i;
				}

				result[best]++;
				remainders[best] = -1;
			}

			return result;
		}

		// TP / (BE) / SL percentages exactly as a label shows them
		private static int[] LabelPercents(bool hasBreakEven, OutcomeOdds odds)
		{
			return hasBreakEven
				? Percentages(new[] { odds.TakeProfit, odds.BreakEven, odds.StopLoss })
				: Percentages(new[] { odds.TakeProfit, odds.StopLoss });
		}

		private static int[] LabelPercents(SignalTrade trade)
		{
			return LabelPercents(trade.HasBreakEven, trade.Estimate.Odds);
		}

		// "TP 31% | BE 41% | SL 28%", or "TP 50% | SL 50%" without break-even
		private static string OddsText(bool hasBreakEven, OutcomeOdds odds, string separator)
		{
			var p = LabelPercents(hasBreakEven, odds);

			return hasBreakEven
				? $"TP {p[0]}%{separator}BE {p[1]}%{separator}SL {p[2]}%"
				: $"TP {p[0]}%{separator}SL {p[1]}%";
		}

		private static string OddsText(SignalTrade trade, string separator)
		{
			return OddsText(trade.HasBreakEven, trade.Estimate.Odds, separator);
		}

		// expected ticks rounded as the label shows them
		private static int LabelExpectedTicks(SignalTrade trade)
		{
			return (int)Math.Round(trade.Estimate.ExpectedTicks, MidpointRounding.AwayFromZero);
		}

		private static string SignedTicks(int ticks)
		{
			return ticks.ToString("+0;-0;0", CultureInfo.InvariantCulture);
		}

		private static string StatsLine(string name, int wins, int breakEvens, int losses, decimal netTicks, bool breakEven)
		{
			var counts = breakEven ? $"{wins} TP  {breakEvens} BE  {losses} SL" : $"{wins} TP  {losses} SL";

			return $"{name}  {counts}  {netTicks.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t";
		}

		private static string TrackRecord(decimal ticks, int count)
		{
			return count > 0
				? $"{count} signals, {(ticks / count).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}t avg"
				: "none yet";
		}

		private static string TallyText(OutcomeTally tally, bool breakEven)
		{
			if (tally.Count == 0)
				return "no history yet";

			return breakEven
				? $"{tally.Wins} TP / {tally.BreakEvens} BE / {tally.Losses} SL of {tally.Count}"
				: $"{tally.Wins}/{tally.Count} hit TP ({(100.0 * tally.Wins / tally.Count).ToString("0", CultureInfo.InvariantCulture)}%)";
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

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
	//   1) Fair Value Gaps (3-candle imbalances). Each gap is watched until it is used (price
	//      reacted off it), filled (price reached its far edge) or too old - then left alone.
	//   2) Reactions off an FVG, read from candlestick patterns (TraderLion's cheat sheet plus
	//      a few stronger ones): a bullish or a bearish pattern that formed at the zone.
	//   3) Big fills: the price inside each bar where the most contracts traded (footprint),
	//      and - live, from Level 2 - resting orders that were filled rather than pulled. The
	//      candles after a fill tell whether the reaction to it was bullish or bearish.
	//   4) Resting limit orders of 70+ contracts still waiting in the order book.
	//   5) Liquidity sweeps: a wick through a recent swing high / low that closes back inside.
	//   6) BUY / SHORT signals from those reactions and sweeps, each followed as a trade with an
	//      80-tick take profit and stop loss (the stop moves to +20 ticks once the trade is 40
	//      ticks in profit) and labelled with the odds of ending at TP, at the break-even stop
	//      or at SL.
	//
	// How the probabilities are estimated
	// -----------------------------------
	// Every signal is tracked as a virtual trade: entry at the signal bar's close, TP and SL a
	// fixed number of ticks away, the stop moved to break-even once the trigger trades,
	// following the order in which price traded. A signal's odds come ONLY from trades that
	// had already finished when it fired (walk-forward, no look-ahead), so the numbers on old
	// signals are exactly what the indicator would have shown live.
	//
	// Signals are grouped direction -> trigger (FVG reaction / sweep / sweep then FVG / fill
	// reaction) -> number of confirmations (EMA trend, bar delta, a big fill on the side of
	// the trade, a candlestick pattern; 0-4). A group with few trades is shrunk toward its
	// parent group (Dirichlet smoothing, per ending):
	//     p = (count + k * p_parent) / (trades + k)
	// and the top-level prior is the exact odds of a driftless random walk: 50 / 50 for a
	// plain symmetric bracket, TP 2/9 / BE 4/9 / SL 1/3 for 80 / 80 with the stop moving to
	// +20 at +40 - an expected 0 ticks. With no history every signal starts there and only
	// moves as real outcomes build up on the chart.
	//
	// Level 2: ATAS hands indicators the order book live (MarketDepthChanged, OnNewTrade), not
	// the history behind its heatmap, so resting orders and order-book fills build up while the
	// chart is open. Signals only use what history has as well - candles and footprint - so
	// they are the same after a reload.
	//
	// Tuned defaults are set for NQ, but the logic is instrument-agnostic: it reads
	// InstrumentInfo.TickSize rather than hardcoded point values.

	[DisplayName("FVG Reaction + Liquidity Sweep")]
	[Category("My Indicators")]
	public class FvgReactionLiquiditySweep : Indicator
	{
		#region Nested types

		public enum SignalMode
		{
			[Display(Name = "FVG reaction, fill reaction or sweep")]
			AnyTrigger,

			[Display(Name = "FVG reaction only")]
			FvgReactionOnly,

			[Display(Name = "Liquidity sweep only")]
			LiquiditySweepOnly,

			[Display(Name = "Sweep, then FVG reaction (confluence)")]
			SweepThenFvg,

			[Display(Name = "Fill reaction only")]
			FillReactionOnly
		}

		// When a gap counts as filled, and stops being watched
		public enum FvgFillRule
		{
			[Display(Name = "Price reaches its far edge")]
			FarEdge,

			[Display(Name = "A candle closes beyond it")]
			CloseBeyond,

			[Display(Name = "Price reaches its middle (50%)")]
			Middle
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
			SweepThenFvg = 2,
			Fill = 3
		}

		private enum TradeOutcome
		{
			Open,
			TakeProfit,
			BreakEven,    // stopped at the break-even stop after the trigger was reached
			StopLoss,
			Expired
		}

		private enum ZoneState
		{
			Active,
			Used,         // price reacted off it
			Filled,       // price traded through it
			Expired       // older than Zone Max Age
		}

		private enum OrderState
		{
			Active,
			Filled,       // it left the book while trades printed against it
			Pulled,       // it left the book without being traded
			OutOfView     // it scrolled out of the depth the feed sends
		}

		// Candlestick patterns, as found on a bar. Each one points one way.
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
			BearishMarubozu = 1 << 19,
			MorningDojiStar = 1 << 20,
			EveningDojiStar = 1 << 21,
			ThreeInsideUp = 1 << 22,
			ThreeInsideDown = 1 << 23,
			ThreeOutsideUp = 1 << 24,
			ThreeOutsideDown = 1 << 25,
			BullishOutsideReversal = 1 << 26,
			BearishOutsideReversal = 1 << 27,
			BullishThreeLineStrike = 1 << 28,
			BearishThreeLineStrike = 1 << 29
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
			public int StartBar;            // the middle (impulse) candle
			public int ConfirmedBar;        // right-hand candle - the gap only exists once it has closed
			public decimal Top;
			public decimal Bottom;
			public bool IsBullish;          // a gap below price, met on the way down (support)
			public ZoneState State;
			public int EndBar = -1;         // the bar that used, filled or expired it
			public int LastTouchBar = -1;   // the last bar that traded into it
			public bool ReactionBullish;
			public CandlePattern ReactionPatterns;
			public decimal ReactionHigh;    // the reaction bar, for its marker
			public decimal ReactionLow;
			public bool SignalShown;        // the reaction became a BUY / SHORT on the chart

			public FvgZone Clone()
			{
				return (FvgZone)MemberwiseClone();
			}
		}

		private class LiquiditySweep
		{
			public int Bar;
			public int SwingBar;            // the bar that made the high / low that was swept
			public decimal Level;
			public bool SweptLows;          // swept the lows (bullish) - else the highs
		}

		// A price where unusually many contracts were filled, and what price did next
		private class FillEvent
		{
			public int Bar;
			public decimal Price;
			public decimal Volume;          // traded at the price in that bar (or into the resting order)
			public decimal BidVolume;       // sold into the bids
			public decimal AskVolume;       // bought from the offers
			public bool BidsFilled;         // resting buy orders were filled (sellers hit them), else resting sell orders
			public bool FromFootprint;      // found in the bar's footprint; otherwise the order book only saw it live
			public decimal RestingSize;     // the resting order the order book saw filled here (0 = none)
			public int Reaction;            // +1 bullish, -1 bearish, 0 none (yet)
			public int ReactionBar = -1;
			public CandlePattern ReactionPatterns;
			public bool Decided;            // reaction found, or the window ran out
			public int WatchedBars;         // bars after the fill bar seen so far
			public decimal UpTicks;         // furthest price went above / below the fill in those bars
			public decimal DownTicks;
			public bool SignalShown;

			public FillEvent Clone()
			{
				return (FillEvent)MemberwiseClone();
			}
		}

		// A limit order of at least Min resting order contracts at one price in the order book
		private class RestingOrder
		{
			public decimal Price;
			public bool IsBid;
			public decimal Volume;
			public decimal MaxVolume;
			public int FirstBar;
			public DateTime FirstTime;
			public decimal TradedAtStart;   // traded at the price, against this side, when it was first seen
			public decimal Traded;          // traded against it since
			public OrderState State;
			public bool Leaving;            // gone from the book, waiting a moment for the trades that took it
			public bool AtBookEdge;         // it was the deepest level when it left
			public int EndBar = -1;
			public DateTime EndTime;

			public RestingOrder Clone()
			{
				return (RestingOrder)MemberwiseClone();
			}
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
			private const int Triggers = 4;
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
			public bool WithTrend;
			public bool DeltaConfirms;
			public bool FillConfirms;             // a big fill on the trade's side near the signal bar's extreme
			public CandlePattern CandlePatterns;
			public int Confirmations;
			public decimal ZoneTop;               // the FVG it reacted off (0 = none)
			public decimal ZoneBottom;
			public decimal FillPrice;             // the fill it reacted to (0 = none)
			public decimal FillVolume;
			public bool FillBidsFilled;
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
			public int Fills;
			public int BullishFills;
			public int BearishFills;
			public int RestingBids;
			public int RestingAsks;
			public RestingOrder Largest;
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

		// what the mouse is over; the most important element wins
		private sealed class Hover
		{
			public Hover(Point mouse)
			{
				Mouse = mouse;
			}

			public Point Mouse { get; }
			public int Priority { get; private set; } = -1;
			public Func<List<(string Text, Color Color)>> Lines { get; private set; }

			public void Offer(int priority, Func<List<(string Text, Color Color)>> lines)
			{
				if (priority <= Priority)
					return;

				Priority = priority;
				Lines = lines;
			}
		}

		#endregion

		#region Fields

		// EMA trend, bar delta, a supporting fill and a candlestick pattern
		private const int MaxConfirmations = 4;

		// Share of a bar's range, from the low (buys) or the high (shorts), where a fill counts
		// as "at the extreme" for the order-flow confirmation.
		private const decimal FillEdgeFraction = 0.35m;

		// Candlestick pattern shapes, as shares of the candle's range (high - low):
		// the "little or no" wick opposite a hammer's or shooting star's long one,
		private const decimal PinBarMaxOtherWick = 0.1m;

		// a doji's body - open and close "virtually equal",
		private const decimal DojiMaxBody = 0.1m;

		// how far from its high (low) each of three white soldiers (black crows) may close,
		private const decimal SoldierMaxCloseGap = 0.25m;

		// and each of a marubozu's (nearly absent) wicks.
		private const decimal MarubozuMaxWick = 0.05m;

		// A resting order that leaves the book waits this long (market time) for the trades that
		// filled it - the feed may report the book change before the prints.
		private const double OrderSettleSeconds = 2;

		// signal arrows sit this many ticks beyond the bar's low / high
		private const int ArrowOffsetTicks = 2;

		private static readonly Color BullColor = Color.FromArgb(255, 38, 166, 154);
		private static readonly Color BearColor = Color.FromArgb(255, 239, 83, 80);
		private static readonly Color NeutralColor = Color.FromArgb(255, 144, 150, 162);
		private static readonly Color BidColor = Color.FromArgb(255, 66, 165, 245);
		private static readonly Color AskColor = Color.FromArgb(255, 255, 167, 38);
		private static readonly Color TextColor = Color.FromArgb(255, 226, 230, 236);
		private static readonly Color DimTextColor = Color.FromArgb(255, 156, 163, 175);
		private static readonly Color CardColor = Color.FromArgb(226, 22, 25, 31);
		private static readonly Color CardBorderColor = Color.FromArgb(255, 58, 63, 74);

		// Every pattern, strongest first: labels list them in this order, and when a bar shows
		// both a bullish and a bearish pattern the stronger one decides. Twins share a rank.
		private static readonly (CandlePattern Pattern, string Name, int Candles)[] CandlePatternInfo =
		{
			(CandlePattern.BullishThreeLineStrike, "Bullish three-line strike", 4),
			(CandlePattern.BearishThreeLineStrike, "Bearish three-line strike", 4),
			(CandlePattern.MorningDojiStar, "Morning doji star", 3),
			(CandlePattern.EveningDojiStar, "Evening doji star", 3),
			(CandlePattern.MorningStar, "Morning star", 3),
			(CandlePattern.EveningStar, "Evening star", 3),
			(CandlePattern.ThreeOutsideUp, "Three outside up", 3),
			(CandlePattern.ThreeOutsideDown, "Three outside down", 3),
			(CandlePattern.ThreeWhiteSoldiers, "Three white soldiers", 3),
			(CandlePattern.ThreeBlackCrows, "Three black crows", 3),
			(CandlePattern.BullishOutsideReversal, "Bullish outside reversal", 2),
			(CandlePattern.BearishOutsideReversal, "Bearish outside reversal", 2),
			(CandlePattern.BullishEngulfing, "Bullish engulfing", 2),
			(CandlePattern.BearishEngulfing, "Bearish engulfing", 2),
			(CandlePattern.ThreeInsideUp, "Three inside up", 3),
			(CandlePattern.ThreeInsideDown, "Three inside down", 3),
			(CandlePattern.PiercingLine, "Piercing line", 2),
			(CandlePattern.DarkCloudCover, "Dark cloud cover", 2),
			(CandlePattern.Hammer, "Hammer", 1),
			(CandlePattern.ShootingStar, "Shooting star", 1),
			(CandlePattern.DragonflyDoji, "Dragonfly doji", 1),
			(CandlePattern.GravestoneDoji, "Gravestone doji", 1),
			(CandlePattern.TweezerBottom, "Tweezer bottom", 2),
			(CandlePattern.TweezerTop, "Tweezer top", 2),
			(CandlePattern.BullishMarubozu, "Bullish marubozu", 1),
			(CandlePattern.BearishMarubozu, "Bearish marubozu", 1),
			(CandlePattern.BullishHarami, "Bullish harami", 2),
			(CandlePattern.BearishHarami, "Bearish harami", 2),
			(CandlePattern.InvertedHammer, "Inverted hammer", 1),
			(CandlePattern.HangingMan, "Hanging man", 1)
		};

		// OnRender and the order-book callbacks run on other threads than OnCalculate;
		// everything below that they share is guarded by this lock.
		private readonly object _sync = new object();

		private readonly List<FvgZone> _activeZones = new List<FvgZone>();
		private readonly List<FvgZone> _retiredZones = new List<FvgZone>();
		private readonly List<LiquiditySweep> _sweeps = new List<LiquiditySweep>();

		private readonly List<FillEvent> _fills = new List<FillEvent>();
		private readonly List<FillEvent> _watchedFills = new List<FillEvent>();       // still inside their reaction window
		private readonly Dictionary<int, FillEvent> _footprintFills = new Dictionary<int, FillEvent>();

		// what the order book saw filled, by bar, price and side: the biggest resting order, and
		// the fill of its own when one was big enough to be shown as one
		private readonly Dictionary<(int Bar, decimal Price, bool IsBid), decimal> _filledRestingSize = new Dictionary<(int Bar, decimal Price, bool IsBid), decimal>();
		private readonly Dictionary<(int Bar, decimal Price, bool IsBid), FillEvent> _orderBookFills = new Dictionary<(int Bar, decimal Price, bool IsBid), FillEvent>();

		// the order book, and the big resting orders in it
		private readonly Dictionary<decimal, decimal> _bids = new Dictionary<decimal, decimal>();
		private readonly Dictionary<decimal, decimal> _asks = new Dictionary<decimal, decimal>();
		private readonly Dictionary<decimal, RestingOrder> _restingBids = new Dictionary<decimal, RestingOrder>();
		private readonly Dictionary<decimal, RestingOrder> _restingAsks = new Dictionary<decimal, RestingOrder>();
		private readonly List<RestingOrder> _endedOrders = new List<RestingOrder>();

		// live traded volume per price: sold into the bids, bought from the offers
		private readonly Dictionary<decimal, decimal> _soldAt = new Dictionary<decimal, decimal>();
		private readonly Dictionary<decimal, decimal> _boughtAt = new Dictionary<decimal, decimal>();

		private readonly List<SignalTrade> _trades = new List<SignalTrade>();
		private readonly List<SignalTrade> _openTrades = new List<SignalTrade>();
		private readonly List<decimal> _ema = new List<decimal>();
		private readonly ProbabilityModel _model = new ProbabilityModel();

		// patterns of the bar being processed, per direction and context
		private readonly CandlePattern?[] _patternCache = new CandlePattern?[4];
		private int _patternCacheBar = -1;

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

		// Series on the price panel. The four trigger series stay hidden (the chart draws its own
		// markers for them) but keep their values for ATAS alerts and automation; the ids keep
		// the original names so saved chart templates still find them. ValueDataSeries.Color is
		// ATAS's CrossColor (WPF Color on Windows, System.Drawing.Color on ATAS X), so colors go
		// through .Convert().
		private readonly ValueDataSeries _bullReaction = new ValueDataSeries("FVG Bull Reaction", "FVG Bull Reaction")
		{
			VisualType = VisualMode.Hide,
			Color = BullColor.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _bearReaction = new ValueDataSeries("FVG Bear Reaction", "FVG Bear Reaction")
		{
			VisualType = VisualMode.Hide,
			Color = BearColor.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _bullSweep = new ValueDataSeries("Liquidity Sweep Low", "Liquidity Sweep Low")
		{
			VisualType = VisualMode.Hide,
			Color = BullColor.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _bearSweep = new ValueDataSeries("Liquidity Sweep High", "Liquidity Sweep High")
		{
			VisualType = VisualMode.Hide,
			Color = BearColor.Convert(),
			Width = 3,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _buySignal = new ValueDataSeries("Buy Signal", "Buy Signal")
		{
			VisualType = VisualMode.UpArrow,
			Color = BullColor.Convert(),
			Width = 4,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private readonly ValueDataSeries _shortSignal = new ValueDataSeries("Short Signal", "Short Signal")
		{
			VisualType = VisualMode.DownArrow,
			Color = BearColor.Convert(),
			Width = 4,
			ShowZeroValue = false,
			ShowCurrentValue = false
		};

		private int _swingLookback = 10;
		private bool _showSweeps = true;

		private int _minFvgTicks = 4;
		private int _maxZoneAgeBars = 150;
		private bool _requireCloseThroughZone = true;
		private FvgFillRule _fvgFill = FvgFillRule.FarEdge;
		private bool _drawZones = true;
		private bool _showUsedZones;
		private bool _showZoneMidline = true;
		private Color _bullishZoneColor = Color.FromArgb(40, 38, 166, 154);
		private Color _bearishZoneColor = Color.FromArgb(40, 239, 83, 80);

		private bool _showFills = true;
		private int _fillMinVolume = 150;
		private double _fillVolumeMultiplier = 3.0;
		private int _reactionBars = 3;
		private bool _showRestingOrders = true;
		private int _restingOrderMin = 70;
		private int _orderFilledPercent = 50;
		private bool _showPulledOrders;

		private bool _patternHammer = true;
		private bool _patternInvertedHammer = true;
		private bool _patternEngulfing = true;
		private bool _patternPiercingLine = true;
		private bool _patternHarami = true;
		private bool _patternTweezers = true;
		private bool _patternStars = true;
		private bool _patternThreeSoldiers = true;
		private bool _patternMarubozu = true;
		private bool _patternThreeInside = true;
		private bool _patternThreeOutside = true;
		private bool _patternOutsideReversal = true;
		private bool _patternThreeLineStrike = true;
		private double _pinBarWickRatio = 2.0;
		private int _patternAverageBars = 14;
		private int _tweezerToleranceTicks = 1;

		private bool _enableBuySignals = true;
		private bool _enableShortSignals = true;
		private SignalMode _signalSource = SignalMode.AnyTrigger;
		private int _confluenceBars = 10;
		private int _trendEmaPeriod = 50;
		private bool _onlyWithTrend;
		private bool _requireDeltaConfirmation;
		private bool _requireFillConfirmation;
		private bool _requireCandlePattern;
		private int _signalCooldownBars = 3;
		private bool _oneTradeAtATime = true;
		private int _minProbabilityPercent;
		private int _minExpectedTicks;

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

		[Display(Name = "Show sweeps", GroupName = "Liquidity Sweep", Order = 11,
			Description = "A dotted line from the swing high / low to the bar that swept it.")]
		public bool ShowSweeps
		{
			get => _showSweeps;
			set { _showSweeps = value; RedrawChart(); }
		}

		[Display(Name = "Min FVG Size (ticks)", GroupName = "Fair Value Gap", Order = 20)]
		[Range(0, 10000)]
		public int MinFvgTicks
		{
			get => _minFvgTicks;
			set { _minFvgTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Zone Max Age (bars)", GroupName = "Fair Value Gap", Order = 21,
			Description = "A gap nothing has used or filled after this many bars is no longer watched.")]
		[Range(5, 100000)]
		public int MaxZoneAgeBars
		{
			get => _maxZoneAgeBars;
			set { _maxZoneAgeBars = Math.Max(5, value); RecalculateValues(); }
		}

		[Display(Name = "Require reaction close beyond zone", GroupName = "Fair Value Gap", Order = 22,
			Description = "A bullish reaction must close above the zone, a bearish one below it - not just form a pattern inside it.")]
		public bool RequireCloseThroughZone
		{
			get => _requireCloseThroughZone;
			set { _requireCloseThroughZone = value; RecalculateValues(); }
		}

		[Display(Name = "FVG is filled when", GroupName = "Fair Value Gap", Order = 23,
			Description = "A filled gap stops being watched, like a used one (a reaction on the same bar counts first).")]
		public FvgFillRule FvgFill
		{
			get => _fvgFill;
			set { _fvgFill = value; RecalculateValues(); }
		}

		[Display(Name = "Draw FVG Zones", GroupName = "Fair Value Gap", Order = 24)]
		public bool DrawZones
		{
			get => _drawZones;
			set { _drawZones = value; RedrawChart(); }
		}

		[Display(Name = "Show used / filled zones", GroupName = "Fair Value Gap", Order = 25,
			Description = "Keep a faint box, ending where it was used or filled, for gaps that are no longer watched.")]
		public bool ShowUsedZones
		{
			get => _showUsedZones;
			set { _showUsedZones = value; RedrawChart(); }
		}

		[Display(Name = "Show zone midline (50%)", GroupName = "Fair Value Gap", Order = 26)]
		public bool ShowZoneMidline
		{
			get => _showZoneMidline;
			set { _showZoneMidline = value; RedrawChart(); }
		}

		[Display(Name = "Bullish zone color", GroupName = "Fair Value Gap", Order = 27)]
		public Color BullishZoneColor
		{
			get => _bullishZoneColor;
			set { _bullishZoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Bearish zone color", GroupName = "Fair Value Gap", Order = 28)]
		public Color BearishZoneColor
		{
			get => _bearishZoneColor;
			set { _bearishZoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Show big fills", GroupName = "Order Flow", Order = 50,
			Description = "A bubble where the most contracts traded inside a bar, colored by the reaction that followed: green bullish, red bearish, gray none (yet). A white ring: the order book saw a resting order filled there.")]
		public bool ShowFills
		{
			get => _showFills;
			set { _showFills = value; RedrawChart(); }
		}

		[Display(Name = "Min filled volume at one price", GroupName = "Order Flow", Order = 51,
			Description = "Contracts that must trade at a single price inside one bar before it counts as a big fill.")]
		[Range(1, 100000000)]
		public int FillMinVolume
		{
			get => _fillMinVolume;
			set { _fillMinVolume = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Filled volume vs bar average (x)", GroupName = "Order Flow", Order = 52,
			Description = "The price must also trade this many times the bar's average volume per price.")]
		[Range(1.0, 1000.0)]
		public double FillVolumeMultiplier
		{
			get => _fillVolumeMultiplier;
			set { _fillVolumeMultiplier = Math.Max(1.0, value); RecalculateValues(); }
		}

		[Display(Name = "Reaction window (bars)", GroupName = "Order Flow", Order = 53,
			Description = "How many bars after a big fill a candlestick pattern may take to show the reaction to it.")]
		[Range(0, 100)]
		public int ReactionBars
		{
			get => _reactionBars;
			set { _reactionBars = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Show resting orders", GroupName = "Order Flow", Order = 54,
			Description = "Live from Level 2: limit orders of at least Min resting order contracts still waiting in the order book, with their size at the right edge.")]
		public bool ShowRestingOrders
		{
			get => _showRestingOrders;
			set { _showRestingOrders = value; RedrawChart(); }
		}

		[Display(Name = "Min resting order (contracts)", GroupName = "Order Flow", Order = 55)]
		[Range(1, 100000000)]
		public int RestingOrderMin
		{
			get => _restingOrderMin;
			set { _restingOrderMin = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Filled when traded (%)", GroupName = "Order Flow", Order = 56,
			Description = "A resting order that leaves the book counts as filled once trades at its price took at least this share of its size; otherwise it was pulled.")]
		[Range(1, 100)]
		public int OrderFilledPercent
		{
			get => _orderFilledPercent;
			set { _orderFilledPercent = Math.Min(100, Math.Max(1, value)); RecalculateValues(); }
		}

		[Display(Name = "Show pulled orders", GroupName = "Order Flow", Order = 57,
			Description = "Keep a faint trace of big orders that were cancelled instead of filled.")]
		public bool ShowPulledOrders
		{
			get => _showPulledOrders;
			set { _showPulledOrders = value; RedrawChart(); }
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

		[Display(Name = "Require order-flow confirmation", GroupName = "Signals", Order = 107,
			Description = "Buys need a big fill of resting bids near the low of the signal bar (or the bar before), shorts a big fill of resting offers near the high.")]
		public bool RequireFillConfirmation
		{
			get => _requireFillConfirmation;
			set { _requireFillConfirmation = value; RecalculateValues(); }
		}

		[Display(Name = "Require candlestick pattern", GroupName = "Signals", Order = 108,
			Description = "Only signal when the signal bar completes a candlestick pattern pointing the signal's way. FVG and fill reactions always have one; this filters sweeps.")]
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

		[Display(Name = "Hammer / Shooting star", GroupName = "Candlestick Patterns", Order = 150,
			Description = "After a decline: a small body at the top, a lower wick at least 'wick / body' times the body and little or no upper wick (a dragonfly doji when the body is almost nil). After a rally: the same upside down - shooting star, gravestone doji.")]
		public bool PatternHammer
		{
			get => _patternHammer;
			set { _patternHammer = value; RecalculateValues(); }
		}

		[Display(Name = "Inverted hammer / Hanging man", GroupName = "Candlestick Patterns", Order = 151,
			Description = "The same shapes the other way up: an inverted hammer (long upper wick) after a decline, a hanging man (long lower wick) after a rally.")]
		public bool PatternInvertedHammer
		{
			get => _patternInvertedHammer;
			set { _patternInvertedHammer = value; RecalculateValues(); }
		}

		[Display(Name = "Engulfing", GroupName = "Candlestick Patterns", Order = 152,
			Description = "A candle whose body covers the whole body of the opposite-colored candle before it, and is at least an average body.")]
		public bool PatternEngulfing
		{
			get => _patternEngulfing;
			set { _patternEngulfing = value; RecalculateValues(); }
		}

		[Display(Name = "Piercing line / Dark cloud cover", GroupName = "Candlestick Patterns", Order = 153,
			Description = "After a long bearish candle, a bullish one from its close or lower that closes above the middle of its body, but not above its open (piercing line). The mirror image: dark cloud cover.")]
		public bool PatternPiercingLine
		{
			get => _patternPiercingLine;
			set { _patternPiercingLine = value; RecalculateValues(); }
		}

		[Display(Name = "Harami", GroupName = "Candlestick Patterns", Order = 154,
			Description = "A long candle, then a small candle of the other color whose body stays inside the first candle's body.")]
		public bool PatternHarami
		{
			get => _patternHarami;
			set { _patternHarami = value; RecalculateValues(); }
		}

		[Display(Name = "Tweezer bottom / top", GroupName = "Candlestick Patterns", Order = 155,
			Description = "A bearish then a bullish candle with the same low, or a bullish then a bearish candle with the same high.")]
		public bool PatternTweezers
		{
			get => _patternTweezers;
			set { _patternTweezers = value; RecalculateValues(); }
		}

		[Display(Name = "Morning star / Evening star", GroupName = "Candlestick Patterns", Order = 156,
			Description = "A long bearish candle, a small one (the star) no higher than the middle of its body, then a long bullish candle closing above that middle - a morning doji star when the star is a doji. The mirror image: evening (doji) star.")]
		public bool PatternStars
		{
			get => _patternStars;
			set { _patternStars = value; RecalculateValues(); }
		}

		[Display(Name = "Three white soldiers / black crows", GroupName = "Candlestick Patterns", Order = 157,
			Description = "Three long bullish candles, each opening inside the body before it, closing higher and near its high. The mirror image: three black crows.")]
		public bool PatternThreeSoldiers
		{
			get => _patternThreeSoldiers;
			set { _patternThreeSoldiers = value; RecalculateValues(); }
		}

		[Display(Name = "Marubozu", GroupName = "Candlestick Patterns", Order = 158,
			Description = "A long candle with (almost) no wicks: it opened at one end of its range and closed at the other.")]
		public bool PatternMarubozu
		{
			get => _patternMarubozu;
			set { _patternMarubozu = value; RecalculateValues(); }
		}

		[Display(Name = "Three inside up / down", GroupName = "Candlestick Patterns", Order = 159,
			Description = "A harami that is confirmed: long bearish candle, small candle inside its body, then a bullish candle closing above the first one's open. The mirror image: three inside down.")]
		public bool PatternThreeInside
		{
			get => _patternThreeInside;
			set { _patternThreeInside = value; RecalculateValues(); }
		}

		[Display(Name = "Three outside up / down", GroupName = "Candlestick Patterns", Order = 160,
			Description = "An engulfing that is confirmed: the candle after a bullish engulfing closes higher still. The mirror image: three outside down.")]
		public bool PatternThreeOutside
		{
			get => _patternThreeOutside;
			set { _patternThreeOutside = value; RecalculateValues(); }
		}

		[Display(Name = "Outside reversal", GroupName = "Candlestick Patterns", Order = 161,
			Description = "Key reversal: a candle trades below the previous low and above the previous high, then closes beyond the previous high (bullish) or low (bearish).")]
		public bool PatternOutsideReversal
		{
			get => _patternOutsideReversal;
			set { _patternOutsideReversal = value; RecalculateValues(); }
		}

		[Display(Name = "Three-line strike", GroupName = "Candlestick Patterns", Order = 162,
			Description = "Three bearish candles closing lower each time, then a bullish candle from their close to above the first one's open, wiping them out (bullish). The mirror image is bearish.")]
		public bool PatternThreeLineStrike
		{
			get => _patternThreeLineStrike;
			set { _patternThreeLineStrike = value; RecalculateValues(); }
		}

		[Display(Name = "Hammer wick / body (min)", GroupName = "Candlestick Patterns", Order = 170,
			Description = "How many times the body the long wick of a hammer, shooting star, inverted hammer or hanging man must be. The cheat sheet says at least 2.")]
		[Range(1.0, 100.0)]
		public double PinBarWickRatio
		{
			get => _pinBarWickRatio;
			set { _pinBarWickRatio = Math.Max(1.0, value); RecalculateValues(); }
		}

		[Display(Name = "Average body (bars)", GroupName = "Candlestick Patterns", Order = 171,
			Description = "Long and small bodies are measured against the average body of this many candles before the pattern.")]
		[Range(1, 1000)]
		public int PatternAverageBars
		{
			get => _patternAverageBars;
			set { _patternAverageBars = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Tweezer match (ticks)", GroupName = "Candlestick Patterns", Order = 172,
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

		[Display(Name = "Compact labels for closed trades", GroupName = "Display", Order = 301,
			Description = "Once a trade has ended, its card shrinks to a small result chip (TP +80t, BE +20t, SL -80t). Hover it for the details.")]
		public bool CompactClosedLabels { get; set; } = true;

		[Display(Name = "Show TP / SL levels", GroupName = "Display", Order = 302,
			Description = "The trade boxes: strong for the open trade, faint for the ones that have ended.")]
		public bool ShowTradeLevels { get; set; } = true;

		[Display(Name = "Show reaction labels", GroupName = "Display", Order = 303,
			Description = "Name the candlestick pattern next to each FVG reaction that did not become a signal on the chart (hovering its marker always does).")]
		public bool ShowReactionLabels { get; set; }

		[Display(Name = "Show statistics panel", GroupName = "Display", Order = 304)]
		public bool ShowStatsPanel { get; set; } = true;

		[Display(Name = "Statistics panel position", GroupName = "Display", Order = 305)]
		public PanelCorner StatsPanelLocation { get; set; } = PanelCorner.TopRight;

		[Display(Name = "Label offset (px)", GroupName = "Display", Order = 306,
			Description = "Distance between the tip of the signal arrow and its label, so the label clears the arrow.")]
		[Range(0, 500)]
		public int LabelOffset { get; set; } = 24;

		[Display(Name = "Label font", GroupName = "Display", Order = 307)]
		public FontSetting LabelFont { get; set; } = new FontSetting("Arial", 9);

		[Display(Name = "Take profit line", GroupName = "Display", Order = 308)]
		public PenSettings TakeProfitPen { get; set; } = new PenSettings { Color = BullColor.Convert(), Width = 1 };

		[Display(Name = "Stop loss line", GroupName = "Display", Order = 309)]
		public PenSettings StopLossPen { get; set; } = new PenSettings { Color = BearColor.Convert(), Width = 1 };

		[Display(Name = "Break-even line", GroupName = "Display", Order = 310)]
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
			// render (new ticks, scrolling, mouse moves), which the hover tooltips need.
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
				{
					ResetState();
					LoadOrderBook();
				}

				// a closed bar is final - ATAS only repeats the last (forming) one, but if an
				// older bar is ever sent again it must not clear that bar's signal
				if (bar <= _lastClosedBar)
					return;

				// Everything that decides a signal runs exactly once per bar, on the CLOSED bar:
				// when the first tick of a new bar arrives, the previous one is final.
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

				// History is done once the last bar has been calculated; from here on signals
				// and TP/SL hits happen in real time and may raise alerts.
				if (bar == CurrentBar - 1)
				{
					_realtime = true;
					SettleLeavingOrders();
				}
			}

			if (alerts != null)
				FireAlerts(alerts);
		}

		private void ResetState()
		{
			_activeZones.Clear();
			_retiredZones.Clear();
			_sweeps.Clear();
			_fills.Clear();
			_watchedFills.Clear();
			_footprintFills.Clear();
			_filledRestingSize.Clear();
			_orderBookFills.Clear();
			_bids.Clear();
			_asks.Clear();
			_restingBids.Clear();
			_restingAsks.Clear();
			_endedOrders.Clear();
			_soldAt.Clear();
			_boughtAt.Clear();
			_trades.Clear();
			_openTrades.Clear();
			_ema.Clear();
			_model.Reset();
			_patternCacheBar = -1;

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
			_patternCacheBar = bar;
			Array.Clear(_patternCache, 0, _patternCache.Length);

			// the bar's final footprint - the last intrabar update can be a tick behind
			DetectFootprintFill(bar, candle);

			AdvanceOpenTrades(bar, candle, ref alerts);
			ExpireStaleTrades(bar, candle, ref alerts);
			UpdateEma(bar, candle.Close);

			// bar + 1 exists (that's why this bar is closed), so we already know whether
			// it opens a new session
			var sessionEnds = ExpireAtSessionEnd && IsNewSession(bar + 1);

			if (sessionEnds)
				ExpireAllOpenTrades(bar, candle, ref alerts);

			var (bullFill, bearFill) = UpdateFillReactions(bar, candle);

			if (bar < SwingLookback + 3)
				return;

			DetectNewFvg(bar);
			CheckLiquiditySweep(bar, candle, out var sweptLows, out var sweptHighs);
			var (bullZone, bearZone) = CheckZoneReactions(bar, candle);

			if (sweptLows)
				_lastLowSweepBar = bar;

			if (sweptHighs)
				_lastHighSweepBar = bar;

			// no new trades on the last bar of a session when trades close at session end
			if (!sessionEnds)
				GenerateSignals(bar, candle, bullZone, bearZone, bullFill, bearFill, sweptLows, sweptHighs, ref alerts);
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

		#endregion

		#region Fair value gaps and sweeps

		// Classic 3-candle imbalance: gap between candle[bar-2] and candle[bar], created by the
		// large range of the middle candle[bar-1].
		private void DetectNewFvg(int bar)
		{
			var left = GetCandle(bar - 2);
			var right = GetCandle(bar);

			// at least one tick of empty space, even with Min FVG Size = 0
			var minGap = Math.Max(MinFvgTicks, 1) * TickSize;

			// Bullish FVG: left.High sits below right.Low
			if (right.Low - left.High >= minGap)
				_activeZones.Add(new FvgZone { StartBar = bar - 1, ConfirmedBar = bar, Top = right.Low, Bottom = left.High, IsBullish = true });

			// Bearish FVG: left.Low sits above right.High
			if (left.Low - right.High >= minGap)
				_activeZones.Add(new FvgZone { StartBar = bar - 1, ConfirmedBar = bar, Top = left.Low, Bottom = right.High, IsBullish = false });
		}

		// A reaction = a candlestick pattern that formed at the zone: one of its candles traded
		// into the gap. Price comes down into a bullish gap and up into a bearish one, which
		// is the "after a decline / rally" the cheat sheet reads the hammer shapes by. The
		// stronger pattern decides between a bullish and a bearish one. A zone is used by its
		// first reaction and stops being watched, like a filled or expired one. Returns the
		// strongest bullish and bearish reaction of the bar.
		private (FvgZone Bullish, FvgZone Bearish) CheckZoneReactions(int bar, IndicatorCandle candle)
		{
			FvgZone bullish = null;
			FvgZone bearish = null;

			for (var i = 0; i < _activeZones.Count; i++)
			{
				var zone = _activeZones[i];

				// the candle that completes the gap sits right on its edge - retests start after it
				if (bar <= zone.ConfirmedBar)
					continue;

				if (candle.Low <= zone.Top && candle.High >= zone.Bottom)
					zone.LastTouchBar = bar;

				if (zone.LastTouchBar >= 0)
				{
					// only patterns long enough to include the candle that touched the zone
					var span = PatternsOfAtLeast(bar - zone.LastTouchBar + 1);
					var bull = FindCandlePatterns(bar, true, zone.IsBullish) & span;
					var bear = FindCandlePatterns(bar, false, zone.IsBullish) & span;

					if (RequireCloseThroughZone)
					{
						if (candle.Close <= zone.Top)
							bull = CandlePattern.None;

						if (candle.Close >= zone.Bottom)
							bear = CandlePattern.None;
					}

					var reaction = DecideReaction(bull, bear);

					if (reaction != 0)
					{
						zone.ReactionBullish = reaction > 0;
						zone.ReactionPatterns = reaction > 0 ? bull : bear;
						zone.ReactionHigh = candle.High;
						zone.ReactionLow = candle.Low;
						Retire(zone, ZoneState.Used, bar);

						if (zone.ReactionBullish)
						{
							_bullReaction[bar] = candle.Low - ArrowOffsetTicks * TickSize;
							bullish = StrongerReaction(bullish, zone);
						}
						else
						{
							_bearReaction[bar] = candle.High + ArrowOffsetTicks * TickSize;
							bearish = StrongerReaction(bearish, zone);
						}

						continue;
					}
				}

				if (IsFilled(zone, candle))
					Retire(zone, ZoneState.Filled, bar);
				else if (bar - zone.StartBar > MaxZoneAgeBars)
					Retire(zone, ZoneState.Expired, bar);
			}

			_activeZones.RemoveAll(z => z.State != ZoneState.Active);
			return (bullish, bearish);
		}

		private bool IsFilled(FvgZone zone, IndicatorCandle candle)
		{
			var middle = (zone.Top + zone.Bottom) / 2;

			switch (FvgFill)
			{
				case FvgFillRule.CloseBeyond:
					return zone.IsBullish ? candle.Close < zone.Bottom : candle.Close > zone.Top;

				case FvgFillRule.Middle:
					return zone.IsBullish ? candle.Low <= middle : candle.High >= middle;

				default:
					return zone.IsBullish ? candle.Low <= zone.Bottom : candle.High >= zone.Top;
			}
		}

		private void Retire(FvgZone zone, ZoneState state, int bar)
		{
			zone.State = state;
			zone.EndBar = bar;
			_retiredZones.Add(zone);
		}

		// the stronger pattern, then the more recent gap (the first one found on a tie)
		private static FvgZone StrongerReaction(FvgZone best, FvgZone zone)
		{
			if (best == null)
				return zone;

			var rank = BestRank(zone.ReactionPatterns);
			var bestRank = BestRank(best.ReactionPatterns);
			return rank < bestRank || (rank == bestRank && zone.ConfirmedBar > best.ConfirmedBar) ? zone : best;
		}

		// Liquidity sweep = current bar wicks beyond the prior N-bar high/low, then closes back
		// inside it -> stop run / liquidity grab.
		private void CheckLiquiditySweep(int bar, IndicatorCandle candle, out bool sweptLows, out bool sweptHighs)
		{
			var highestHigh = decimal.MinValue;
			var lowestLow = decimal.MaxValue;
			var highBar = -1;
			var lowBar = -1;

			for (var i = Math.Max(0, bar - SwingLookback); i < bar; i++)
			{
				var c = GetCandle(i);

				// the latest bar at the extreme, so the sweep line is as short as it can be
				if (c.High >= highestHigh)
				{
					highestHigh = c.High;
					highBar = i;
				}

				if (c.Low <= lowestLow)
				{
					lowestLow = c.Low;
					lowBar = i;
				}
			}

			sweptHighs = candle.High > highestHigh && candle.Close < highestHigh;
			sweptLows = candle.Low < lowestLow && candle.Close > lowestLow;
			var offset = ArrowOffsetTicks * TickSize;

			if (sweptHighs)
			{
				_bearSweep[bar] = candle.High + offset;
				_sweeps.Add(new LiquiditySweep { Bar = bar, SwingBar = highBar, Level = highestHigh });
			}

			if (sweptLows)
			{
				_bullSweep[bar] = candle.Low - offset;
				_sweeps.Add(new LiquiditySweep { Bar = bar, SwingBar = lowBar, Level = lowestLow, SweptLows = true });
			}
		}

		#endregion

		#region Candlestick patterns

		// The patterns that complete on `bar` and point one way. The hammer shapes are read by
		// what came before them: after a decline a long lower wick is a hammer and a long upper
		// wick an inverted hammer (bullish); after a rally they are a hanging man and a shooting
		// star (bearish). All other patterns point the same way wherever they appear. Only
		// closed bars are read.
		private CandlePattern FindCandlePatterns(int bar, bool bullish, bool afterDecline)
		{
			var slot = (bullish ? 2 : 0) + (afterDecline ? 1 : 0);

			if (bar == _patternCacheBar && _patternCache[slot].HasValue)
				return _patternCache[slot].Value;

			var c = new CandleShape(GetCandle(bar));
			var found = SingleCandlePatterns(c, AverageBody(bar), bullish, afterDecline);

			if (bar >= 1)
			{
				var p = new CandleShape(GetCandle(bar - 1));
				found |= TwoCandlePatterns(p, c, AverageBody(bar - 1), bullish);

				if (bar >= 2)
				{
					var a = new CandleShape(GetCandle(bar - 2));
					found |= ThreeCandlePatterns(a, p, c, AverageBody(bar - 2), bullish);

					if (bar >= 3)
						found |= FourCandlePatterns(new CandleShape(GetCandle(bar - 3)), a, p, c, AverageBody(bar - 3), bullish);
				}
			}

			if (bar == _patternCacheBar)
				_patternCache[slot] = found;

			return found;
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

		private CandlePattern SingleCandlePatterns(CandleShape c, decimal averageBody, bool bullish, bool afterDecline)
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

			if (bullish)
			{
				if (afterDecline && PatternHammer && longLowerWick)
					found |= doji ? CandlePattern.DragonflyDoji : CandlePattern.Hammer;

				if (afterDecline && PatternInvertedHammer && longUpperWick)
					found |= CandlePattern.InvertedHammer;

				if (PatternMarubozu && marubozu && c.IsBullish)
					found |= CandlePattern.BullishMarubozu;
			}
			else
			{
				if (!afterDecline && PatternHammer && longUpperWick)
					found |= doji ? CandlePattern.GravestoneDoji : CandlePattern.ShootingStar;

				if (!afterDecline && PatternInvertedHammer && longLowerWick)
					found |= CandlePattern.HangingMan;

				if (PatternMarubozu && marubozu && c.IsBearish)
					found |= CandlePattern.BearishMarubozu;
			}

			return found;
		}

		// p = the candle before c. Futures rarely gap between bars, so "opens below the
		// previous close" is taken as "at or below".
		private CandlePattern TwoCandlePatterns(CandleShape p, CandleShape c, decimal averageBody, bool bullish)
		{
			var tolerance = TweezerToleranceTicks * TickSize;
			var outside = c.High > p.High && c.Low < p.Low;
			var found = CandlePattern.None;

			if (bullish)
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

				// takes out the previous low, then closes above the previous high
				if (PatternOutsideReversal && outside && c.IsBullish && c.Close > p.High)
					found |= CandlePattern.BullishOutsideReversal;
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

				if (PatternOutsideReversal && outside && c.IsBearish && c.Close < p.Low)
					found |= CandlePattern.BearishOutsideReversal;
			}

			return found;
		}

		// a, p = the two candles before c
		private CandlePattern ThreeCandlePatterns(CandleShape a, CandleShape p, CandleShape c, decimal averageBody, bool bullish)
		{
			var found = CandlePattern.None;

			// each candle opens inside the previous body
			var opensInside = p.Open >= a.Bottom && p.Open <= a.Top && c.Open >= p.Bottom && c.Open <= p.Top;

			// the middle candle is small and its body stays inside the first one's
			var harami = IsLongBody(a, averageBody) && IsSmallBody(p, averageBody) && p.Top <= a.Top && p.Bottom >= a.Bottom;
			var starIsDoji = p.Body <= DojiMaxBody * p.Range;

			if (bullish)
			{
				// a long bearish candle, a small star no higher than the middle of its body, then
				// a long bullish candle closing above that middle
				if (PatternStars && a.IsBearish && IsLongBody(a, averageBody) && IsSmallBody(p, averageBody) && p.Top <= a.Middle
					&& c.IsBullish && IsLongBody(c, averageBody) && c.Close > a.Middle)
					found |= starIsDoji ? CandlePattern.MorningDojiStar : CandlePattern.MorningStar;

				if (PatternThreeSoldiers && IsSoldier(a, averageBody) && IsSoldier(p, averageBody) && IsSoldier(c, averageBody)
					&& opensInside && p.Close > a.Close && c.Close > p.Close)
					found |= CandlePattern.ThreeWhiteSoldiers;

				// a bearish harami, confirmed by a bullish candle closing above the first one's open
				if (PatternThreeInside && a.IsBearish && harami && c.IsBullish && c.Close > a.Open)
					found |= CandlePattern.ThreeInsideUp;

				// a bullish engulfing, confirmed by a close higher still
				if (PatternThreeOutside && a.IsBearish && p.IsBullish && p.Open <= a.Close && p.Close >= a.Open
					&& p.Body > a.Body && IsLongBody(p, averageBody) && c.Close > p.Close)
					found |= CandlePattern.ThreeOutsideUp;
			}
			else
			{
				if (PatternStars && a.IsBullish && IsLongBody(a, averageBody) && IsSmallBody(p, averageBody) && p.Bottom >= a.Middle
					&& c.IsBearish && IsLongBody(c, averageBody) && c.Close < a.Middle)
					found |= starIsDoji ? CandlePattern.EveningDojiStar : CandlePattern.EveningStar;

				if (PatternThreeSoldiers && IsCrow(a, averageBody) && IsCrow(p, averageBody) && IsCrow(c, averageBody)
					&& opensInside && p.Close < a.Close && c.Close < p.Close)
					found |= CandlePattern.ThreeBlackCrows;

				if (PatternThreeInside && a.IsBullish && harami && c.IsBearish && c.Close < a.Open)
					found |= CandlePattern.ThreeInsideDown;

				if (PatternThreeOutside && a.IsBullish && p.IsBearish && p.Open >= a.Close && p.Close <= a.Open
					&& p.Body > a.Body && IsLongBody(p, averageBody) && c.Close < p.Close)
					found |= CandlePattern.ThreeOutsideDown;
			}

			return found;
		}

		// x1, x2, x3 = the three candles before c
		private CandlePattern FourCandlePatterns(CandleShape x1, CandleShape x2, CandleShape x3, CandleShape c, decimal averageBody,
			bool bullish)
		{
			if (!PatternThreeLineStrike)
				return CandlePattern.None;

			// three candles one way, closing further each time, then one candle that starts from
			// the last close and wipes out all three
			if (bullish)
			{
				return x1.IsBearish && x2.IsBearish && x3.IsBearish && x2.Close < x1.Close && x3.Close < x2.Close
					&& c.IsBullish && c.Open <= x3.Close && c.Close > x1.Open
						? CandlePattern.BullishThreeLineStrike
						: CandlePattern.None;
			}

			return x1.IsBullish && x2.IsBullish && x3.IsBullish && x2.Close > x1.Close && x3.Close > x2.Close
				&& c.IsBearish && c.Open >= x3.Close && c.Close < x1.Open
					? CandlePattern.BearishThreeLineStrike
					: CandlePattern.None;
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

		// the patterns made of at least `candles` candles
		private static CandlePattern PatternsOfAtLeast(int candles)
		{
			var mask = CandlePattern.None;

			foreach (var info in CandlePatternInfo)
			{
				if (info.Candles >= candles)
					mask |= info.Pattern;
			}

			return mask;
		}

		// position of the strongest of `patterns` in the list (twins share it); int.MaxValue for none
		private static int BestRank(CandlePattern patterns)
		{
			for (var i = 0; i < CandlePatternInfo.Length; i++)
			{
				if ((patterns & CandlePatternInfo[i].Pattern) != 0)
					return i / 2;
			}

			return int.MaxValue;
		}

		// +1 bullish, -1 bearish, 0 when there is no pattern or equally strong ones both ways
		private static int DecideReaction(CandlePattern bullish, CandlePattern bearish)
		{
			var bull = BestRank(bullish);
			var bear = BestRank(bearish);
			return bull < bear ? 1 : bear < bull ? -1 : 0;
		}

		#endregion

		#region Order flow

		// The price inside the closed bar where the most contracts traded, when it stands out: at
		// least Min filled volume, and Filled volume vs bar average times the bar's average per
		// price. Whoever was on the passive side there got filled - resting bids when sellers
		// hit them (bid volume), resting offers when buyers lifted them (ask volume).
		private void DetectFootprintFill(int bar, IndicatorCandle candle)
		{
			var levels = candle.GetAllPriceLevels();

			if (levels == null)
				return;

			PriceVolumeInfo best = null;
			var total = 0m;
			var count = 0;

			foreach (var level in levels)
			{
				total += level.Volume;
				count++;

				if (best == null || level.Volume > best.Volume || (level.Volume == best.Volume && level.Price < best.Price))
					best = level;
			}

			if (best == null || best.Volume <= 0)
				return;

			var threshold = Math.Max(FillMinVolume, (decimal)FillVolumeMultiplier * total / count);

			if (best.Volume < threshold)
				return;

			// the order book may have seen a resting order on that side filled right there during the
			// bar: a fill of its own becomes this one, a smaller one marks it
			var bidsFilled = best.Bid >= best.Ask;
			var key = (bar, best.Price, bidsFilled);

			if (!_orderBookFills.TryGetValue(key, out var fill))
			{
				fill = new FillEvent { Bar = bar, Price = best.Price };
				_fills.Add(fill);
				_watchedFills.Add(fill);
			}

			if (_filledRestingSize.TryGetValue(key, out var resting))
				fill.RestingSize = Math.Max(fill.RestingSize, resting);

			fill.FromFootprint = true;
			fill.Volume = best.Volume;
			fill.BidVolume = best.Bid;
			fill.AskVolume = best.Ask;
			fill.BidsFilled = bidsFilled;
			_footprintFills[bar] = fill;
		}

		// Reads the reaction to every fill still in its window: the first candlestick pattern on
		// the fill bar or the ReactionBars bars after it that closes away from the fill price.
		// Filled bids mean price came down into them, filled offers that it rallied into them -
		// the context the hammer shapes are read by. Returns the strongest footprint-fill reaction
		// of the bar each way, for the signals.
		private (FillEvent Bullish, FillEvent Bearish) UpdateFillReactions(int bar, IndicatorCandle candle)
		{
			FillEvent bullish = null;
			FillEvent bearish = null;

			for (var i = _watchedFills.Count - 1; i >= 0; i--)
			{
				var fill = _watchedFills[i];

				if (bar < fill.Bar)
					continue;

				if (StepFill(fill, bar, candle) && fill.ReactionBar == bar && fill.FromFootprint)
				{
					if (fill.Reaction > 0)
						bullish = StrongerFill(bullish, fill);
					else
						bearish = StrongerFill(bearish, fill);
				}

				if (fill.Decided && bar >= fill.Bar + ReactionBars)
					_watchedFills.RemoveAt(i);
			}

			return (bullish, bearish);
		}

		// one bar of a fill's window; true when this bar decided its reaction
		private bool StepFill(FillEvent fill, int bar, IndicatorCandle candle)
		{
			var tickSize = TickSize;

			if (bar > fill.Bar && bar <= fill.Bar + ReactionBars)
			{
				fill.WatchedBars = bar - fill.Bar;
				fill.UpTicks = Math.Max(fill.UpTicks, (candle.High - fill.Price) / tickSize);
				fill.DownTicks = Math.Max(fill.DownTicks, (fill.Price - candle.Low) / tickSize);
			}

			if (fill.Decided)
				return false;

			var bull = candle.Close > fill.Price ? FindCandlePatterns(bar, true, fill.BidsFilled) : CandlePattern.None;
			var bear = candle.Close < fill.Price ? FindCandlePatterns(bar, false, fill.BidsFilled) : CandlePattern.None;
			var reaction = DecideReaction(bull, bear);

			if (reaction != 0)
			{
				fill.Reaction = reaction;
				fill.ReactionBar = bar;
				fill.ReactionPatterns = reaction > 0 ? bull : bear;
				fill.Decided = true;
				return true;
			}

			if (bar >= fill.Bar + ReactionBars)
				fill.Decided = true;

			return false;
		}

		// the stronger pattern, then the bigger fill, then the later one
		private static FillEvent StrongerFill(FillEvent best, FillEvent fill)
		{
			if (best == null)
				return fill;

			var rank = BestRank(fill.ReactionPatterns);
			var bestRank = BestRank(best.ReactionPatterns);

			if (rank != bestRank)
				return rank < bestRank ? fill : best;

			if (fill.Volume != best.Volume)
				return fill.Volume > best.Volume ? fill : best;

			return fill.Bar > best.Bar ? fill : best;
		}

		// A big fill of resting bids near the low of the signal bar or the bar before (buys), or
		// of resting offers near the high (shorts): the passive side took the aggression and
		// price did not go through.
		private bool HasSupportingFill(int bar, bool isLong)
		{
			for (var b = bar; b >= Math.Max(0, bar - 1); b--)
			{
				if (!_footprintFills.TryGetValue(b, out var fill) || fill.BidsFilled != isLong)
					continue;

				var candle = GetCandle(b);
				var edge = (candle.High - candle.Low) * FillEdgeFraction;

				if (isLong ? fill.Price <= candle.Low + edge : fill.Price >= candle.High - edge)
					return true;
			}

			return false;
		}

		// The order book as it is now, when the chart (re)calculates
		private void LoadOrderBook()
		{
			var snapshot = MarketDepthInfo?.GetMarketDepthSnapshot();

			if (snapshot == null)
				return;

			foreach (var level in snapshot)
				ApplyDepth(level);
		}

		protected override void MarketDepthChanged(MarketDataArg depth)
		{
			lock (_sync)
			{
				ApplyDepth(depth);
				SettleLeavingOrders();
			}
		}

		protected override void OnNewTrade(MarketDataArg trade)
		{
			lock (_sync)
			{
				if (trade.Direction == TradeDirection.Sell)
					AddVolume(_soldAt, trade.Price, trade.Volume);
				else if (trade.Direction == TradeDirection.Buy)
					AddVolume(_boughtAt, trade.Price, trade.Volume);
				else
					return;

				// a print may be what a leaving order was waiting for
				SettleLeavingOrders();
			}
		}

		// One price of the order book changed. Levels of at least Min resting order contracts
		// are followed as resting orders; when one drops below that it is leaving the book, and
		// SettleLeavingOrders decides whether it was filled or pulled.
		private void ApplyDepth(MarketDataArg depth)
		{
			bool isBid;

			if (depth.DataType == MarketDataType.Bid)
				isBid = true;
			else if (depth.DataType == MarketDataType.Ask)
				isBid = false;
			else
				return;

			var book = isBid ? _bids : _asks;
			var price = depth.Price;
			var volume = depth.Volume;

			if (volume > 0)
				book[price] = volume;
			else
				book.Remove(price);

			var orders = isBid ? _restingBids : _restingAsks;
			orders.TryGetValue(price, out var order);

			if (volume >= RestingOrderMin)
			{
				if (order == null)
				{
					order = new RestingOrder
					{
						Price = price,
						IsBid = isBid,
						FirstBar = LiveBar,
						FirstTime = MarketTime,
						TradedAtStart = TradedAgainst(price, isBid)
					};

					orders[price] = order;
				}

				// still there, or back before it was settled
				order.Leaving = false;
				order.EndBar = -1;
				order.Volume = volume;
				order.MaxVolume = Math.Max(order.MaxVolume, volume);
				return;
			}

			if (order == null)
				return;

			order.Volume = volume;

			if (order.Leaving)
				return;

			order.Leaving = true;
			order.EndBar = LiveBar;
			order.EndTime = MarketTime;

			// the deepest level leaving the book has usually just scrolled out of the depth the
			// feed sends, rather than been filled or pulled
			order.AtBookEdge = volume == 0 && (isBid ? !_bids.Keys.Any(p => p < price) : !_asks.Keys.Any(p => p > price));
		}

		// A leaving order is filled once trades at its price, against its side, took Filled when
		// traded % of its size; it is pulled if they have not after OrderSettleSeconds.
		private void SettleLeavingOrders()
		{
			SettleLeavingOrders(_restingBids);
			SettleLeavingOrders(_restingAsks);
		}

		private void SettleLeavingOrders(Dictionary<decimal, RestingOrder> orders)
		{
			List<decimal> settled = null;

			foreach (var order in orders.Values)
			{
				if (!order.Leaving)
					continue;

				order.Traded = TradedAgainst(order.Price, order.IsBid) - order.TradedAtStart;
				var filled = order.Traded >= order.MaxVolume * OrderFilledPercent / 100m;

				if (!filled && (MarketTime - order.EndTime).TotalSeconds < OrderSettleSeconds)
					continue;

				order.State = filled ? OrderState.Filled : order.AtBookEdge ? OrderState.OutOfView : OrderState.Pulled;
				_endedOrders.Add(order);

				if (settled == null)
					settled = new List<decimal>();

				settled.Add(order.Price);

				if (filled)
					RecordOrderBookFill(order);
			}

			if (settled == null)
				return;

			foreach (var price in settled)
				orders.Remove(price);
		}

		// A resting order the order book saw filled: it marks the footprint fill at that price if
		// the bar has one for the same side. Otherwise, if at least Min filled volume traded
		// against it, it becomes a fill of its own (shown, but not used for signals - history has
		// no order book to repeat it); a smaller one only shows as its band ending, unless the
		// bar's footprint fill turns out to be right there when the bar closes.
		private void RecordOrderBookFill(RestingOrder order)
		{
			var key = (order.EndBar, order.Price, order.IsBid);
			_filledRestingSize.TryGetValue(key, out var biggest);
			biggest = Math.Max(biggest, order.MaxVolume);
			_filledRestingSize[key] = biggest;

			if (_footprintFills.TryGetValue(order.EndBar, out var footprint) && footprint.Price == order.Price && footprint.BidsFilled == order.IsBid)
			{
				footprint.RestingSize = Math.Max(footprint.RestingSize, biggest);
				return;
			}

			// the same price filled again (the level refilled): one fill, with both volumes
			if (_orderBookFills.TryGetValue(key, out var earlier))
			{
				earlier.Volume += order.Traded;
				earlier.RestingSize = biggest;
				return;
			}

			if (order.Traded < FillMinVolume)
				return;

			var fill = new FillEvent
			{
				Bar = order.EndBar,
				Price = order.Price,
				Volume = order.Traded,
				BidsFilled = order.IsBid,
				RestingSize = biggest
			};

			_fills.Add(fill);
			_watchedFills.Add(fill);
			_orderBookFills[key] = fill;

			// its bar may have closed while the order waited for its prints
			for (var bar = fill.Bar; bar <= _lastClosedBar && bar <= fill.Bar + ReactionBars; bar++)
				StepFill(fill, bar, GetCandle(bar));
		}

		private decimal TradedAgainst(decimal price, bool isBid)
		{
			var traded = isBid ? _soldAt : _boughtAt;
			return traded.TryGetValue(price, out var volume) ? volume : 0;
		}

		private static void AddVolume(Dictionary<decimal, decimal> traded, decimal price, decimal volume)
		{
			traded.TryGetValue(price, out var total);
			traded[price] = total + volume;
		}

		private int LiveBar => Math.Max(0, CurrentBar - 1);

		#endregion

		#region Signals and TP / SL tracking

		private void GenerateSignals(int bar, IndicatorCandle candle, FvgZone bullZone, FvgZone bearZone, FillEvent bullFill, FillEvent bearFill,
			bool sweptLows, bool sweptHighs, ref List<PendingAlert> alerts)
		{
			var longTrade = EnableBuySignals ? TryBuildSignal(bar, candle, true, bullZone, bullFill, sweptLows) : null;
			var shortTrade = EnableShortSignals ? TryBuildSignal(bar, candle, false, bearZone, bearFill, sweptHighs) : null;

			var candidates = new List<SignalTrade>(2);

			if (longTrade != null)
				candidates.Add(longTrade);

			if (shortTrade != null)
				candidates.Add(shortTrade);

			if (candidates.Count == 0)
				return;

			var canShow = !OneTradeAtATime || !_openTrades.Any(t => t.IsShown);

			// An outside bar can trigger both sides at once. With one position at a time only
			// the side with the better expected result is shown - neither on a tie.
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

				if (trade.IsShown)
				{
					// its label names the reaction, so the zone's own marker steps aside
					var zone = trade.IsLong ? bullZone : bearZone;
					var fill = trade.IsLong ? bullFill : bearFill;

					if (zone != null && (trade.Trigger == TriggerType.Fvg || trade.Trigger == TriggerType.SweepThenFvg))
						zone.SignalShown = true;

					if (fill != null && trade.Trigger == TriggerType.Fill)
						fill.SignalShown = true;
				}

				RegisterSignal(trade, candle, ref alerts);
			}
		}

		private SignalTrade TryBuildSignal(int bar, IndicatorCandle candle, bool isLong, FvgZone zone, FillEvent fill, bool sweepNow)
		{
			var lastSweep = isLong ? _lastLowSweepBar : _lastHighSweepBar;
			var sweepBefore = lastSweep >= 0 && bar - lastSweep <= ConfluenceBars;
			TriggerType trigger;

			switch (SignalSource)
			{
				case SignalMode.FvgReactionOnly when zone != null:
					trigger = sweepBefore ? TriggerType.SweepThenFvg : TriggerType.Fvg;
					break;

				case SignalMode.LiquiditySweepOnly when sweepNow:
					trigger = TriggerType.Sweep;
					break;

				case SignalMode.SweepThenFvg when zone != null && sweepBefore:
					trigger = TriggerType.SweepThenFvg;
					break;

				case SignalMode.FillReactionOnly when fill != null:
					trigger = TriggerType.Fill;
					break;

				case SignalMode.AnyTrigger when zone != null:
					trigger = sweepBefore ? TriggerType.SweepThenFvg : TriggerType.Fvg;
					break;

				case SignalMode.AnyTrigger when fill != null:
					trigger = TriggerType.Fill;
					break;

				case SignalMode.AnyTrigger when sweepNow:
					trigger = TriggerType.Sweep;
					break;

				default:
					return null;
			}

			// don't count the same move twice
			var lastSignal = isLong ? _lastLongSignalBar : _lastShortSignalBar;

			if (lastSignal >= 0 && bar - lastSignal <= SignalCooldownBars)
				return null;

			var fromZone = trigger == TriggerType.Fvg || trigger == TriggerType.SweepThenFvg;
			var fromFill = trigger == TriggerType.Fill;

			// a reaction brings its own pattern; a sweep bar is read as coming after a dip (buys)
			// or a rally (shorts)
			var patterns = fromZone ? zone.ReactionPatterns
				: fromFill ? fill.ReactionPatterns
				: FindCandlePatterns(bar, isLong, isLong);

			var withTrend = TrendEmaPeriod > 0 && bar >= TrendEmaPeriod
				&& (isLong ? candle.Close > _ema[bar] : candle.Close < _ema[bar]);
			var deltaConfirms = isLong ? candle.Delta > 0 : candle.Delta < 0;
			var fillConfirms = HasSupportingFill(bar, isLong);
			var hasPattern = patterns != CandlePattern.None;

			if ((OnlyWithTrend && !withTrend)
				|| (RequireDeltaConfirmation && !deltaConfirms)
				|| (RequireFillConfirmation && !fillConfirms)
				|| (RequireCandlePattern && !hasPattern))
				return null;

			var confirmations = (withTrend ? 1 : 0) + (deltaConfirms ? 1 : 0) + (fillConfirms ? 1 : 0) + (hasPattern ? 1 : 0);
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
				WithTrend = withTrend,
				DeltaConfirms = deltaConfirms,
				FillConfirms = fillConfirms,
				CandlePatterns = patterns,
				Confirmations = confirmations,
				ZoneTop = fromZone ? zone.Top : 0,
				ZoneBottom = fromZone ? zone.Bottom : 0,
				FillPrice = fromFill ? fill.Price : 0,
				FillVolume = fromFill ? fill.Volume : 0,
				FillBidsFilled = fromFill && fill.BidsFilled,
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

			var offset = ArrowOffsetTicks * TickSize;

			if (trade.IsLong)
				_buySignal[bar] = candle.Low - offset;
			else
				_shortSignal[bar] = candle.High + offset;

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
			List<FvgZone> reactions;
			List<LiquiditySweep> sweeps = null;
			List<FillEvent> fills = null;
			List<RestingOrder> orders = null;
			List<SignalTrade> trades;
			PanelStats stats = null;

			// copy what's visible under the lock (OnCalculate and the order book may be updating
			// on their own threads), then draw from the copies
			lock (_sync)
			{
				if (DrawZones)
				{
					zones = _activeZones.Where(z => z.StartBar <= lastBar)
						.Concat(ShowUsedZones ? _retiredZones.Where(z => z.StartBar <= lastBar && z.EndBar >= firstBar) : Enumerable.Empty<FvgZone>())
						.Select(z => z.Clone())
						.ToList();
				}

				reactions = _retiredZones
					.Where(z => z.State == ZoneState.Used && !z.SignalShown && z.EndBar >= firstBar && z.EndBar <= lastBar)
					.Select(z => z.Clone())
					.ToList();

				if (ShowSweeps)
					sweeps = _sweeps.Where(s => s.Bar >= firstBar && s.SwingBar <= lastBar).ToList();

				if (ShowFills)
					fills = _fills.Where(f => f.Bar >= firstBar && f.Bar <= lastBar).Select(f => f.Clone()).ToList();

				if (ShowRestingOrders)
				{
					// leaving ones wait (for up to OrderSettleSeconds) to be settled as filled or pulled
					orders = _restingBids.Values.Concat(_restingAsks.Values)
						.Where(o => o.FirstBar <= lastBar && !o.Leaving)
						.Concat(_endedOrders.Where(o => o.EndBar >= firstBar && o.FirstBar <= lastBar
							&& (o.State == OrderState.Filled || ShowPulledOrders)))
						.Select(o => o.Clone())
						.ToList();
				}

				trades = _trades
					.Where(t => t.IsShown && t.EntryBar <= lastBar && (t.Outcome == TradeOutcome.Open || t.ExitBar >= firstBar))
					.Select(t => t.Clone())
					.ToList();

				if (ShowStatsPanel)
					stats = BuildPanelStats();
			}

			var hover = new Hover(MouseLocationInfo.LastPosition);
			var priceTags = new List<(string Text, int X, int Y, Color Color)>();

			if (zones != null)
				RenderZones(context, zones);

			if (orders != null)
				RenderRestingOrders(context, orders, hover);

			if (sweeps != null)
				RenderSweeps(context, sweeps);

			if (ShowTradeLevels)
				RenderTradeLevels(context, trades, firstBar, lastBar, priceTags);

			if (fills != null)
				RenderFills(context, fills, hover);

			RenderReactions(context, reactions, hover);

			if (ShowSignalLabels)
				RenderSignalLabels(context, trades, firstBar, lastBar, hover);

			// the open trade's price tags stay on top of the bubbles, markers and labels
			foreach (var (text, x, y, color) in priceTags)
				DrawPriceTag(context, text, x, y, color, SmallFont);

			if (stats != null)
				RenderStatsPanel(context, stats);

			if (hover.Lines != null)
				RenderTooltip(context, hover.Lines(), hover.Mouse);
		}

		private PanelStats BuildPanelStats()
		{
			var openTrade = _openTrades.Where(t => t.IsShown).OrderByDescending(t => t.EntryBar).FirstOrDefault();
			var resting = _restingBids.Values.Concat(_restingAsks.Values).Where(o => !o.Leaving).ToList();

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
				LastPrice = _lastPrice,
				Fills = _fills.Count,
				BullishFills = _fills.Count(f => f.Reaction > 0),
				BearishFills = _fills.Count(f => f.Reaction < 0),
				RestingBids = resting.Count(o => o.IsBid),
				RestingAsks = resting.Count(o => !o.IsBid),
				Largest = resting.OrderByDescending(o => o.Volume).FirstOrDefault()?.Clone()
			};
		}

		// Active gaps reach to the right edge, with a thin edge top and bottom and a dashed
		// midline; used, filled and expired ones end where that happened, as a faint trace.
		private void RenderZones(RenderContext context, List<FvgZone> zones)
		{
			// in cluster mode each price is a row: start below the edge candle's own row
			var isClusterMode = ChartInfo.ChartVisualMode == ChartVisualModes.Clusters;
			var rowHeight = isClusterMode ? (int)ChartInfo.PriceChartContainer.PriceRowHeight : 0;
			var right = ChartInfo.Region.Width;

			foreach (var zone in zones)
			{
				var color = zone.IsBullish ? BullishZoneColor : BearishZoneColor;
				var active = zone.State == ZoneState.Active;
				var x1 = ChartInfo.GetXByBar(zone.StartBar);
				var x2 = active ? right : ChartInfo.GetXByBar(zone.EndBar + 1);
				var yTop = ChartInfo.GetYByPrice(zone.Top, isClusterMode) + rowHeight;
				var yBottom = ChartInfo.GetYByPrice(zone.Bottom, isClusterMode);

				if (x2 <= x1 || yBottom <= yTop)
					continue;

				var rect = new Rectangle(x1, yTop, x2 - x1, yBottom - yTop);

				if (!active)
				{
					context.FillRectangle(WithAlpha(color, color.A * 2 / 5), rect);
					continue;
				}

				context.FillRectangle(color, rect);

				var edge = new RenderPen(WithAlpha(color, Math.Min(255, color.A * 3)));
				context.DrawLine(edge, x1, yTop, x2, yTop);
				context.DrawLine(edge, x1, yBottom, x2, yBottom);

				if (ShowZoneMidline)
				{
					var yMiddle = (yTop + yBottom) / 2;
					var midline = new RenderPen(WithAlpha(color, Math.Min(255, color.A * 2))) { DashStyle = DashStyle.Dash };
					context.DrawLine(midline, x1, yMiddle, x2, yMiddle);
				}
			}
		}

		// Resting orders as bands on their price row, from when the order book first showed them
		// to now: blue bids, orange offers, stronger the bigger they are, with the size at the
		// right edge. Filled ones stop where they were filled.
		private void RenderRestingOrders(RenderContext context, List<RestingOrder> orders, Hover hover)
		{
			var tickSize = TickSize;
			var right = ChartInfo.Region.Width;
			var font = SmallFont;
			var tags = new List<Rectangle>();
			var maxVolume = orders.Where(o => o.State == OrderState.Active).Select(o => o.Volume).DefaultIfEmpty(0).Max();

			// biggest first, so they keep their tag when tags would overlap
			foreach (var order in orders.OrderByDescending(o => o.State == OrderState.Active ? o.Volume : 0))
			{
				var active = order.State == OrderState.Active;
				var color = order.IsBid ? BidColor : AskColor;
				var x1 = ChartInfo.GetXByBar(order.FirstBar);
				var x2 = active ? right : ChartInfo.GetXByBar(order.EndBar + 1);
				var y = ChartInfo.GetYByPrice(order.Price, true);
				var height = ChartInfo.GetYByPrice(order.Price - tickSize, true) - y;

				if (height < 2)
				{
					y -= (2 - height) / 2;
					height = 2;
				}

				if (x2 <= x1)
					continue;

				var band = new Rectangle(x1, y, x2 - x1, height);
				var strength = maxVolume > 0 ? (double)(order.Volume / maxVolume) : 1;
				var alpha = active ? (int)Math.Round(90 + 130 * Math.Min(1, strength)) : order.State == OrderState.Filled ? 70 : 35;
				context.FillRectangle(WithAlpha(color, alpha), band);

				var captured = order;

				if (Math.Abs(hover.Mouse.Y - (y + height / 2)) <= height / 2 + 2 && hover.Mouse.X >= x1 && hover.Mouse.X <= x2)
					hover.Offer(1, () => OrderTooltip(captured));

				if (!active)
					continue;

				var text = FormatVolume(order.Volume);
				var size = context.MeasureString(text, font);
				var tag = new Rectangle(right - size.Width - 10, y + height / 2 - size.Height / 2, size.Width + 6, size.Height);

				if (tags.Any(t => t.IntersectsWith(tag)))
					continue;

				tags.Add(tag);
				context.FillRectangle(WithAlpha(color, 220), tag, 3);
				context.DrawString(text, font, Color.White, tag.X + 3, tag.Y);

				if (tag.Contains(hover.Mouse))
					hover.Offer(1, () => OrderTooltip(captured));
			}
		}

		// a dotted line from the swing that held the liquidity to the bar that took it
		private void RenderSweeps(RenderContext context, List<LiquiditySweep> sweeps)
		{
			var pen = new RenderPen(WithAlpha(NeutralColor, 170)) { DashStyle = DashStyle.Dot };

			foreach (var sweep in sweeps)
			{
				var y = ChartInfo.GetYByPrice(sweep.Level, false);
				var x1 = ChartInfo.GetXByBar(sweep.SwingBar, false);
				var x2 = ChartInfo.GetXByBar(sweep.Bar, false);

				context.DrawLine(pen, x1, y, x2, y);
				context.FillEllipse(WithAlpha(sweep.SweptLows ? BullColor : BearColor, 230), new Rectangle(x2 - 3, y - 3, 6, 6));
			}
		}

		// Long/short-position style boxes: entry -> TP shaded green, entry -> SL red, from the
		// signal bar to the bar that settled the trade. With break-even on, a dotted line marks
		// the trigger; once it is reached the stop line carries on at the break-even price. An
		// open trade adds price tags for its TP and its current stop to `priceTags`.
		private void RenderTradeLevels(RenderContext context, List<SignalTrade> trades, int firstBar, int lastBar,
			List<(string Text, int X, int Y, Color Color)> priceTags)
		{
			var lastIndex = CurrentBar - 1;
			var tpColor = TakeProfitPen.Color.Convert();
			var slColor = StopLossPen.Color.Convert();
			var beColor = BreakEvenPen.Color.Convert();
			var entryPen = new RenderPen(WithAlpha(NeutralColor, 160)) { DashStyle = DashStyle.Dash };
			var triggerPen = new RenderPen(WithAlpha(beColor, 160)) { DashStyle = DashStyle.Dot };
			var tpTrace = new RenderPen(WithAlpha(tpColor, 90));
			var slTrace = new RenderPen(WithAlpha(slColor, 90));
			var beTrace = new RenderPen(WithAlpha(beColor, 90));

			foreach (var trade in trades)
			{
				var open = trade.Outcome == TradeOutcome.Open;
				var endBar = open ? lastIndex : trade.ExitBar;

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

				// the open trade in full, the ones that have ended as a faint trace
				var shade = open ? 30 : 14;
				context.FillRectangle(WithAlpha(tpColor, shade), VerticalSpan(x1, x2, yEntry, yTp));
				context.FillRectangle(WithAlpha(slColor, shade), VerticalSpan(x1, xStopMoved, yEntry, ySl));
				context.DrawLine(open ? TakeProfitPen.RenderObject : tpTrace, x1, yTp, x2, yTp);
				context.DrawLine(open ? StopLossPen.RenderObject : slTrace, x1, ySl, xStopMoved, ySl);

				if (trade.BreakEvenActive)
				{
					var yBreakEven = ChartInfo.GetYByPrice(trade.BreakEvenPrice, false);
					context.DrawLine(open ? BreakEvenPen.RenderObject : beTrace, xStopMoved, yBreakEven, x2, yBreakEven);
				}

				if (!open)
					continue;

				context.DrawLine(entryPen, x1, yEntry, x2, yEntry);

				if (trade.HasBreakEven)
				{
					var yTrigger = ChartInfo.GetYByPrice(trade.TriggerPrice, false);
					context.DrawLine(triggerPen, x1, yTrigger, xStopMoved, yTrigger);
				}

				// live trade: price tags at the end of its TP line and of the stop that applies now
				priceTags.Add(($"TP {FormatPrice(trade.TakeProfitPrice)}", x2 + 4, yTp, tpColor));

				if (trade.BreakEvenActive)
					priceTags.Add(($"BE {FormatPrice(trade.BreakEvenPrice)}", x2 + 4, ChartInfo.GetYByPrice(trade.BreakEvenPrice, false), beColor));
				else
					priceTags.Add(($"SL {FormatPrice(trade.StopLossPrice)}", x2 + 4, ySl, slColor));
			}
		}

		// A bubble per big fill, sized by its volume (compared with the biggest on screen) and
		// colored by the reaction: green bullish, red bearish, gray none - faint while it is
		// still waiting. A white ring: the order book saw a resting order filled there.
		private void RenderFills(RenderContext context, List<FillEvent> fills, Hover hover)
		{
			if (fills.Count == 0)
				return;

			var maxVolume = fills.Max(f => f.Volume);

			foreach (var fill in fills)
			{
				var x = ChartInfo.GetXByBar(fill.Bar, false);
				var y = ChartInfo.GetYByPrice(fill.Price, false);
				var share = maxVolume > 0 ? (double)(fill.Volume / maxVolume) : 1;
				var radius = 3 + (int)Math.Round(8 * Math.Sqrt(Math.Max(0, Math.Min(1, share))));
				var color = fill.Reaction > 0 ? BullColor : fill.Reaction < 0 ? BearColor : NeutralColor;
				var rect = new Rectangle(x - radius, y - radius, radius * 2, radius * 2);

				context.FillEllipse(WithAlpha(color, fill.Decided ? 150 : 70), rect);
				context.DrawEllipse(new RenderPen(WithAlpha(color, 235)), rect);

				if (fill.RestingSize > 0)
				{
					var ring = new Rectangle(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6);
					context.DrawEllipse(new RenderPen(WithAlpha(Color.White, 170)), ring);
				}

				var dx = hover.Mouse.X - x;
				var dy = hover.Mouse.Y - y;
				var captured = fill;

				if (dx * dx + dy * dy <= (radius + 2) * (radius + 2))
					hover.Offer(2, () => FillTooltip(captured));
			}
		}

		// FVG reactions that did not become a signal on the chart: a small triangle at the
		// candle and the pattern that made it
		private void RenderReactions(RenderContext context, List<FvgZone> zones, Hover hover)
		{
			var font = SmallFont;
			var placed = new List<Rectangle>();

			foreach (var zone in zones.OrderBy(z => z.EndBar))
			{
				var bullish = zone.ReactionBullish;
				var color = bullish ? BullColor : BearColor;
				var x = ChartInfo.GetXByBar(zone.EndBar, false);
				var direction = bullish ? 1 : -1;
				var tip = bullish
					? ChartInfo.GetYByPrice(zone.ReactionLow, false) + 5
					: ChartInfo.GetYByPrice(zone.ReactionHigh, false) - 5;

				var marker = new[] { new Point(x, tip), new Point(x - 4, tip + direction * 7), new Point(x + 4, tip + direction * 7) };
				context.FillPolygon(color, marker);

				var captured = zone;
				var markerBox = new Rectangle(x - 5, Math.Min(tip, tip + direction * 7) - 1, 10, 9);

				if (markerBox.Contains(hover.Mouse))
					hover.Offer(3, () => ZoneTooltip(captured));

				if (!ShowReactionLabels)
					continue;

				var text = PatternSummary(zone.ReactionPatterns);
				var size = context.MeasureString(text, font);
				var rect = new Rectangle(x - size.Width / 2 - 3, bullish ? tip + 9 : tip - 9 - size.Height, size.Width + 6, size.Height);

				for (var attempt = 0; attempt < 10 && placed.Any(r => r.IntersectsWith(rect)); attempt++)
					rect.Y += bullish ? size.Height + 2 : -(size.Height + 2);

				placed.Add(rect);
				context.FillRectangle(WithAlpha(CardColor, 200), rect, 3);
				context.DrawString(text, font, color, rect.X + 3, rect.Y);

				if (rect.Contains(hover.Mouse))
					hover.Offer(3, () => ZoneTooltip(captured));
			}
		}

		// Two-line card under a buy / above a short:
		//   BUY  FVG · Hammer                            [OPEN / TP / BE / SL / EXP]
		//   TP 31%  BE 41%  SL 28%   EV +6t
		// the setup (trigger and candlestick pattern, "+1" when the bar made more than one), the
		// odds of each ending and the ticks those odds are worth. Once the trade has ended (with
		// Compact labels for closed trades) only a chip with its result is left: "TP +80t".
		private void RenderSignalLabels(RenderContext context, List<SignalTrade> trades, int firstBar, int lastBar, Hover hover)
		{
			const int pad = 5;
			const int stripe = 3;

			var font = LabelFont.RenderObject;
			var small = SmallFont;
			var arrowOffset = ArrowOffsetTicks * TickSize;
			var region = ChartInfo.PriceChartContainer.Region;
			var placed = new List<Rectangle>();

			foreach (var trade in trades.OrderBy(t => t.EntryBar))
			{
				if (trade.EntryBar < firstBar || trade.EntryBar > lastBar)
					continue;

				if (CompactClosedLabels && trade.Outcome != TradeOutcome.Open)
				{
					RenderResultChip(context, trade, region, placed, hover);
					continue;
				}

				var side = Side(trade);
				var setup = SetupText(trade);
				var odds = $"{OddsText(trade.HasBreakEven, trade.Estimate.Odds, "  ")}   EV {SignedTicks(LabelExpectedTicks(trade))}t";
				var badge = OutcomeBadge(trade.Outcome);
				var sideColor = trade.IsLong ? BuyColor : ShortColor;

				var sideSize = context.MeasureString(side + "  ", font);
				var setupSize = context.MeasureString(setup, font);
				var oddsSize = context.MeasureString(odds, font);
				var badgeSize = context.MeasureString(badge, small);

				var badgeWidth = badgeSize.Width + 8;
				var width = stripe + pad + Math.Max(sideSize.Width + setupSize.Width, oddsSize.Width) + pad + badgeWidth + pad;
				var height = sideSize.Height + oddsSize.Height + pad * 2;

				// centred on the bar but kept inside the chart, measured vertically from the arrow
				// tip (which moves away from the bar as you zoom in)
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

				var textX = rect.X + stripe + pad;
				context.FillRectangle(CardColor, rect, 4);
				context.FillRectangle(sideColor, new Rectangle(rect.X, rect.Y + 3, stripe, rect.Height - 6));
				context.DrawString(side, font, sideColor, textX, rect.Y + pad);
				context.DrawString(setup, font, TextColor, textX + sideSize.Width, rect.Y + pad);
				context.DrawString(odds, font, DimTextColor, textX, rect.Y + pad + sideSize.Height);

				var badgeRect = new Rectangle(rect.Right - pad - badgeWidth, rect.Y + pad, badgeWidth, badgeSize.Height + 2);
				context.FillRectangle(OutcomeColor(trade.Outcome), badgeRect, 3);
				context.DrawString(badge, small, Color.White, badgeRect.X + 4, badgeRect.Y + 1);

				var captured = trade;

				if (rect.Contains(hover.Mouse))
					hover.Offer(4, () => SignalTooltip(captured));
			}
		}

		// "TP +80t" in the color of the ending, where the card was
		private void RenderResultChip(RenderContext context, SignalTrade trade, Rectangle region, List<Rectangle> placed, Hover hover)
		{
			var font = SmallFont;
			var arrowOffset = ArrowOffsetTicks * TickSize;
			var text = $"{OutcomeBadge(trade.Outcome)} {SignedTicks((int)Math.Round(ResultTicks(trade), MidpointRounding.AwayFromZero))}t";
			var size = context.MeasureString(text, font);
			var width = size.Width + 10;
			var height = size.Height + 4;

			var x = ChartInfo.GetXByBar(trade.EntryBar, false) - width / 2;
			x = Math.Max(region.X, Math.Min(x, region.X + region.Width - width));
			var y = trade.IsLong
				? ChartInfo.GetYByPrice(trade.SignalLow - arrowOffset, false) + LabelOffset
				: ChartInfo.GetYByPrice(trade.SignalHigh + arrowOffset, false) - LabelOffset - height;

			var rect = new Rectangle(x, y, width, height);

			for (var attempt = 0; attempt < 20 && placed.Any(r => r.IntersectsWith(rect)); attempt++)
				rect.Y += trade.IsLong ? height + 2 : -(height + 2);

			placed.Add(rect);
			context.FillRectangle(WithAlpha(CardColor, 200), rect, 3);
			context.DrawString(text, font, OutcomeColor(trade.Outcome), rect.X + 5, rect.Y + 2);

			if (rect.Contains(hover.Mouse))
				hover.Offer(4, () => SignalTooltip(trade));
		}

		private void RenderStatsPanel(RenderContext context, PanelStats stats)
		{
			var breakEven = BreakEvenEnabled;
			var bracket = $"TP {TakeProfitTicks}t · SL {StopLossTicks}t";

			if (breakEven)
				bracket += $" · BE +{BreakEvenTriggerTicks}t → +{EffectiveBreakEvenStopTicks}t";

			string[] Row(string name, int wins, int breakEvens, int losses, decimal net)
			{
				var ticks = $"{net.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t";
				return breakEven
					? new[] { name, $"{wins} TP", $"{breakEvens} BE", $"{losses} SL", ticks }
					: new[] { name, $"{wins} TP", $"{losses} SL", ticks };
			}

			var table = new List<(string[] Cells, Color Color)>
			{
				(Row("Longs", stats.LongWins, stats.LongBreakEvens, stats.LongLosses, stats.LongNetTicks), BuyColor),
				(Row("Shorts", stats.ShortWins, stats.ShortBreakEvens, stats.ShortLosses, stats.ShortNetTicks), ShortColor),
				(Row("Total", stats.LongWins + stats.ShortWins, stats.LongBreakEvens + stats.ShortBreakEvens,
					stats.LongLosses + stats.ShortLosses, stats.LongNetTicks + stats.ShortNetTicks), TextColor)
			};

			var footer = new List<(string Text, Color Color)>
			{
				($"Open {stats.Open} · Expired {stats.Expired} · Hidden {stats.Filtered} · Model n={stats.ModelResolved}", DimTextColor),

				// were the labels right? what signals labelled with a positive / not positive
				// expected result really made (every settled signal, hidden ones included)
				($"Labelled EV>0: {TrackRecord(stats.PositiveEvTicks, stats.PositiveEvCount)}   "
					+ $"EV≤0: {TrackRecord(stats.NegativeEvTicks, stats.NegativeEvCount)}", DimTextColor),
				($"Fills {stats.Fills}: {stats.BullishFills} bullish · {stats.BearishFills} bearish reactions", DimTextColor)
			};

			var restingLine = $"Resting ≥{RestingOrderMin}: {stats.RestingBids} bids · {stats.RestingAsks} offers";

			if (stats.Largest != null)
				restingLine += $" · largest {FormatVolume(stats.Largest.Volume)} {(stats.Largest.IsBid ? "bid" : "offer")} @ {FormatPrice(stats.Largest.Price)}";

			footer.Add((restingLine, DimTextColor));

			var open = stats.OpenTrade;

			if (open != null)
			{
				// how far the open trade has moved, and the odds from here
				var excursion = (stats.LastPrice - open.EntryPrice) / TickSize * (open.IsLong ? 1 : -1);
				var stop = open.BreakEvenActive ? $", stop +{EffectiveBreakEvenStopTicks}t" : string.Empty;
				var odds = OddsText(open.HasBreakEven, LiveOdds(open, stats.LastPrice), " · ");

				footer.Add(($"Live {Side(open)} {excursion.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t{stop}:  {odds}",
					open.IsLong ? BuyColor : ShortColor));
			}

			const int pad = 8;
			const int gap = 12;
			var font = LabelFont.RenderObject;
			var title = $"FVG · Sweep · Fill signals   {bracket}";
			var titleSize = context.MeasureString(title, font);
			var columns = table[0].Cells.Length;
			var widths = new int[columns];
			var rowHeight = titleSize.Height;

			foreach (var (cells, _) in table)
			{
				for (var i = 0; i < columns; i++)
					widths[i] = Math.Max(widths[i], context.MeasureString(cells[i], font).Width);
			}

			var tableWidth = widths.Sum() + gap * (columns - 1);
			var contentWidth = Math.Max(titleSize.Width, Math.Max(tableWidth, footer.Max(f => context.MeasureString(f.Text, font).Width)));
			var height = pad * 2 + rowHeight * (1 + table.Count + footer.Count) + 8;

			var region = ChartInfo.PriceChartContainer.Region;
			const int margin = 10;
			var left = StatsPanelLocation == PanelCorner.TopLeft || StatsPanelLocation == PanelCorner.BottomLeft;
			var top = StatsPanelLocation == PanelCorner.TopLeft || StatsPanelLocation == PanelCorner.TopRight;
			var width = contentWidth + pad * 2;
			var x = left ? region.X + margin : region.X + region.Width - width - margin;
			var y = top ? region.Y + margin : region.Y + region.Height - height - margin;

			var panel = new Rectangle(x, y, width, height);
			context.FillRectangle(CardColor, panel, 6);

			var lineY = y + pad;
			context.DrawString(title, font, TextColor, x + pad, lineY);
			lineY += rowHeight + 4;
			context.DrawLine(new RenderPen(CardBorderColor), x + pad, lineY - 2, x + width - pad, lineY - 2);

			foreach (var (cells, color) in table)
			{
				var cellX = x + pad;

				for (var i = 0; i < columns; i++)
				{
					// names on the left, numbers right-aligned in their column
					var cellWidth = context.MeasureString(cells[i], font).Width;
					var textX = i == 0 ? cellX : cellX + widths[i] - cellWidth;
					context.DrawString(cells[i], font, i == 0 ? color : TextColor, textX, lineY);
					cellX += widths[i] + gap;
				}

				lineY += rowHeight;
			}

			lineY += 4;
			context.DrawLine(new RenderPen(CardBorderColor), x + pad, lineY - 2, x + width - pad, lineY - 2);

			foreach (var (text, color) in footer)
			{
				context.DrawString(text, font, color, x + pad, lineY);
				lineY += rowHeight;
			}
		}

		// hover details for a signal: what the probabilities were built from and how the trade ended
		private List<(string Text, Color Color)> SignalTooltip(SignalTrade trade)
		{
			var estimate = trade.Estimate;
			var percents = LabelPercents(trade.HasBreakEven, estimate.Odds);
			var side = trade.IsLong ? "buys" : "shorts";
			var time = trade.EntryTime.Add(InstrumentInfo.TimeZoneOffset).ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
			var expected = estimate.ExpectedTicks.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

			var odds = trade.HasBreakEven
				? $"P(TP) {percents[0]}%   P(BE) {percents[1]}%   P(SL) {percents[2]}%   EV {expected}t"
				: $"P(TP) {percents[0]}%   P(SL) {percents[1]}%   EV {expected}t";

			var lines = new List<(string Text, Color Color)>
			{
				($"{Side(trade)} @ {FormatPrice(trade.EntryPrice)}   {time}", trade.IsLong ? BuyColor : ShortColor),
				($"TP {FormatPrice(trade.TakeProfitPrice)} (+{TakeProfitTicks}t)   SL {FormatPrice(trade.StopLossPrice)} (-{StopLossTicks}t)", TextColor)
			};

			if (trade.HasBreakEven)
			{
				lines.Add(($"Break-even: at +{BreakEvenTriggerTicks}t the stop moves to {FormatPrice(trade.BreakEvenPrice)} (+{EffectiveBreakEvenStopTicks}t)",
					TextColor));
			}

			lines.Add((odds, TextColor));
			lines.Add(($"This setup: {TallyText(estimate.Setup, trade.HasBreakEven)}", DimTextColor));
			lines.Add(($"All {TriggerLabel(trade.Trigger)} {side}: {TallyText(estimate.Trigger, trade.HasBreakEven)}", DimTextColor));
			lines.Add(($"All {side}: {TallyText(estimate.Direction, trade.HasBreakEven)}", DimTextColor));
			lines.Add(($"Confirmations {trade.Confirmations}/{MaxConfirmations}: trend {YesNo(trade.WithTrend)} · delta {YesNo(trade.DeltaConfirms)}"
				+ $" · order flow {YesNo(trade.FillConfirms)} · pattern {YesNo(trade.CandlePatterns != CandlePattern.None)}", DimTextColor));

			var patterns = PatternNames(trade.CandlePatterns);

			if (patterns.Count > 0)
				lines.Add(($"Candlestick pattern{(patterns.Count > 1 ? "s" : string.Empty)}: {string.Join(", ", patterns)}", DimTextColor));

			if (trade.ZoneTop > 0)
			{
				var ticks = (trade.ZoneTop - trade.ZoneBottom) / TickSize;
				lines.Add(($"Reacted off the FVG {FormatPrice(trade.ZoneBottom)} - {FormatPrice(trade.ZoneTop)} ({ticks:0}t)", DimTextColor));
			}

			if (trade.FillPrice > 0)
			{
				var who = trade.FillBidsFilled ? "bids" : "offers";
				lines.Add(($"Reacted to {FormatVolume(trade.FillVolume)} filled @ {FormatPrice(trade.FillPrice)} (resting {who})", DimTextColor));
			}

			if (trade.BreakEvenActive)
				lines.Add(($"Stop moved to +{EffectiveBreakEvenStopTicks}t on bar {trade.BreakEvenBar}", BreakEvenPen.Color.Convert()));

			lines.Add((ResultText(trade), OutcomeColor(trade.Outcome)));
			return lines;
		}

		private List<(string Text, Color Color)> FillTooltip(FillEvent fill)
		{
			var lines = new List<(string Text, Color Color)>
			{
				($"{FormatVolume(fill.Volume)} filled @ {FormatPrice(fill.Price)} on bar {fill.Bar}", TextColor)
			};

			if (fill.FromFootprint)
			{
				lines.Add((fill.BidsFilled
					? $"Sellers hit the bids: {FormatVolume(fill.BidVolume)} sold, {FormatVolume(fill.AskVolume)} bought here"
					: $"Buyers lifted the offers: {FormatVolume(fill.AskVolume)} bought, {FormatVolume(fill.BidVolume)} sold here", DimTextColor));
			}

			if (fill.RestingSize > 0)
			{
				lines.Add(($"Order book: a resting {FormatVolume(fill.RestingSize)}-lot {(fill.BidsFilled ? "bid" : "offer")} was filled here",
					DimTextColor));
			}

			if (fill.Reaction != 0)
			{
				var color = fill.Reaction > 0 ? BullColor : BearColor;
				lines.Add(($"{(fill.Reaction > 0 ? "Bullish" : "Bearish")} reaction on bar {fill.ReactionBar}: {string.Join(", ", PatternNames(fill.ReactionPatterns))}",
					color));
			}
			else if (fill.Decided)
				lines.Add(($"No clear reaction within {ReactionBars} bar{(ReactionBars == 1 ? string.Empty : "s")}", NeutralColor));
			else
				lines.Add(("Waiting for a reaction", NeutralColor));

			if (fill.WatchedBars > 0)
			{
				lines.Add(($"Next {fill.WatchedBars} bar{(fill.WatchedBars == 1 ? string.Empty : "s")}: +{fill.UpTicks:0}t / -{fill.DownTicks:0}t",
					DimTextColor));
			}

			if (fill.SignalShown)
				lines.Add(($"Gave the {(fill.Reaction > 0 ? "BUY" : "SHORT")} signal on bar {fill.ReactionBar}", DimTextColor));

			return lines;
		}

		private List<(string Text, Color Color)> ZoneTooltip(FvgZone zone)
		{
			var ticks = (zone.Top - zone.Bottom) / TickSize;
			var kind = zone.IsBullish ? "Bullish" : "Bearish";
			var reaction = zone.ReactionBullish ? "Bullish" : "Bearish";

			return new List<(string Text, Color Color)>
			{
				($"{kind} FVG {FormatPrice(zone.Bottom)} - {FormatPrice(zone.Top)} ({ticks:0}t), from bar {zone.StartBar}", TextColor),
				($"{reaction} reaction on bar {zone.EndBar}: {string.Join(", ", PatternNames(zone.ReactionPatterns))}",
					zone.ReactionBullish ? BullColor : BearColor)
			};
		}

		private List<(string Text, Color Color)> OrderTooltip(RestingOrder order)
		{
			var kind = order.IsBid ? "bid" : "offer";
			var time = order.FirstTime.Add(InstrumentInfo.TimeZoneOffset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
			var lines = new List<(string Text, Color Color)>();

			switch (order.State)
			{
				case OrderState.Active:
					var max = order.MaxVolume > order.Volume ? $" (up to {FormatVolume(order.MaxVolume)})" : string.Empty;
					lines.Add(($"Resting {kind} {FormatVolume(order.Volume)} @ {FormatPrice(order.Price)}{max}", order.IsBid ? BidColor : AskColor));
					lines.Add(($"In the book since {time}", DimTextColor));
					break;

				case OrderState.Filled:
					lines.Add(($"{FormatVolume(order.MaxVolume)}-lot {kind} @ {FormatPrice(order.Price)} was filled on bar {order.EndBar}",
						order.IsBid ? BidColor : AskColor));
					break;

				default:
					var how = order.State == OrderState.Pulled ? "was pulled" : "left the visible depth";
					lines.Add(($"{FormatVolume(order.MaxVolume)}-lot {kind} @ {FormatPrice(order.Price)} {how} on bar {order.EndBar}", DimTextColor));
					break;
			}

			var traded = order.State == OrderState.Active ? TradedSince(order) : order.Traded;

			if (traded > 0)
				lines.Add(($"Traded against it: {FormatVolume(traded)}", DimTextColor));

			return lines;
		}

		private decimal TradedSince(RestingOrder order)
		{
			lock (_sync)
				return TradedAgainst(order.Price, order.IsBid) - order.TradedAtStart;
		}

		private void RenderTooltip(RenderContext context, List<(string Text, Color Color)> lines, Point mouse)
		{
			var size = MeasureLines(context, lines);
			var region = ChartInfo.PriceChartContainer.Region;

			var x = Math.Max(region.X, Math.Min(mouse.X + 16, region.X + region.Width - size.Width));
			var y = Math.Max(region.Y, Math.Min(mouse.Y + 16, region.Y + region.Height - size.Height));

			DrawTextBox(context, lines, new Rectangle(x, y, size.Width, size.Height));
		}

		private Size MeasureLines(RenderContext context, List<(string Text, Color Color)> lines)
		{
			const int pad = 7;
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
			const int pad = 7;
			var font = LabelFont.RenderObject;

			context.FillRectangle(CardColor, rect, 5);

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
			var rect = new Rectangle(x, y - size.Height / 2 - 1, size.Width + 8, size.Height + 2);

			context.FillRectangle(WithAlpha(color, 230), rect, 3);
			context.DrawString(text, font, Color.White, rect.X + 4, rect.Y + 1);
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
					return NeutralColor;

				default:
					return Color.FromArgb(255, 94, 106, 130);
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

				case TriggerType.Fill:
					return "Fill";

				default:
					return "FVG";
			}
		}

		private static string Side(SignalTrade trade)
		{
			return trade.IsLong ? "BUY" : "SHORT";
		}

		// "FVG · Hammer", "Fill · Bullish engulfing +1", "Sweep"
		private static string SetupText(SignalTrade trade)
		{
			var pattern = PatternSummary(trade.CandlePatterns);
			return pattern.Length == 0 ? TriggerLabel(trade.Trigger) : $"{TriggerLabel(trade.Trigger)} · {pattern}";
		}

		// the names of the patterns in `patterns`, strongest first
		private static List<string> PatternNames(CandlePattern patterns)
		{
			return CandlePatternInfo.Where(p => (patterns & p.Pattern) != 0).Select(p => p.Name).ToList();
		}

		// the strongest pattern's name, "+N" when there are more ("" for none)
		private static string PatternSummary(CandlePattern patterns)
		{
			var names = PatternNames(patterns);

			return names.Count == 0 ? string.Empty
				: names.Count == 1 ? names[0]
				: $"{names[0]} +{names.Count - 1}";
		}

		// 1.2k, 850
		private static string FormatVolume(decimal volume)
		{
			return volume >= 1000
				? (volume / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
				: volume.ToString("0", CultureInfo.InvariantCulture);
		}

		private RenderFont SmallFont
		{
			get
			{
				var font = LabelFont.RenderObject;
				return new RenderFont(font.FontFamily, Math.Max(6f, font.Size - 1));
			}
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

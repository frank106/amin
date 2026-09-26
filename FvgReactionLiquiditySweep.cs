using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ATAS.DataFeedsCore;
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
	//   7) Optional execution, off by default: the orders of the signals shown on the chart, with
	//      the entry, take profit, stop and break-even their trades already carry. Paper
	//      (simulated) unless it is armed for a live account, one position at a time, with a daily
	//      loss limit and a CSV log of every order for reconciling with the scoreboard.
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
			FillReactionOnly,

			[Display(Name = "Key-level sweeps only (alone or then FVG)")]
			KeyLevelSweeps
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

		// When signals may fire, in New York time
		public enum SignalHoursRule
		{
			[Display(Name = "All hours")]
			AllHours,

			[Display(Name = "Regular hours")]
			RegularHours,

			[Display(Name = "First two hours of regular hours")]
			FirstTwoHours,

			[Display(Name = "Regular hours, not 11:30 - 13:30")]
			RegularHoursNoLunch,

			[Display(Name = "Custom window")]
			Custom
		}

		// How many contracts the busiest price of a bar needs to be a big fill
		public enum FillSizeRule
		{
			[Display(Name = "Among the biggest of recent bars")]
			TopOfRecentBars,

			[Display(Name = "A fixed number of contracts")]
			FixedContracts
		}

		// How many contracts a price in the order book needs to be a resting order
		public enum RestingSizeRule
		{
			[Display(Name = "A fixed number of contracts")]
			FixedContracts,

			[Display(Name = "Times the typical level in the book")]
			TimesTypicalLevel
		}

		// How far the take profit and the stop loss sit from the entry
		public enum BracketRule
		{
			[Display(Name = "Fixed ticks")]
			FixedTicks,

			[Display(Name = "Multiples of the ATR")]
			AtrMultiple,

			[Display(Name = "Stop beyond the signal bar")]
			BeyondSignalBar
		}

		// Where a signal's trade is entered
		public enum EntryRule
		{
			[Display(Name = "At the signal bar's close")]
			AtClose,

			[Display(Name = "Limit order on a pullback")]
			LimitPullback
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
			Fill = 3,
			KeySweep = 4,          // a sweep of a key level
			KeySweepThenFvg = 5    // an FVG reaction after a sweep of a key level
		}

		// in order of importance, two by two
		private enum KeyLevelKind
		{
			PriorDayHigh,
			PriorDayLow,
			OvernightHigh,
			OvernightLow,
			OpeningRangeHigh,
			OpeningRangeLow,
			EqualHighs,
			EqualLows
		}

		private enum KeyLevelState
		{
			Fresh,        // price has not traded beyond it yet
			Swept,        // a bar wicked beyond it and closed back inside
			Broken,       // a bar closed beyond it
			Expired       // its day ended, or it got too old, untouched
		}

		private enum TradeOutcome
		{
			Open,
			TakeProfit,
			BreakEven,    // stopped at the break-even stop after the trigger was reached
			StopLoss,
			Expired,
			Missed        // a limit entry that was never filled
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

		// A price where stops tend to rest: the prior day's regular-hours high / low, the overnight
		// high / low, the opening range high / low, or two swing highs / lows at the same price.
		// It counts until price trades beyond it.
		private class KeyLevel
		{
			public KeyLevelKind Kind;
			public decimal Price;
			public int FromBar;             // the first bar that can take it
			public int EndBar = -1;         // the bar that swept, broke or expired it
			public KeyLevelState State;
			public int FirstSwingBar = -1;  // equal highs / lows: the two swings
			public int SecondSwingBar = -1;

			public bool IsHigh => ((int)Kind & 1) == 0;

			public KeyLevel Clone()
			{
				return (KeyLevel)MemberwiseClone();
			}
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
			public decimal Threshold;       // the size it needed when it appeared; it leaves below that

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

		// What the model knew at the moment a signal fired: the odds, and the counts of every
		// group they were built from, broadest first
		private readonly struct ProbabilityEstimate
		{
			public ProbabilityEstimate(OutcomeOdds odds, double expectedTicks, OutcomeCounter[] levels)
			{
				Odds = odds;
				ExpectedTicks = expectedTicks;
				Levels = levels.Select(l => new OutcomeTally(l)).ToArray();
			}

			public OutcomeOdds Odds { get; }
			public double TakeProfit => Odds.TakeProfit;   // P(the trade ends at TP)
			public double BreakEven => Odds.BreakEven;     // P(it ends at the break-even stop)
			public double StopLoss => Odds.StopLoss;       // P(it ends at the full stop loss)
			public double ExpectedTicks { get; }           // TP, BE and SL ticks weighted by those odds
			public OutcomeTally[] Levels { get; }
			public OutcomeTally Setup => Levels[Levels.Length - 1];
			public OutcomeTally Trigger => Levels[Math.Min(1, Levels.Length - 1)];
			public OutcomeTally Direction => Levels[0];
		}

		// Walk-forward estimate of how a trade ends (TP / break-even / SL), smoothed down a path
		// of ever narrower groups: direction, trigger, then each split switched on (time of day,
		// volatility, trend, confirmations). Each group's counts are pulled toward the odds of
		// the group above it.
		private class ProbabilityModel
		{
			private readonly Dictionary<long, OutcomeCounter> _groups = new Dictionary<long, OutcomeCounter>();

			public int Resolved { get; private set; }

			public void Reset()
			{
				_groups.Clear();
				Resolved = 0;
			}

			public void Record(long[] path, TradeOutcome outcome)
			{
				foreach (var group in path)
					Add(Counter(group), outcome);

				Resolved++;
			}

			public ProbabilityEstimate Estimate(long[] path, OutcomeOdds prior, double priorWeight,
				double takeProfitTicks, double breakEvenTicks, double stopLossTicks)
			{
				var odds = prior;
				var levels = new OutcomeCounter[path.Length];

				for (var i = 0; i < path.Length; i++)
				{
					levels[i] = Counter(path[i]);
					odds = Smooth(levels[i], odds, priorWeight);
				}

				var expectedTicks = odds.TakeProfit * takeProfitTicks + odds.BreakEven * breakEvenTicks - odds.StopLoss * stopLossTicks;
				return new ProbabilityEstimate(odds, expectedTicks, levels);
			}

			private OutcomeCounter Counter(long group)
			{
				if (!_groups.TryGetValue(group, out var counter))
					_groups[group] = counter = new OutcomeCounter();

				return counter;
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
			public KeyLevel SweptLevel;           // the key level a key-level sweep took (null = none)
			public ProbabilityEstimate Estimate;
			public bool IsShown;          // false = tracked for the statistics only (filtered, or a position was already open)
			public bool HasBreakEven;     // the break-even stop was on when the signal fired
			public decimal TriggerPrice;  // reaching this price moves the stop...
			public decimal BreakEvenPrice;// ...to this one
			public int TargetTicks;       // the bracket, in ticks from the entry: take profit,
			public int RiskTicks;         // stop loss,
			public int TriggerTicks;      // break-even trigger (0 = no break-even)
			public int LockedTicks;       // and the profit the break-even stop keeps
			public bool LimitEntry;       // entered by a limit order on a pullback...
			public bool Pending;          // ...that is still waiting for its fill
			public decimal ThroughPrice;  // price has to trade here (a tick through the limit) to fill it
			public int FillBar = -1;      // the bar it was entered on (the signal bar for an entry at the close)
			public int ExpiryBar = -1;    // the last bar a limit order may fill on
			public decimal SignalClose;
			public int TimeBucket;        // time of day: first 30 minutes, morning, midday, afternoon, outside regular hours
			public int VolatilityBucket;  // quiet, normal or busy compared with the longer average range
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
			public PathResult(TradeOutcome outcome, bool breakEvenActive, decimal exitPrice, bool filled = true)
			{
				Outcome = outcome;
				BreakEvenActive = breakEvenActive;
				ExitPrice = exitPrice;
				Filled = filled;
			}

			public TradeOutcome Outcome { get; }
			public bool BreakEvenActive { get; }
			public decimal ExitPrice { get; }
			public bool Filled { get; }   // false: a limit order still waiting

			// worst (0) to best (4) for the trade: SL, still open without break-even,
			// break-even hit, still open with the stop in profit, TP
			public int Rank => Outcome == TradeOutcome.StopLoss ? 0
				: Outcome == TradeOutcome.BreakEven ? 2
				: Outcome == TradeOutcome.TakeProfit ? 4
				: BreakEvenActive ? 3 : 1;

			public bool SameAs(PathResult other)
			{
				return Outcome == other.Outcome && BreakEvenActive == other.BreakEvenActive && Filled == other.Filled;
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
			public int Missed;
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
			public decimal FillThreshold;      // what a big fill takes now (0 = not known yet)
			public decimal RestingThreshold;   // what a resting order takes now
			public DateTime LastBarTime;       // for the New York clock
			public Scoreboard Board;
			public List<(string Text, Color Color)> Execution;   // null while execution is off
		}

		// Every closed signal, hidden ones included, by trigger and by candlestick pattern, and
		// how the labelled take-profit odds held up
		private class Scoreboard
		{
			public int Closed;
			public List<ScoreRow> Triggers = new List<ScoreRow>();
			public List<ScoreRow> Patterns = new List<ScoreRow>();
			public List<OddsBucket> OddsCheck = new List<OddsBucket>();
		}

		private class ScoreRow
		{
			public string Name;
			public int Count;
			public int Wins;
			public int BreakEvens;
			public int Losses;
			public decimal Ticks;
		}

		// signals labelled with a TP chance in [From, To) %, and how many hit TP
		private class OddsBucket
		{
			public int From;
			public int To;
			public int Count;
			public int Hits;
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

		// What an order the executor sends is for
		private enum OrderRole
		{
			Entry,
			TakeProfit,
			StopLoss,
			Exit          // closing at market: the chart's trade ended, or the position lost its protection
		}

		// One order the executor sent, and what it knows of it. Paper orders are ATAS orders too, so
		// paper and live go through the same code; paper ones just never leave the indicator.
		private class ExecOrder
		{
			public Order Order;
			public readonly List<Order> Replaced = new List<Order>();   // earlier versions of a modified order
			public OrderRole Role;
			public bool IsBuy;
			public decimal Quantity;
			public decimal Filled;
			public bool Working = true;     // may still fill
			public bool CancelSent;
			public int CancelAttempts;      // live: cancels the broker refused
			public bool FillPending;        // live: reported filled, its fills not in yet

			public bool Matches(Order order, string id)
			{
				if (order != null && (ReferenceEquals(order, Order) || Replaced.Any(o => ReferenceEquals(o, order))))
					return true;

				return !string.IsNullOrEmpty(id) && (id == Order.Id || Replaced.Any(o => o.Id == id));
			}
		}

		// The position the executor holds for one signal, from its entry order until it is flat and
		// the chart's trade has ended. It follows its own copy of the chart's trade, walked through
		// the same prices by the same rules, so a recalculation of the chart never changes it.
		private class ExecPosition
		{
			public string SignalId;
			public SignalTrade Trade;
			public bool Paper;
			public bool IsLong;
			public decimal Contracts;
			public decimal TickValue;        // $ per tick and contract, fixed at the entry
			public string Account;
			public decimal Entered;          // contracts filled on the entry
			public decimal Open;             // contracts held now
			public decimal AverageEntry;
			public decimal ExitTicks;        // ticks made on the contracts closed so far, summed over them
			public decimal StopPrice;        // where the stop sits
			public bool StopMoved;           // to break-even (tried once)
			public bool Exiting;             // closing at market once the bracket is out of the way
			public bool ExitFailed;
			public bool ChartEnded;          // the chart's trade ended (logged once)
			public DateTime FlatSince;       // live: since when the account shows no position (MinValue: it shows one)
			public DateTime UnprotectedSince;// live: since when the position has no working stop (MinValue: it has one)
			public bool Done;                // flat and nothing working
			public ExecOrder Entry;
			public ExecOrder TakeProfit;
			public ExecOrder Stop;
			public ExecOrder Exit;
			public readonly List<ExecOrder> Orders = new List<ExecOrder>();

			public ExecOrder Find(Order order, string id)
			{
				return Orders.FirstOrDefault(o => o.Matches(order, id));
			}
		}

		// the result of the day's closed trades, in one mode (paper and live count apart)
		private class ExecDay
		{
			public DateTime Day = DateTime.MinValue;
			public decimal Pnl;
			public int Trades;
			public bool Tripped;             // the daily loss limit was reached (logged once)
		}

		private enum LiveAction
		{
			Place,
			Modify,
			Cancel
		}

		// a live order call, made once the lock is released
		private readonly struct LiveRequest
		{
			public LiveRequest(LiveAction action, Order order, Order newOrder = null)
			{
				Action = action;
				Order = order;
				NewOrder = newOrder;
			}

			public LiveAction Action { get; }
			public Order Order { get; }
			public Order NewOrder { get; }
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

		// the adaptive big-fill size needs this many recent bars with a footprint
		private const int MinFillHistory = 20;

		// the scoreboard lists this many patterns, the most frequent first
		private const int ScoreboardPatterns = 10;

		// the adaptive resting-order size needs this many prices in the book
		private const int MinBookLevels = 5;

		// a swing high / low is higher / lower than this many bars on each side
		private const int SwingStrength = 3;

		// New York time: the trading day starts at 18:00, lunch is 11:30 - 13:30
		private static readonly TimeSpan TradingDayStart = new TimeSpan(18, 0, 0);
		private static readonly TimeSpan LunchStart = new TimeSpan(11, 30, 0);
		private static readonly TimeSpan LunchEnd = new TimeSpan(13, 30, 0);

		private static readonly Color BullColor = Color.FromArgb(255, 38, 166, 154);
		private static readonly Color BearColor = Color.FromArgb(255, 239, 83, 80);
		private static readonly Color NeutralColor = Color.FromArgb(255, 144, 150, 162);
		private static readonly Color BidColor = Color.FromArgb(255, 66, 165, 245);
		private static readonly Color AskColor = Color.FromArgb(255, 255, 167, 38);
		private static readonly Color TextColor = Color.FromArgb(255, 226, 230, 236);
		private static readonly Color DimTextColor = Color.FromArgb(255, 156, 163, 175);
		private static readonly Color CardColor = Color.FromArgb(226, 22, 25, 31);
		private static readonly Color CardBorderColor = Color.FromArgb(255, 58, 63, 74);
		private static readonly Color LevelColor = Color.FromArgb(255, 149, 117, 205);

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

		// the busiest price of each of the last Fill lookback bars (0 = no footprint), and the
		// non-zero ones sorted, for the adaptive big-fill size
		private readonly Queue<decimal> _recentPeaks = new Queue<decimal>();
		private readonly List<decimal> _sortedPeaks = new List<decimal>();
		private decimal _fillThreshold;     // what a big fill takes on the next bar (0 = not known yet)

		// key levels still counted, and the ones that ended
		private readonly List<KeyLevel> _keyLevels = new List<KeyLevel>();
		private readonly List<KeyLevel> _endedLevels = new List<KeyLevel>();

		// the trading day being built (New York time, from 18:00): its regular-hours, overnight
		// and opening ranges so far, and the last regular session that ended
		private DateTime _tradingDay;
		private bool _hasRth;
		private decimal _rthHigh;
		private decimal _rthLow;
		private bool _hasPriorRth;
		private decimal _priorRthHigh;
		private decimal _priorRthLow;
		private bool _hasOvernight;
		private decimal _overnightHigh;
		private decimal _overnightLow;
		private bool _overnightPosted;
		private bool _hasOpening;
		private decimal _openingHigh;
		private decimal _openingLow;
		private bool _openingPosted;

		// confirmed swing highs / lows, for equal highs / lows
		private readonly List<int> _swingHighs = new List<int>();
		private readonly List<int> _swingLows = new List<int>();

		// the last sweep of a key level each way, for Sweep+FVG
		private int _lastLowKeySweepBar = -1;
		private int _lastHighKeySweepBar = -1;
		private KeyLevel _lastLowKeySweep;
		private KeyLevel _lastHighKeySweep;

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

		// running total of the bars' true ranges, for the average true range over any window
		private readonly List<decimal> _cumulativeRange = new List<decimal>();

		// swing highs / lows (bar, price) nothing has traded beyond yet, for Sweep confirmed swings only
		private readonly List<(int Bar, decimal Price)> _openSwingHighs = new List<(int Bar, decimal Price)>();
		private readonly List<(int Bar, decimal Price)> _openSwingLows = new List<(int Bar, decimal Price)>();
		private readonly ProbabilityModel _model = new ProbabilityModel();

		// where the statistics panel was drawn last (render thread only), for its hover
		private Rectangle _lastPanel = Rectangle.Empty;

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
		private int _missed;
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
		private double _minSweepAtr;
		private bool _sweepConfirmedSwings;

		private int _minFvgTicks = 4;
		private double _minFvgAtr;
		private int _maxZoneAgeBars = 150;
		private bool _requireCloseThroughZone = true;
		private FvgFillRule _fvgFill = FvgFillRule.FarEdge;
		private bool _drawZones = true;
		private bool _showUsedZones;
		private bool _showZoneMidline = true;
		private Color _bullishZoneColor = Color.FromArgb(40, 38, 166, 154);
		private Color _bearishZoneColor = Color.FromArgb(40, 239, 83, 80);

		private SignalHoursRule _signalHours = SignalHoursRule.RegularHours;
		private TimeSpan _regularHoursStart = new TimeSpan(9, 30, 0);
		private TimeSpan _regularHoursEnd = new TimeSpan(16, 0, 0);
		private TimeSpan _customHoursStart = new TimeSpan(9, 30, 0);
		private TimeSpan _customHoursEnd = new TimeSpan(10, 30, 0);
		private int _openingRangeMinutes = 30;
		private bool _levelPriorDay = true;
		private bool _levelOvernight = true;
		private bool _levelOpeningRange = true;
		private bool _levelEqual = true;
		private int _equalToleranceTicks = 2;
		private int _equalLookbackBars = 120;
		private bool _drawKeyLevels = true;

		private bool _showFills = true;
		private FillSizeRule _fillSize = FillSizeRule.TopOfRecentBars;
		private int _fillTopPercent = 10;
		private int _fillLookbackBars = 200;
		private int _fillMinVolume = 150;
		private double _fillVolumeMultiplier = 3.0;
		private int _reactionBars = 3;
		private bool _showRestingOrders = true;
		private RestingSizeRule _restingSize = RestingSizeRule.FixedContracts;
		private int _restingOrderMin = 70;
		private double _restingMultiplier = 5.0;
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
		private int _atrPeriod = 20;

		private EntryRule _entry = EntryRule.AtClose;
		private int _pullbackPercent = 25;
		private int _limitValidBars = 3;
		private BracketRule _bracket = BracketRule.FixedTicks;
		private int _takeProfitTicks = 80;
		private int _stopLossTicks = 80;
		private double _takeProfitAtr = 3.0;
		private double _stopLossAtr = 3.0;
		private int _stopBufferTicks = 2;
		private double _rewardRisk = 1.5;
		private int _minStopTicks = 16;
		private int _maxStopTicks = 200;
		private int _breakEvenTriggerTicks = 40;
		private int _breakEvenStopTicks = 20;
		private int _breakEvenTriggerPercent = 50;
		private int _breakEvenStopPercent = 25;
		private int _maxBarsInTrade;
		private bool _expireAtSessionEnd;
		private SameBarHitRule _sameBarRule = SameBarHitRule.NearestExtremeFirst;
		private int _probabilitySmoothing = 10;
		private bool _oddsByTime;
		private bool _oddsByVolatility;
		private bool _oddsByTrend;
		private bool _oddsByConfirmations = true;

		// Execution. None of it is reset by a recalculation: redrawing the chart never forgets an
		// open position or its orders.

		// the columns of the execution log, in file order
		private static readonly string[] ExecutionLogColumns =
		{
			"logged_utc", "bar_time_ny", "trading_day", "mode", "account", "instrument", "event", "signal", "signal_bar", "side",
			"setup", "trigger", "patterns", "confirmations", "trend", "delta", "order_flow", "pattern", "odds_tp", "odds_be",
			"odds_sl", "ev_ticks", "order", "type", "qty", "price", "entry", "take_profit", "stop_loss", "be_trigger", "be_stop",
			"position", "pnl_ticks", "pnl_usd", "day_pnl_usd", "chart_outcome", "chart_ticks", "note"
		};

		// live: how long the account may show no position while the executor holds one before the
		// position counts as closed outside the indicator (fills can arrive after the position update)
		private static readonly TimeSpan AccountFlatGrace = TimeSpan.FromSeconds(3);

		// Live trades, one per account at a time across the charts of this ATAS: the account's ID ->
		// the indicator whose live position holds it, from its entry until the position is done
		private static readonly Dictionary<string, FvgReactionLiquiditySweep> LiveAccountHolders =
			new Dictionary<string, FvgReactionLiquiditySweep>(StringComparer.OrdinalIgnoreCase);

		private readonly List<SignalTrade> _execCandidates = new List<SignalTrade>();   // real-time signals of the bar that just closed
		private readonly List<ExecOrder> _paperBook = new List<ExecOrder>();            // paper orders resting in the simulator
		private readonly HashSet<string> _execFillIds = new HashSet<string>();          // live fills already counted
		private readonly ExecDay _paperDay = new ExecDay();
		private readonly ExecDay _liveDay = new ExecDay();
		private ExecPosition _execPosition;
		private List<PendingAlert> _execAlerts;         // raised, and live orders sent, once the lock is released
		private List<LiveRequest> _execRequests;
		private bool _execHalted;
		private string _execHaltReason;
		private bool _execConfigDirty = true;           // the settings may have changed since the last MODE row
		private string _execConfig = string.Empty;
		private string _execLastEvent;                  // for the panel
		private Color _execLastEventColor;
		private string _execLogError;
		private DateTime _execNow;                      // time of the latest price the executor saw (UTC)
		private decimal _execPrice;                     // and that price
		private int _paperOrderCount;

		// what the executor last saw of the bars, so each update walks only what is new
		private int _execBar = -1;
		private DateTime _execBarTime;
		private decimal _execHigh;
		private decimal _execLow;
		private decimal _execClose;

		private bool _executeSignals;
		private bool _paperTrading = true;
		private string _liveAccount = string.Empty;
		private int _contracts = 1;
		private decimal _dailyLossLimit;
		private bool _skipTradesBeyondLimit = true;
		private decimal _tickValue = 5m;
		private decimal _commissionPerContract = 5m;
		private int _slippageTicks = 1;
		private bool _alertOnOrders = true;
		private string _executionLogFolder = string.Empty;

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

		[Display(Name = "Min sweep depth (x ATR, 0 = off)", GroupName = "Liquidity Sweep", Order = 12,
			Description = "How far beyond the swing high / low a sweep has to trade, in average true ranges of the bars before it. A shallower poke is no sweep; a key level it takes still ends as swept, but gives no signal.")]
		[Range(0.0, 100.0)]
		public double MinSweepAtr
		{
			get => _minSweepAtr;
			set { _minSweepAtr = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Sweep confirmed swings only", GroupName = "Liquidity Sweep", Order = 13,
			Description = "A sweep has to take out a swing high / low of the lookback - higher / lower than the 3 bars on each side - that nothing has traded beyond since, and each swing gives one sweep. Off: any bar that wicks beyond the lookback's high / low.")]
		public bool SweepConfirmedSwings
		{
			get => _sweepConfirmedSwings;
			set { _sweepConfirmedSwings = value; RecalculateValues(); }
		}

		[Display(Name = "Min FVG Size (ticks)", GroupName = "Fair Value Gap", Order = 20)]
		[Range(0, 10000)]
		public int MinFvgTicks
		{
			get => _minFvgTicks;
			set { _minFvgTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Min FVG size (x ATR, 0 = off)", GroupName = "Fair Value Gap", Order = 21,
			Description = "A gap also has to be this many average true ranges of the bars before it.")]
		[Range(0.0, 100.0)]
		public double MinFvgAtr
		{
			get => _minFvgAtr;
			set { _minFvgAtr = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Zone Max Age (bars)", GroupName = "Fair Value Gap", Order = 22,
			Description = "A gap nothing has used or filled after this many bars is no longer watched.")]
		[Range(5, 100000)]
		public int MaxZoneAgeBars
		{
			get => _maxZoneAgeBars;
			set { _maxZoneAgeBars = Math.Max(5, value); RecalculateValues(); }
		}

		[Display(Name = "Require reaction close beyond zone", GroupName = "Fair Value Gap", Order = 23,
			Description = "A bullish reaction must close above the zone, a bearish one below it - not just form a pattern inside it.")]
		public bool RequireCloseThroughZone
		{
			get => _requireCloseThroughZone;
			set { _requireCloseThroughZone = value; RecalculateValues(); }
		}

		[Display(Name = "FVG is filled when", GroupName = "Fair Value Gap", Order = 24,
			Description = "A filled gap stops being watched, like a used one (a reaction on the same bar counts first).")]
		public FvgFillRule FvgFill
		{
			get => _fvgFill;
			set { _fvgFill = value; RecalculateValues(); }
		}

		[Display(Name = "Draw FVG Zones", GroupName = "Fair Value Gap", Order = 25)]
		public bool DrawZones
		{
			get => _drawZones;
			set { _drawZones = value; RedrawChart(); }
		}

		[Display(Name = "Show used / filled zones", GroupName = "Fair Value Gap", Order = 26,
			Description = "Keep a faint box, ending where it was used or filled, for gaps that are no longer watched.")]
		public bool ShowUsedZones
		{
			get => _showUsedZones;
			set { _showUsedZones = value; RedrawChart(); }
		}

		[Display(Name = "Show zone midline (50%)", GroupName = "Fair Value Gap", Order = 27)]
		public bool ShowZoneMidline
		{
			get => _showZoneMidline;
			set { _showZoneMidline = value; RedrawChart(); }
		}

		[Display(Name = "Bullish zone color", GroupName = "Fair Value Gap", Order = 28)]
		public Color BullishZoneColor
		{
			get => _bullishZoneColor;
			set { _bullishZoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Bearish zone color", GroupName = "Fair Value Gap", Order = 29)]
		public Color BearishZoneColor
		{
			get => _bearishZoneColor;
			set { _bearishZoneColor = value; RedrawChart(); }
		}

		[Display(Name = "Signal hours (New York)", GroupName = "Sessions & Key Levels", Order = 30,
			Description = "When signals may fire, in New York time whatever time zone the chart shows. Gaps, fills and levels are still found around the clock.")]
		public SignalHoursRule SignalHours
		{
			get => _signalHours;
			set { _signalHours = value; RecalculateValues(); }
		}

		[Display(Name = "Custom window start (New York)", GroupName = "Sessions & Key Levels", Order = 31,
			Description = "With Signal hours set to Custom window, signals may fire from this New York time until the end time. A window that ends before it starts runs over midnight.")]
		public TimeSpan CustomHoursStart
		{
			get => _customHoursStart;
			set { _customHoursStart = WithinDay(value); RecalculateValues(); }
		}

		[Display(Name = "Custom window end (New York)", GroupName = "Sessions & Key Levels", Order = 32,
			Description = "End of the custom window (not included). The same time as the start: all day.")]
		public TimeSpan CustomHoursEnd
		{
			get => _customHoursEnd;
			set { _customHoursEnd = WithinDay(value); RecalculateValues(); }
		}

		[Display(Name = "Regular hours start (New York)", GroupName = "Sessions & Key Levels", Order = 33,
			Description = "Start of the regular session, for the signal hours, the prior-day and overnight levels and the opening range.")]
		public TimeSpan RegularHoursStart
		{
			get => _regularHoursStart;
			set { _regularHoursStart = ClampToTradingDay(value); RecalculateValues(); }
		}

		[Display(Name = "Regular hours end (New York)", GroupName = "Sessions & Key Levels", Order = 34)]
		public TimeSpan RegularHoursEnd
		{
			get => _regularHoursEnd;
			set { _regularHoursEnd = ClampToTradingDay(value); RecalculateValues(); }
		}

		[Display(Name = "Opening range (minutes)", GroupName = "Sessions & Key Levels", Order = 35,
			Description = "The first minutes of regular hours whose high and low become the opening range.")]
		[Range(1, 600)]
		public int OpeningRangeMinutes
		{
			get => _openingRangeMinutes;
			set { _openingRangeMinutes = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Prior day high / low", GroupName = "Sessions & Key Levels", Order = 36,
			Description = "The high and low of the previous regular session, for the whole next trading day.")]
		public bool LevelPriorDay
		{
			get => _levelPriorDay;
			set { _levelPriorDay = value; RecalculateValues(); }
		}

		[Display(Name = "Overnight high / low", GroupName = "Sessions & Key Levels", Order = 37,
			Description = "The high and low from 18:00 New York time until regular hours open, for the rest of the day.")]
		public bool LevelOvernight
		{
			get => _levelOvernight;
			set { _levelOvernight = value; RecalculateValues(); }
		}

		[Display(Name = "Opening range high / low", GroupName = "Sessions & Key Levels", Order = 38)]
		public bool LevelOpeningRange
		{
			get => _levelOpeningRange;
			set { _levelOpeningRange = value; RecalculateValues(); }
		}

		[Display(Name = "Equal highs / lows", GroupName = "Sessions & Key Levels", Order = 39,
			Description = "Two swing highs (lows) within Equal level match ticks of each other, with nothing above (below) them in between: stops pile up there.")]
		public bool LevelEqual
		{
			get => _levelEqual;
			set { _levelEqual = value; RecalculateValues(); }
		}

		[Display(Name = "Equal level match (ticks)", GroupName = "Sessions & Key Levels", Order = 40)]
		[Range(0, 1000)]
		public int EqualToleranceTicks
		{
			get => _equalToleranceTicks;
			set { _equalToleranceTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Equal level lookback (bars)", GroupName = "Sessions & Key Levels", Order = 41,
			Description = "How far apart the two swings may be, and how long the level counts after the second one.")]
		[Range(10, 100000)]
		public int EqualLookbackBars
		{
			get => _equalLookbackBars;
			set { _equalLookbackBars = Math.Max(10, value); RecalculateValues(); }
		}

		[Display(Name = "Draw key levels", GroupName = "Sessions & Key Levels", Order = 42)]
		public bool DrawKeyLevels
		{
			get => _drawKeyLevels;
			set { _drawKeyLevels = value; RedrawChart(); }
		}

		[Display(Name = "Show big fills", GroupName = "Order Flow", Order = 50,
			Description = "A bubble where the most contracts traded inside a bar, colored by the reaction that followed: green bullish, red bearish, gray none (yet). A white ring: the order book saw a resting order filled there.")]
		public bool ShowFills
		{
			get => _showFills;
			set { _showFills = value; RedrawChart(); }
		}

		[Display(Name = "Big fill size", GroupName = "Order Flow", Order = 51,
			Description = "Among the biggest of recent bars: the busiest price of a bar must rank in the Top share of the last Fill lookback bars, so the size follows the market and the hour. Fixed: at least Min filled volume contracts.")]
		public FillSizeRule FillSize
		{
			get => _fillSize;
			set { _fillSize = value; RecalculateValues(); }
		}

		[Display(Name = "Top share of recent bars (%)", GroupName = "Order Flow", Order = 52)]
		[Range(1, 50)]
		public int FillTopPercent
		{
			get => _fillTopPercent;
			set { _fillTopPercent = Math.Min(50, Math.Max(1, value)); RecalculateValues(); }
		}

		[Display(Name = "Fill lookback (bars)", GroupName = "Order Flow", Order = 53)]
		[Range(20, 100000)]
		public int FillLookbackBars
		{
			get => _fillLookbackBars;
			set { _fillLookbackBars = Math.Max(20, value); RecalculateValues(); }
		}

		[Display(Name = "Min filled volume at one price", GroupName = "Order Flow", Order = 54,
			Description = "With a fixed big fill size: contracts that must trade at a single price inside one bar before it counts as a big fill.")]
		[Range(1, 100000000)]
		public int FillMinVolume
		{
			get => _fillMinVolume;
			set { _fillMinVolume = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Filled volume vs bar average (x)", GroupName = "Order Flow", Order = 55,
			Description = "The price must also trade this many times the bar's average volume per price.")]
		[Range(1.0, 1000.0)]
		public double FillVolumeMultiplier
		{
			get => _fillVolumeMultiplier;
			set { _fillVolumeMultiplier = Math.Max(1.0, value); RecalculateValues(); }
		}

		[Display(Name = "Reaction window (bars)", GroupName = "Order Flow", Order = 56,
			Description = "How many bars after a big fill a candlestick pattern may take to show the reaction to it.")]
		[Range(0, 100)]
		public int ReactionBars
		{
			get => _reactionBars;
			set { _reactionBars = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Show resting orders", GroupName = "Order Flow", Order = 57,
			Description = "Live from Level 2: limit orders of at least Min resting order contracts still waiting in the order book, with their size at the right edge.")]
		public bool ShowRestingOrders
		{
			get => _showRestingOrders;
			set { _showRestingOrders = value; RedrawChart(); }
		}

		[Display(Name = "Resting order size", GroupName = "Order Flow", Order = 58,
			Description = "Fixed: at least Min resting order contracts at one price. Times the typical level: at least Resting order vs typical level times the median size of the prices in the book when it appears.")]
		public RestingSizeRule RestingSize
		{
			get => _restingSize;
			set { _restingSize = value; RecalculateValues(); }
		}

		[Display(Name = "Min resting order (contracts)", GroupName = "Order Flow", Order = 59)]
		[Range(1, 100000000)]
		public int RestingOrderMin
		{
			get => _restingOrderMin;
			set { _restingOrderMin = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Resting order vs typical level (x)", GroupName = "Order Flow", Order = 60)]
		[Range(1.0, 1000.0)]
		public double RestingMultiplier
		{
			get => _restingMultiplier;
			set { _restingMultiplier = Math.Max(1.0, value); RecalculateValues(); }
		}

		[Display(Name = "Filled when traded (%)", GroupName = "Order Flow", Order = 61,
			Description = "A resting order that leaves the book counts as filled once trades at its price took at least this share of its size; otherwise it was pulled.")]
		[Range(1, 100)]
		public int OrderFilledPercent
		{
			get => _orderFilledPercent;
			set { _orderFilledPercent = Math.Min(100, Math.Max(1, value)); RecalculateValues(); }
		}

		[Display(Name = "Show pulled orders", GroupName = "Order Flow", Order = 62,
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

		[Display(Name = "Volatility (ATR) period (bars)", GroupName = "Signals", Order = 113,
			Description = "Bars in the average true range that the volatility-based settings measure against: sweep depth, gap size, ATR brackets and the volatility of the odds.")]
		[Range(1, 10000)]
		public int AtrPeriod
		{
			get => _atrPeriod;
			set { _atrPeriod = Math.Max(1, value); RecalculateValues(); }
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

		[Display(Name = "Entry", GroupName = "Entry", Order = 190,
			Description = "At the signal bar's close, or a limit order a share of the signal bar's range better: below the close for buys, above it for shorts.")]
		public EntryRule Entry
		{
			get => _entry;
			set { _entry = value; RecalculateValues(); }
		}

		[Display(Name = "Pullback (% of the signal bar)", GroupName = "Entry", Order = 191,
			Description = "With a limit entry: how far back from the signal bar's close the order sits, as a share of the bar's range.")]
		[Range(0, 100)]
		public int PullbackPercent
		{
			get => _pullbackPercent;
			set { _pullbackPercent = Math.Min(100, Math.Max(0, value)); RecalculateValues(); }
		}

		[Display(Name = "Limit order valid (bars)", GroupName = "Entry", Order = 192,
			Description = "With a limit entry: how many bars after the signal bar the order waits. It fills when price trades a tick through it; unfilled, it is cancelled and left out of the statistics.")]
		[Range(1, 10000)]
		public int LimitValidBars
		{
			get => _limitValidBars;
			set { _limitValidBars = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Bracket size", GroupName = "Take Profit / Stop Loss", Order = 200,
			Description = "Fixed ticks: Take profit / Stop loss ticks. Multiples of the ATR: both scale with the average true range of the bars before the signal. Stop beyond the signal bar: the stop sits Stop buffer ticks beyond the signal bar's low (buys) / high (shorts), the take profit a multiple of that.")]
		public BracketRule Bracket
		{
			get => _bracket;
			set { _bracket = value; RecalculateValues(); }
		}

		[Display(Name = "Take profit (ticks)", GroupName = "Take Profit / Stop Loss", Order = 201)]
		[Range(1, 100000)]
		public int TakeProfitTicks
		{
			get => _takeProfitTicks;
			set { _takeProfitTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Stop loss (ticks)", GroupName = "Take Profit / Stop Loss", Order = 202)]
		[Range(1, 100000)]
		public int StopLossTicks
		{
			get => _stopLossTicks;
			set { _stopLossTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Take profit (x ATR)", GroupName = "Take Profit / Stop Loss", Order = 203,
			Description = "With the ATR bracket: the take profit, in average true ranges of the bars before the signal.")]
		[Range(0.1, 1000.0)]
		public double TakeProfitAtr
		{
			get => _takeProfitAtr;
			set { _takeProfitAtr = Math.Max(0.1, value); RecalculateValues(); }
		}

		[Display(Name = "Stop loss (x ATR)", GroupName = "Take Profit / Stop Loss", Order = 204,
			Description = "With the ATR bracket: the stop loss, in average true ranges of the bars before the signal.")]
		[Range(0.1, 1000.0)]
		public double StopLossAtr
		{
			get => _stopLossAtr;
			set { _stopLossAtr = Math.Max(0.1, value); RecalculateValues(); }
		}

		[Display(Name = "Stop buffer (ticks)", GroupName = "Take Profit / Stop Loss", Order = 205,
			Description = "With the stop beyond the signal bar: how far beyond the bar's low (buys) / high (shorts) the stop sits.")]
		[Range(0, 10000)]
		public int StopBufferTicks
		{
			get => _stopBufferTicks;
			set { _stopBufferTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Take profit (x the stop)", GroupName = "Take Profit / Stop Loss", Order = 206,
			Description = "With the stop beyond the signal bar: the take profit as a multiple of the stop's distance (the reward : risk).")]
		[Range(0.1, 100.0)]
		public double RewardRisk
		{
			get => _rewardRisk;
			set { _rewardRisk = Math.Max(0.1, value); RecalculateValues(); }
		}

		[Display(Name = "Min stop (ticks)", GroupName = "Take Profit / Stop Loss", Order = 207,
			Description = "With the ATR or signal-bar bracket: the stop is at least this far from the entry, and at most Max stop. The take profit keeps its ratio to it.")]
		[Range(1, 100000)]
		public int MinStopTicks
		{
			get => _minStopTicks;
			set { _minStopTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Max stop (ticks)", GroupName = "Take Profit / Stop Loss", Order = 208)]
		[Range(1, 100000)]
		public int MaxStopTicks
		{
			get => _maxStopTicks;
			set { _maxStopTicks = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Break-even trigger (ticks, 0 = off)", GroupName = "Take Profit / Stop Loss", Order = 209,
			Description = "With fixed ticks: once a trade is this many ticks in profit, its stop moves to the break-even stop. Must be below the take profit.")]
		[Range(0, 100000)]
		public int BreakEvenTriggerTicks
		{
			get => _breakEvenTriggerTicks;
			set { _breakEvenTriggerTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Break-even stop (ticks in profit)", GroupName = "Take Profit / Stop Loss", Order = 210,
			Description = "With fixed ticks: where the stop moves once the trigger is reached, in ticks of profit from the entry (0 = the entry price). Kept below the trigger.")]
		[Range(0, 100000)]
		public int BreakEvenStopTicks
		{
			get => _breakEvenStopTicks;
			set { _breakEvenStopTicks = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Break-even trigger (% of TP, 0 = off)", GroupName = "Take Profit / Stop Loss", Order = 211,
			Description = "With the ATR or signal-bar bracket: the break-even trigger as a share of the take profit's distance.")]
		[Range(0, 99)]
		public int BreakEvenTriggerPercent
		{
			get => _breakEvenTriggerPercent;
			set { _breakEvenTriggerPercent = Math.Min(99, Math.Max(0, value)); RecalculateValues(); }
		}

		[Display(Name = "Break-even stop (% of TP)", GroupName = "Take Profit / Stop Loss", Order = 212,
			Description = "With the ATR or signal-bar bracket: the profit the break-even stop keeps, as a share of the take profit's distance. Kept below the trigger.")]
		[Range(0, 99)]
		public int BreakEvenStopPercent
		{
			get => _breakEvenStopPercent;
			set { _breakEvenStopPercent = Math.Min(99, Math.Max(0, value)); RecalculateValues(); }
		}

		[Display(Name = "Max bars in trade (0 = no limit)", GroupName = "Take Profit / Stop Loss", Order = 213,
			Description = "Trades that hit neither level within this many bars expire and are left out of the probabilities.")]
		[Range(0, 100000)]
		public int MaxBarsInTrade
		{
			get => _maxBarsInTrade;
			set { _maxBarsInTrade = Math.Max(0, value); RecalculateValues(); }
		}

		[Display(Name = "Close trades at session end", GroupName = "Take Profit / Stop Loss", Order = 214,
			Description = "Expire open trades on the last bar of each session and take no new signals on it.")]
		public bool ExpireAtSessionEnd
		{
			get => _expireAtSessionEnd;
			set { _expireAtSessionEnd = value; RecalculateValues(); }
		}

		[Display(Name = "Order of high and low inside a bar", GroupName = "Take Profit / Stop Loss", Order = 215,
			Description = "On historical bars only O/H/L/C are known, so when the order decides a trade (TP vs SL, or whether the break-even stop was hit after the trigger) this rule picks it. Live bars follow the trades as they happen.")]
		public SameBarHitRule SameBarRule
		{
			get => _sameBarRule;
			set { _sameBarRule = value; RecalculateValues(); }
		}

		[Display(Name = "Probability smoothing (virtual trades)", GroupName = "Take Profit / Stop Loss", Order = 216,
			Description = "How many trades' worth of weight the parent group gets. Higher = steadier probabilities that need more history to move.")]
		[Range(1, 1000)]
		public int ProbabilitySmoothing
		{
			get => _probabilitySmoothing;
			set { _probabilitySmoothing = Math.Max(1, value); RecalculateValues(); }
		}

		[Display(Name = "Odds by time of day", GroupName = "Take Profit / Stop Loss", Order = 217,
			Description = "Split each setup's odds by when the signal came: the first 30 minutes of regular hours, the morning, midday, the afternoon, or outside regular hours.")]
		public bool OddsByTime
		{
			get => _oddsByTime;
			set { _oddsByTime = value; RecalculateValues(); }
		}

		[Display(Name = "Odds by volatility", GroupName = "Take Profit / Stop Loss", Order = 218,
			Description = "Split each setup's odds by how busy the market is: the ATR of the bars before the signal against ten times as many bars - quiet (under 0.8), normal, busy (over 1.25).")]
		public bool OddsByVolatility
		{
			get => _oddsByVolatility;
			set { _oddsByVolatility = value; RecalculateValues(); }
		}

		[Display(Name = "Odds by trend", GroupName = "Take Profit / Stop Loss", Order = 219,
			Description = "Split each setup's odds by whether the signal goes with the trend EMA or against it.")]
		public bool OddsByTrend
		{
			get => _oddsByTrend;
			set { _oddsByTrend = value; RecalculateValues(); }
		}

		[Display(Name = "Odds by confirmations", GroupName = "Take Profit / Stop Loss", Order = 220,
			Description = "Split each setup's odds by how many of the four confirmations it had.")]
		public bool OddsByConfirmations
		{
			get => _oddsByConfirmations;
			set { _oddsByConfirmations = value; RecalculateValues(); }
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

		[Display(Name = "Keep the scoreboard open", GroupName = "Display", Order = 305,
			Description = "Results per setup and per candlestick pattern, and how the odds held up, next to the statistics panel. Hovering the panel always shows it.")]
		public bool ShowScoreboard { get; set; }

		[Display(Name = "Statistics panel position", GroupName = "Display", Order = 306)]
		public PanelCorner StatsPanelLocation { get; set; } = PanelCorner.TopRight;

		[Display(Name = "Label offset (px)", GroupName = "Display", Order = 307,
			Description = "Distance between the tip of the signal arrow and its label, so the label clears the arrow.")]
		[Range(0, 500)]
		public int LabelOffset { get; set; } = 24;

		[Display(Name = "Label font", GroupName = "Display", Order = 308)]
		public FontSetting LabelFont { get; set; } = new FontSetting("Arial", 9);

		[Display(Name = "Take profit line", GroupName = "Display", Order = 309)]
		public PenSettings TakeProfitPen { get; set; } = new PenSettings { Color = BullColor.Convert(), Width = 1 };

		[Display(Name = "Stop loss line", GroupName = "Display", Order = 310)]
		public PenSettings StopLossPen { get; set; } = new PenSettings { Color = BearColor.Convert(), Width = 1 };

		[Display(Name = "Break-even line", GroupName = "Display", Order = 311)]
		public PenSettings BreakEvenPen { get; set; } = new PenSettings { Color = Color.FromArgb(255, 255, 179, 0).Convert(), Width = 1 };

		[Display(Name = "Alert on new signal", GroupName = "Alerts", Order = 400)]
		public bool UseAlerts { get; set; }

		[Display(Name = "Alert on TP / SL / break-even", GroupName = "Alerts", Order = 401,
			Description = "When a shown trade hits its take profit, stop loss or break-even stop, and when its stop moves to break-even.")]
		public bool AlertOnTradeResult { get; set; }

		[Display(Name = "Alert sound file", GroupName = "Alerts", Order = 402)]
		public string AlertFile { get; set; } = "alert1";

		// Execution settings change what the executor does from now on; they don't redraw history
		[Display(Name = "Execute signals", GroupName = "Execution", Order = 500,
			Description = "Send orders for the BUY / SHORT signals the chart shows, in real time only. Off by default. With Paper trading on, nothing leaves the indicator. Switching it off stops new entries (an open position is still managed) and clears a halt.")]
		public bool ExecuteSignals
		{
			get => _executeSignals;
			set
			{
				lock (_sync)
				{
					if (!value)
					{
						_execHalted = false;
						_execHaltReason = null;
					}

					_executeSignals = value;
					_execConfigDirty = true;
				}

				RedrawChart();
			}
		}

		[Display(Name = "Paper trading (simulated fills)", GroupName = "Execution", Order = 501,
			Description = "On (the default): a simulator inside the indicator fills the orders. Off: real orders through ATAS, only to the Live account and only with a daily loss limit. A position keeps the mode it was opened in.")]
		public bool PaperTrading
		{
			get => _paperTrading;
			set { _paperTrading = value; _execConfigDirty = true; RedrawChart(); }
		}

		[Display(Name = "Live account (must match the chart's)", GroupName = "Execution", Order = 502,
			Description = "With Paper trading off, orders only go out while this is the account selected on the chart. Empty: no live orders.")]
		public string LiveAccount
		{
			get => _liveAccount;
			set { _liveAccount = value?.Trim() ?? string.Empty; _execConfigDirty = true; RedrawChart(); }
		}

		[Display(Name = "Contracts per trade", GroupName = "Execution", Order = 503)]
		[Range(1, 1000)]
		public int Contracts
		{
			get => _contracts;
			set { _contracts = Math.Min(1000, Math.Max(1, value)); _execConfigDirty = true; RedrawChart(); }
		}

		[Display(Name = "Daily loss limit ($, 0 = off)", GroupName = "Execution", Order = 504,
			Description = "Once the day's closed trades have lost this much, commission included, no new entries until the next trading day (18:00 New York). Paper and live count apart. Live trading needs one.")]
		[Range(0.0, 100000000.0)]
		public decimal DailyLossLimit
		{
			get => _dailyLossLimit;
			set { _dailyLossLimit = Math.Max(0, value); _execConfigDirty = true; RedrawChart(); }
		}

		[Display(Name = "Skip trades that could breach the limit", GroupName = "Execution", Order = 505,
			Description = "With a daily loss limit: also skip a signal whose stop, with slippage and commission, could take the day past the limit.")]
		public bool SkipTradesBeyondLimit
		{
			get => _skipTradesBeyondLimit;
			set { _skipTradesBeyondLimit = value; _execConfigDirty = true; }
		}

		[Display(Name = "Tick value ($ per contract)", GroupName = "Execution", Order = 506,
			Description = "5 for NQ, 0.5 for MNQ. When the trading connection reports a different one, the larger is used, so a wrong setting can only make the loss limit stricter.")]
		[Range(0.0, 100000.0)]
		public decimal TickValue
		{
			get => _tickValue;
			set { _tickValue = Math.Max(0, value); _execConfigDirty = true; }
		}

		[Display(Name = "Commission ($ per contract, round trip)", GroupName = "Execution", Order = 507,
			Description = "Taken off every closed trade, for the daily loss limit and the log. 5.00 is the backtest's one tick on NQ; set what your broker charges.")]
		[Range(0.0, 100000.0)]
		public decimal CommissionPerContract
		{
			get => _commissionPerContract;
			set { _commissionPerContract = Math.Max(0, value); _execConfigDirty = true; }
		}

		[Display(Name = "Slippage (ticks per market / stop fill)", GroupName = "Execution", Order = 508,
			Description = "Paper market and stop orders fill this many ticks worse than the price that reached them. Both modes count it in a new trade's risk.")]
		[Range(0, 1000)]
		public int SlippageTicks
		{
			get => _slippageTicks;
			set { _slippageTicks = Math.Max(0, value); _execConfigDirty = true; }
		}

		[Display(Name = "Alert on orders", GroupName = "Execution", Order = 509,
			Description = "An ATAS alert on each fill and each closed position. A halt, a rejected order and the daily loss limit always alert.")]
		public bool AlertOnOrders
		{
			get => _alertOnOrders;
			set => _alertOnOrders = value;
		}

		[Display(Name = "Log folder (empty = ATAS's data folder)", GroupName = "Execution", Order = 510,
			Description = "Where the CSV log of every signal, order, fill and result goes: one file per trading day, instrument and mode. Empty: ATAS/FvgExecution in the application data folder.")]
		public string ExecutionLogFolder
		{
			get => _executionLogFolder;
			set => _executionLogFolder = value?.Trim() ?? string.Empty;
		}

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
			{
				ResetState();
				_execConfigDirty = true;
			}
		}

		protected override void OnCalculate(int bar, decimal value)
		{
			List<PendingAlert> alerts = null;
			List<LiveRequest> requests = null;

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

				// the executor follows the chart in real time, never through history
				if (_realtime)
					SyncExecution(bar);

				requests = TakeExecutionOutput(ref alerts);
			}

			if (alerts != null)
				FireAlerts(alerts);

			// live orders only: paper ones never leave the indicator
			if (requests != null)
				_ = SendLiveRequests(requests);
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
			_recentPeaks.Clear();
			_sortedPeaks.Clear();
			_fillThreshold = 0;
			_keyLevels.Clear();
			_endedLevels.Clear();
			_swingHighs.Clear();
			_swingLows.Clear();
			_tradingDay = DateTime.MinValue;
			_hasRth = false;
			_hasPriorRth = false;
			_hasOvernight = false;
			_overnightPosted = false;
			_hasOpening = false;
			_openingPosted = false;
			_lastLowKeySweepBar = -1;
			_lastHighKeySweepBar = -1;
			_lastLowKeySweep = null;
			_lastHighKeySweep = null;
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
			_cumulativeRange.Clear();
			_openSwingHighs.Clear();
			_openSwingLows.Clear();
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
			_missed = 0;
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
			UpdateTrueRange(bar, candle);

			// bar + 1 exists (that's why this bar is closed), so we already know whether
			// it opens a new session
			var sessionEnds = SessionEndsAfter(bar);

			if (sessionEnds)
				ExpireAllOpenTrades(bar, candle, ref alerts);

			var (bullFill, bearFill) = UpdateFillReactions(bar, candle);

			// every bar belongs to a trading day, warm-up included
			var (keyLow, keyHigh) = UpdateKeyLevels(bar, candle);

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
				GenerateSignals(bar, candle, bullZone, bearZone, bullFill, bearFill, sweptLows, sweptHighs, keyLow, keyHigh, ref alerts);
		}

		// Close trades at session end: the closed bar is the session's last (bar + 1 has to exist)
		private bool SessionEndsAfter(int bar)
		{
			return ExpireAtSessionEnd && IsNewSession(bar + 1);
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

		// running total of true ranges: each bar's range, stretched to the close before it
		private void UpdateTrueRange(int bar, IndicatorCandle candle)
		{
			var range = candle.High - candle.Low;

			if (bar > 0)
			{
				var previousClose = GetCandle(bar - 1).Close;
				range = Math.Max(candle.High, previousClose) - Math.Min(candle.Low, previousClose);
			}

			_cumulativeRange.Add((bar > 0 ? _cumulativeRange[bar - 1] : 0) + range);
		}

		// average true range of the ATR period bars up to endBar (fewer at the start); 0 before the first
		private decimal AverageRange(int endBar)
		{
			return AverageRange(endBar, AtrPeriod);
		}

		private decimal AverageRange(int endBar, int period)
		{
			if (endBar < 0)
				return 0;

			var count = Math.Min(period, endBar + 1);
			var before = endBar - count >= 0 ? _cumulativeRange[endBar - count] : 0;
			return (_cumulativeRange[endBar] - before) / count;
		}

		#endregion

		#region Fair value gaps and sweeps

		// Classic 3-candle imbalance: gap between candle[bar-2] and candle[bar], created by the
		// large range of the middle candle[bar-1].
		private void DetectNewFvg(int bar)
		{
			var left = GetCandle(bar - 2);
			var right = GetCandle(bar);

			// at least one tick of empty space, even with Min FVG Size = 0, and at least Min FVG size
			// (x ATR) of the bars before the gap
			var minGap = Math.Max(Math.Max(MinFvgTicks, 1) * TickSize, (decimal)MinFvgAtr * AverageRange(bar - 3));

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

		// Liquidity sweep = the bar wicks beyond the high / low of the lookback and closes back
		// inside it -> stop run / liquidity grab. With Sweep confirmed swings only, the level is
		// a swing of the lookback nothing has traded beyond yet. Either way the wick has to reach
		// Min sweep depth (x ATR) beyond it.
		private void CheckLiquiditySweep(int bar, IndicatorCandle candle, out bool sweptLows, out bool sweptHighs)
		{
			bool highTaken;
			bool lowTaken;
			decimal highLevel;
			decimal lowLevel;
			int highBar;
			int lowBar;

			if (SweepConfirmedSwings)
			{
				(highTaken, highLevel, highBar) = TakeOpenSwings(_openSwingHighs, bar, candle, true);
				(lowTaken, lowLevel, lowBar) = TakeOpenSwings(_openSwingLows, bar, candle, false);
				RememberSwings(bar);
			}
			else
			{
				highLevel = decimal.MinValue;
				lowLevel = decimal.MaxValue;
				highBar = -1;
				lowBar = -1;

				for (var i = Math.Max(0, bar - SwingLookback); i < bar; i++)
				{
					var c = GetCandle(i);

					// the latest bar at the extreme, so the sweep line is as short as it can be
					if (c.High >= highLevel)
					{
						highLevel = c.High;
						highBar = i;
					}

					if (c.Low <= lowLevel)
					{
						lowLevel = c.Low;
						lowBar = i;
					}
				}

				highTaken = candle.High > highLevel && candle.Close < highLevel;
				lowTaken = candle.Low < lowLevel && candle.Close > lowLevel;
			}

			var minDepth = MinSweepDepth(bar);
			sweptHighs = highTaken && candle.High - highLevel >= minDepth;
			sweptLows = lowTaken && lowLevel - candle.Low >= minDepth;
			var offset = ArrowOffsetTicks * TickSize;

			if (sweptHighs)
			{
				_bearSweep[bar] = candle.High + offset;
				_sweeps.Add(new LiquiditySweep { Bar = bar, SwingBar = highBar, Level = highLevel });
			}

			if (sweptLows)
			{
				_bullSweep[bar] = candle.Low - offset;
				_sweeps.Add(new LiquiditySweep { Bar = bar, SwingBar = lowBar, Level = lowLevel, SweptLows = true });
			}
		}

		// how far beyond a level a sweep on `bar` has to reach, from the bars before it
		private decimal MinSweepDepth(int bar)
		{
			return (decimal)MinSweepAtr * AverageRange(bar - 1);
		}

		// Swings older than the lookback are forgotten; the ones the bar traded beyond are taken.
		// It swept the furthest one it closed back inside of (the latest on a tie).
		private (bool Swept, decimal Level, int SwingBar) TakeOpenSwings(List<(int Bar, decimal Price)> swings, int bar, IndicatorCandle candle, bool highs)
		{
			swings.RemoveAll(sw => bar - sw.Bar > SwingLookback);
			var swept = false;
			var level = 0m;
			var swingBar = -1;

			for (var i = swings.Count - 1; i >= 0; i--)
			{
				var (swing, price) = swings[i];

				if (highs ? candle.High <= price : candle.Low >= price)
					continue;

				var back = highs ? candle.Close < price : candle.Close > price;
				var further = !swept || (highs ? price > level : price < level) || (price == level && swing > swingBar);

				if (back && further)
				{
					swept = true;
					level = price;
					swingBar = swing;
				}

				swings.RemoveAt(i);
			}

			return (swept, level, swingBar);
		}

		// the swing SwingStrength bars back is confirmed now, untouched so far
		private void RememberSwings(int bar)
		{
			var pivot = bar - SwingStrength;

			if (pivot - SwingStrength < 0)
				return;

			if (IsSwing(pivot, true))
				_openSwingHighs.Add((pivot, GetCandle(pivot).High));

			if (IsSwing(pivot, false))
				_openSwingLows.Add((pivot, GetCandle(pivot).Low));
		}

		#endregion

		#region Sessions and key levels

		// New York time of a candle time (UTC): UTC-5, or UTC-4 from the second Sunday of March
		// 2:00 to the first Sunday of November 2:00 (the US daylight-saving rule since 2007)
		private static DateTime NewYorkTime(DateTime utc)
		{
			// no real time (e.g. a candle ATAS has not stamped yet)
			if (utc.Year < 2)
				return utc;

			var summerStart = NthSunday(utc.Year, 3, 2).AddHours(7);
			var summerEnd = NthSunday(utc.Year, 11, 1).AddHours(6);
			return utc.AddHours(utc >= summerStart && utc < summerEnd ? -4 : -5);
		}

		private static DateTime NthSunday(int year, int month, int n)
		{
			var first = new DateTime(year, month, 1);
			var toSunday = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
			return first.AddDays(toSunday + 7 * (n - 1));
		}

		// the trading day a New York time belongs to: it starts at 18:00 the evening before
		private static DateTime TradingDayOf(DateTime newYork)
		{
			return newYork.TimeOfDay >= TradingDayStart ? newYork.Date.AddDays(1) : newYork.Date;
		}

		// a time of day, from 00:00 to 23:59
		private static TimeSpan WithinDay(TimeSpan time)
		{
			var minutes = (int)Math.Floor(time.TotalMinutes) % (24 * 60);
			return TimeSpan.FromMinutes(minutes < 0 ? minutes + 24 * 60 : minutes);
		}

		// regular hours sit between midnight and 18:00 New York time
		private static TimeSpan ClampToTradingDay(TimeSpan time)
		{
			if (time < TimeSpan.Zero)
				return TimeSpan.Zero;

			return time > TradingDayStart ? TradingDayStart : time;
		}

		private bool IsRegularHours(TimeSpan time)
		{
			return time >= RegularHoursStart && time < RegularHoursEnd;
		}

		// whether a bar (by its open time) may give a signal
		private bool InSignalHours(DateTime candleTime)
		{
			if (SignalHours == SignalHoursRule.AllHours)
				return true;

			var time = NewYorkTime(candleTime).TimeOfDay;

			switch (SignalHours)
			{
				case SignalHoursRule.FirstTwoHours:
					return IsRegularHours(time) && time < RegularHoursStart + TimeSpan.FromHours(2);

				case SignalHoursRule.RegularHoursNoLunch:
					return IsRegularHours(time) && !(time >= LunchStart && time < LunchEnd);

				case SignalHoursRule.Custom:
					return CustomHoursStart == CustomHoursEnd
						|| (CustomHoursStart < CustomHoursEnd
							? time >= CustomHoursStart && time < CustomHoursEnd
							: time >= CustomHoursStart || time < CustomHoursEnd);

				default:
					return IsRegularHours(time);
			}
		}

		// Follows the trading day through a closed bar: posts the levels that became known before
		// it opened (the prior day at 18:00, the overnight range when regular hours open, the
		// opening range when it is over), lets the bar sweep or break the levels it trades
		// beyond, then adds the bar to today's ranges and looks for new equal highs / lows.
		// Returns the most important level swept each way.
		private (KeyLevel Low, KeyLevel High) UpdateKeyLevels(int bar, IndicatorCandle candle)
		{
			var newYork = NewYorkTime(candle.Time);
			var day = TradingDayOf(newYork);
			var time = newYork.TimeOfDay;
			var regular = IsRegularHours(time);
			var openingEnd = RegularHoursStart + TimeSpan.FromMinutes(OpeningRangeMinutes);

			if (day != _tradingDay)
			{
				if (_hasRth)
				{
					_hasPriorRth = true;
					_priorRthHigh = _rthHigh;
					_priorRthLow = _rthLow;
				}

				// yesterday's levels only count for their day
				foreach (var level in _keyLevels.Where(l => l.Kind < KeyLevelKind.EqualHighs).ToList())
					EndLevel(level, KeyLevelState.Expired, bar - 1);

				_tradingDay = day;
				_hasRth = false;
				_hasOvernight = false;
				_hasOpening = false;
				_overnightPosted = false;
				_openingPosted = false;

				if (LevelPriorDay && _hasPriorRth)
				{
					PostLevel(KeyLevelKind.PriorDayHigh, _priorRthHigh, bar);
					PostLevel(KeyLevelKind.PriorDayLow, _priorRthLow, bar);
				}
			}

			if (regular && !_overnightPosted)
			{
				_overnightPosted = true;

				if (LevelOvernight && _hasOvernight)
				{
					PostLevel(KeyLevelKind.OvernightHigh, _overnightHigh, bar);
					PostLevel(KeyLevelKind.OvernightLow, _overnightLow, bar);
				}
			}

			if (regular && time >= openingEnd && !_openingPosted)
			{
				_openingPosted = true;

				if (LevelOpeningRange && _hasOpening)
				{
					PostLevel(KeyLevelKind.OpeningRangeHigh, _openingHigh, bar);
					PostLevel(KeyLevelKind.OpeningRangeLow, _openingLow, bar);
				}
			}

			var swept = TakeKeyLevels(bar, candle);

			if (regular)
			{
				Extend(ref _hasRth, ref _rthHigh, ref _rthLow, candle);

				if (time < openingEnd)
					Extend(ref _hasOpening, ref _openingHigh, ref _openingLow, candle);
			}
			else if (!_overnightPosted)
				Extend(ref _hasOvernight, ref _overnightHigh, ref _overnightLow, candle);

			if (LevelEqual)
				FindEqualLevels(bar);

			return swept;
		}

		private static void Extend(ref bool has, ref decimal high, ref decimal low, IndicatorCandle candle)
		{
			high = has ? Math.Max(high, candle.High) : candle.High;
			low = has ? Math.Min(low, candle.Low) : candle.Low;
			has = true;
		}

		private void PostLevel(KeyLevelKind kind, decimal price, int fromBar, int firstSwing = -1, int secondSwing = -1)
		{
			_keyLevels.Add(new KeyLevel { Kind = kind, Price = price, FromBar = fromBar, FirstSwingBar = firstSwing, SecondSwingBar = secondSwing });
		}

		private void EndLevel(KeyLevel level, KeyLevelState state, int bar)
		{
			level.State = state;
			level.EndBar = bar;
			_keyLevels.Remove(level);
			_endedLevels.Add(level);
		}

		// A bar that trades beyond a level takes it: a sweep when it closes back inside, a break
		// otherwise. Equal highs / lows that got too old expire.
		private (KeyLevel Low, KeyLevel High) TakeKeyLevels(int bar, IndicatorCandle candle)
		{
			KeyLevel sweptLow = null;
			KeyLevel sweptHigh = null;
			var minDepth = MinSweepDepth(bar);

			foreach (var level in _keyLevels.Where(l => l.FromBar <= bar).ToList())
			{
				var beyond = level.IsHigh ? candle.High > level.Price : candle.Low < level.Price;

				if (!beyond)
				{
					if (level.Kind >= KeyLevelKind.EqualHighs && bar - level.SecondSwingBar > EqualLookbackBars)
						EndLevel(level, KeyLevelState.Expired, bar);

					continue;
				}

				var back = level.IsHigh ? candle.Close < level.Price : candle.Close > level.Price;
				EndLevel(level, back ? KeyLevelState.Swept : KeyLevelState.Broken, bar);

				// swept, but only a deep enough sweep gives a signal
				var depth = level.IsHigh ? candle.High - level.Price : level.Price - candle.Low;

				if (!back || depth < minDepth)
					continue;

				if (level.IsHigh)
					sweptHigh = MoreImportant(sweptHigh, level);
				else
					sweptLow = MoreImportant(sweptLow, level);
			}

			var offset = ArrowOffsetTicks * TickSize;

			if (sweptHigh != null)
			{
				_lastHighKeySweepBar = bar;
				_lastHighKeySweep = sweptHigh;
				_bearSweep[bar] = candle.High + offset;
			}

			if (sweptLow != null)
			{
				_lastLowKeySweepBar = bar;
				_lastLowKeySweep = sweptLow;
				_bullSweep[bar] = candle.Low - offset;
			}

			return (sweptLow, sweptHigh);
		}

		// the prior day before the overnight range, the opening range, then equal highs / lows;
		// between two of a kind the further one (it took more stops)
		private static KeyLevel MoreImportant(KeyLevel best, KeyLevel level)
		{
			if (best == null)
				return level;

			var rank = (int)level.Kind / 2;
			var bestRank = (int)best.Kind / 2;

			if (rank != bestRank)
				return rank < bestRank ? level : best;

			if (level.Price != best.Price)
				return level.IsHigh == level.Price > best.Price ? level : best;

			return level.FromBar < best.FromBar ? level : best;
		}

		// Confirms the swing SwingStrength bars back, and pairs it with an earlier swing at the
		// same price (within Equal level match) that nothing traded beyond in between
		private void FindEqualLevels(int bar)
		{
			var pivot = bar - SwingStrength;

			if (pivot - SwingStrength < 0)
				return;

			if (IsSwing(pivot, true))
			{
				PairSwing(pivot, true, bar);
				_swingHighs.Add(pivot);
			}

			if (IsSwing(pivot, false))
			{
				PairSwing(pivot, false, bar);
				_swingLows.Add(pivot);
			}

			_swingHighs.RemoveAll(p => pivot - p > EqualLookbackBars);
			_swingLows.RemoveAll(p => pivot - p > EqualLookbackBars);
		}

		// above (below) the SwingStrength bars before it, and not below (above) the ones after it
		private bool IsSwing(int pivot, bool high)
		{
			var extreme = high ? GetCandle(pivot).High : GetCandle(pivot).Low;

			for (var i = pivot - SwingStrength; i <= pivot + SwingStrength; i++)
			{
				if (i == pivot)
					continue;

				var value = high ? GetCandle(i).High : GetCandle(i).Low;
				var beyond = high ? value > extreme : value < extreme;
				var equal = value == extreme;

				if (beyond || (equal && i < pivot))
					return false;
			}

			return true;
		}

		private void PairSwing(int second, bool high, int bar)
		{
			var swings = high ? _swingHighs : _swingLows;
			var tolerance = EqualToleranceTicks * TickSize;
			var price = high ? GetCandle(second).High : GetCandle(second).Low;
			var kind = high ? KeyLevelKind.EqualHighs : KeyLevelKind.EqualLows;

			for (var i = swings.Count - 1; i >= 0; i--)
			{
				var first = swings[i];

				if (second - first > EqualLookbackBars)
					break;

				var other = high ? GetCandle(first).High : GetCandle(first).Low;

				if (Math.Abs(other - price) > tolerance)
					continue;

				var level = high ? Math.Max(other, price) : Math.Min(other, price);
				var clear = true;

				for (var b = first + 1; b < second && clear; b++)
					clear = high ? GetCandle(b).High <= level : GetCandle(b).Low >= level;

				if (!clear)
					continue;

				// one level per price
				if (!_keyLevels.Any(l => l.Kind == kind && Math.Abs(l.Price - level) <= tolerance))
					PostLevel(kind, level, bar + 1, first, second);

				return;
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

		// The price inside the closed bar where the most contracts traded, when it stands out:
		// among the biggest of the recent bars (or at least Min filled volume), and Filled volume
		// vs bar average times the bar's average per price. Whoever was on the passive side there got filled - resting bids when sellers
		// hit them (bid volume), resting offers when buyers lifted them (ask volume).
		private void DetectFootprintFill(int bar, IndicatorCandle candle)
		{
			var levels = candle.GetAllPriceLevels();
			PriceVolumeInfo best = null;
			var total = 0m;
			var count = 0;

			if (levels != null)
			{
				foreach (var level in levels)
				{
					total += level.Volume;
					count++;

					if (best == null || level.Volume > best.Volume || (level.Volume == best.Volume && level.Price < best.Price))
						best = level;
				}
			}

			// the size a big fill takes comes from the bars before this one
			var peak = best != null && best.Volume > 0 ? best.Volume : 0;
			var minimum = FillMinimum();
			RememberPeak(peak);
			_fillThreshold = FillMinimum();

			if (peak <= 0 || minimum <= 0)
				return;

			var threshold = Math.Max(minimum, (decimal)FillVolumeMultiplier * total / count);

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

		// What a big fill takes: Min filled volume, or - among the biggest of recent bars - the
		// busiest price of a bar ranked at the Top share of the last Fill lookback bars (0 until
		// MinFillHistory of them had a footprint)
		private decimal FillMinimum()
		{
			if (FillSize == FillSizeRule.FixedContracts)
				return FillMinVolume;

			var count = _sortedPeaks.Count;

			if (count < MinFillHistory)
				return 0;

			var rank = Math.Max(1, (count * FillTopPercent + 99) / 100);
			return _sortedPeaks[count - rank];
		}

		// the busiest price of the bar joins the recent ones; the oldest leaves after Fill lookback bars
		private void RememberPeak(decimal peak)
		{
			_recentPeaks.Enqueue(peak);

			if (peak > 0)
			{
				var at = _sortedPeaks.BinarySearch(peak);
				_sortedPeaks.Insert(at < 0 ? ~at : at, peak);
			}

			while (_recentPeaks.Count > FillLookbackBars)
			{
				var old = _recentPeaks.Dequeue();

				if (old > 0)
					_sortedPeaks.RemoveAt(_sortedPeaks.BinarySearch(old));
			}
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

		// One price of the order book changed. Levels of at least the resting order size
		// (RestingMinimum) are followed as resting orders; when one drops below that it is leaving the book, and
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

			// an order keeps the size it needed when it appeared
			var needed = order != null ? order.Threshold : volume > 0 ? RestingMinimum() : decimal.MaxValue;

			if (volume >= needed)
			{
				if (order == null)
				{
					order = new RestingOrder
					{
						Price = price,
						IsBid = isBid,
						FirstBar = LiveBar,
						FirstTime = MarketTime,
						TradedAtStart = TradedAgainst(price, isBid),
						Threshold = needed
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

		// What a resting order takes now: Min resting order, or Resting order vs typical level
		// times the median size of the prices in the book (Min resting order until it has
		// MinBookLevels prices)
		private decimal RestingMinimum()
		{
			if (RestingSize == RestingSizeRule.FixedContracts)
				return RestingOrderMin;

			var sizes = _bids.Values.Concat(_asks.Values).OrderBy(v => v).ToList();

			if (sizes.Count < MinBookLevels)
				return RestingOrderMin;

			var middle = sizes.Count / 2;
			var median = sizes.Count % 2 == 1 ? sizes[middle] : (sizes[middle - 1] + sizes[middle]) / 2;
			return Math.Max(1, Math.Ceiling(median * (decimal)RestingMultiplier));
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

			// as big as a big fill: Min filled volume, or what the recent bars' big fills took
			var minimum = FillSize == FillSizeRule.FixedContracts || _fillThreshold <= 0 ? FillMinVolume : _fillThreshold;

			if (order.Traded < minimum)
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
			bool sweptLows, bool sweptHighs, KeyLevel keyLow, KeyLevel keyHigh, ref List<PendingAlert> alerts)
		{
			// outside the signal hours nothing is taken, not even for the statistics
			if (!InSignalHours(candle.Time))
				return;

			var longTrade = EnableBuySignals ? TryBuildSignal(bar, candle, true, bullZone, bullFill, sweptLows, keyLow) : null;
			var shortTrade = EnableShortSignals ? TryBuildSignal(bar, candle, false, bearZone, bearFill, sweptHighs, keyHigh) : null;

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

					if (zone != null && IsZoneTrigger(trade.Trigger))
						zone.SignalShown = true;

					if (fill != null && trade.Trigger == TriggerType.Fill)
						fill.SignalShown = true;
				}

				RegisterSignal(trade, candle, ref alerts);
			}
		}

		private SignalTrade TryBuildSignal(int bar, IndicatorCandle candle, bool isLong, FvgZone zone, FillEvent fill, bool sweepNow,
			KeyLevel keySweepNow)
		{
			var lastSweep = isLong ? _lastLowSweepBar : _lastHighSweepBar;
			var lastKeySweep = isLong ? _lastLowKeySweepBar : _lastHighKeySweepBar;
			var sweepBefore = lastSweep >= 0 && bar - lastSweep <= ConfluenceBars;
			var keySweepBefore = lastKeySweep >= 0 && bar - lastKeySweep <= ConfluenceBars;

			// a gap reaction after a sweep of a key level outranks one after a sweep of any swing
			var fvgTrigger = keySweepBefore ? TriggerType.KeySweepThenFvg : sweepBefore ? TriggerType.SweepThenFvg : TriggerType.Fvg;
			TriggerType trigger;

			switch (SignalSource)
			{
				case SignalMode.FvgReactionOnly when zone != null:
					trigger = fvgTrigger;
					break;

				case SignalMode.LiquiditySweepOnly when keySweepNow != null:
					trigger = TriggerType.KeySweep;
					break;

				case SignalMode.LiquiditySweepOnly when sweepNow:
					trigger = TriggerType.Sweep;
					break;

				case SignalMode.SweepThenFvg when zone != null && (sweepBefore || keySweepBefore):
					trigger = fvgTrigger;
					break;

				case SignalMode.FillReactionOnly when fill != null:
					trigger = TriggerType.Fill;
					break;

				case SignalMode.KeyLevelSweeps when zone != null && keySweepBefore:
					trigger = TriggerType.KeySweepThenFvg;
					break;

				case SignalMode.KeyLevelSweeps when keySweepNow != null:
					trigger = TriggerType.KeySweep;
					break;

				case SignalMode.AnyTrigger when zone != null:
					trigger = fvgTrigger;
					break;

				case SignalMode.AnyTrigger when fill != null:
					trigger = TriggerType.Fill;
					break;

				case SignalMode.AnyTrigger when keySweepNow != null:
					trigger = TriggerType.KeySweep;
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

			var fromZone = IsZoneTrigger(trigger);
			var fromFill = trigger == TriggerType.Fill;
			var sweptLevel = trigger == TriggerType.KeySweep ? keySweepNow
				: trigger == TriggerType.KeySweepThenFvg ? (isLong ? _lastLowKeySweep : _lastHighKeySweep)
				: null;

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
			var limit = Entry == EntryRule.LimitPullback;

			// a limit order a share of the signal bar's range better than its close
			if (limit)
				entry -= direction * Math.Round((candle.High - candle.Low) * PullbackPercent / 100m / tickSize, MidpointRounding.AwayFromZero) * tickSize;

			var (target, risk, triggerTicks, locked, breakEven) = BracketFor(bar, candle, isLong, entry);
			var timeBucket = TimeBucket(candle.Time);
			var volatilityBucket = VolatilityBucket(bar);
			var path = ModelPath(isLong, trigger, timeBucket, volatilityBucket, withTrend, confirmations);

			return new SignalTrade
			{
				EntryBar = bar,
				EntryTime = candle.Time,
				IsLong = isLong,
				EntryPrice = entry,
				TakeProfitPrice = entry + direction * target * tickSize,
				StopLossPrice = entry - direction * risk * tickSize,
				HasBreakEven = breakEven,
				TriggerPrice = entry + direction * triggerTicks * tickSize,
				BreakEvenPrice = entry + direction * locked * tickSize,
				TargetTicks = target,
				RiskTicks = risk,
				TriggerTicks = triggerTicks,
				LockedTicks = locked,
				LimitEntry = limit,
				Pending = limit,
				ThroughPrice = entry - direction * tickSize,
				FillBar = limit ? -1 : bar,
				ExpiryBar = limit ? bar + LimitValidBars : -1,
				SignalClose = candle.Close,
				TimeBucket = timeBucket,
				VolatilityBucket = volatilityBucket,
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
				SweptLevel = sweptLevel,
				Estimate = _model.Estimate(path, PriorOdds(target, risk, triggerTicks, locked, breakEven), ProbabilitySmoothing,
					target, breakEven ? locked : 0, risk)
			};
		}

		// The groups a signal's odds come from, broadest first: its direction, its trigger, then
		// each split switched on. Each group adds a base-16 digit to the one above it.
		private long[] ModelPath(bool isLong, TriggerType trigger, int timeBucket, int volatilityBucket, bool withTrend, int confirmations)
		{
			var path = new List<long>(6);
			long group = isLong ? 1 : 2;
			path.Add(group);
			group = group * 16 + (int)trigger + 1;
			path.Add(group);

			void Split(bool on, int value)
			{
				if (!on)
					return;

				group = group * 16 + value + 1;
				path.Add(group);
			}

			Split(OddsByTime, timeBucket);
			Split(OddsByVolatility, volatilityBucket);
			Split(OddsByTrend, withTrend ? 1 : 0);
			Split(OddsByConfirmations, confirmations);
			return path.ToArray();
		}

		private long[] ModelPath(SignalTrade trade)
		{
			return ModelPath(trade.IsLong, trade.Trigger, trade.TimeBucket, trade.VolatilityBucket, trade.WithTrend, trade.Confirmations);
		}

		// first 30 minutes of regular hours (0), the morning (1), midday (2), the afternoon (3), outside regular hours (4)
		private int TimeBucket(DateTime candleTime)
		{
			var time = NewYorkTime(candleTime).TimeOfDay;

			if (!IsRegularHours(time))
				return 4;

			var since = time - RegularHoursStart;
			return since < TimeSpan.FromMinutes(30) ? 0 : since < TimeSpan.FromHours(2) ? 1 : since < TimeSpan.FromHours(4.5) ? 2 : 3;
		}

		// quiet (0), normal (1) or busy (2): the ATR before the signal against ten times as many bars
		private int VolatilityBucket(int bar)
		{
			var longer = AverageRange(bar - 1, AtrPeriod * 10);

			if (longer <= 0)
				return 1;

			var ratio = AverageRange(bar - 1) / longer;
			return ratio < 0.8m ? 0 : ratio > 1.25m ? 2 : 1;
		}

		// The bracket of a trade entered at `entry` off the signal bar: take profit, stop loss,
		// break-even trigger and the profit the moved stop keeps, in ticks from the entry
		private (int Target, int Risk, int Trigger, int Locked, bool BreakEven) BracketFor(int bar, IndicatorCandle candle, bool isLong, decimal entry)
		{
			if (Bracket == BracketRule.FixedTicks)
				return (TakeProfitTicks, StopLossTicks, BreakEvenTriggerTicks, EffectiveBreakEvenStopTicks, BreakEvenEnabled);

			var tick = TickSize;
			decimal risk;
			double ratio;

			if (Bracket == BracketRule.AtrMultiple)
			{
				risk = (decimal)StopLossAtr * AverageRange(bar - 1) / tick;
				ratio = TakeProfitAtr / StopLossAtr;
			}
			else
			{
				var stop = isLong ? candle.Low - StopBufferTicks * tick : candle.High + StopBufferTicks * tick;
				risk = (isLong ? entry - stop : stop - entry) / tick;
				ratio = RewardRisk;
			}

			// the stop within Min / Max stop, the take profit in proportion to it
			var riskTicks = (int)Math.Round(risk, MidpointRounding.AwayFromZero);
			riskTicks = Math.Max(MinStopTicks, Math.Min(Math.Max(MinStopTicks, MaxStopTicks), riskTicks));
			var target = Math.Max(1, (int)Math.Round(riskTicks * (decimal)ratio, MidpointRounding.AwayFromZero));
			var trigger = (int)Math.Round(target * BreakEvenTriggerPercent / 100m, MidpointRounding.AwayFromZero);
			var locked = Math.Max(0, Math.Min((int)Math.Round(target * BreakEvenStopPercent / 100m, MidpointRounding.AwayFromZero), trigger - 1));
			var breakEven = BreakEvenTriggerPercent > 0 && trigger > 0 && trigger < target;
			return (target, riskTicks, breakEven ? trigger : 0, breakEven ? locked : 0, breakEven);
		}

		private void RegisterSignal(SignalTrade trade, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			var bar = trade.EntryBar;

			_trades.Add(trade);
			_openTrades.Add(trade);

			// a real-time signal waits for the executor, which decides once the update is done (a
			// hidden one only gets a line in its log)
			if (_realtime && ExecuteSignals)
				_execCandidates.Add(trade);

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
				var setup = string.Join(", ", PatternNames(trade.CandlePatterns).Prepend(SetupName(trade)));
				var message = $"{Side(trade)}{(trade.LimitEntry ? " limit" : string.Empty)} @ {FormatPrice(trade.EntryPrice)} ({setup}): {OddsText(trade, " / ")}"
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

				var result = Walk(trade, bar, start, newHigh, newLow, candle.Close, out var filled, out var moved, out var ambiguous);

				if (filled && _realtime && AlertOnTradeResult && trade.IsShown)
					QueueAlert(ref alerts, $"{Side(trade)} limit filled @ {FormatPrice(trade.EntryPrice)}", trade.IsLong ? BuyColor : ShortColor);

				if (moved && _realtime && AlertOnTradeResult && trade.IsShown && result.Outcome == TradeOutcome.Open)
				{
					var message = $"{Side(trade)} from {FormatPrice(trade.EntryPrice)}: +{trade.TriggerTicks}t reached, "
						+ $"stop moved to {FormatPrice(trade.BreakEvenPrice)} (+{trade.LockedTicks}t)";

					QueueAlert(ref alerts, message, BreakEvenPen.Color.Convert());
				}

				if (result.Outcome == TradeOutcome.Open)
					continue;

				trade.AmbiguousExit = ambiguous;
				CloseTrade(trade, result.Outcome, bar, result.ExitPrice, ref alerts);
				_openTrades.RemoveAt(i);
			}
		}

		// Moves one trade along a stretch of trading: fills a waiting limit order and moves the stop
		// as the stretch reaches them. How it ends is left to the caller.
		private PathResult Walk(SignalTrade trade, int bar, decimal start, decimal? newHigh, decimal? newLow, decimal close,
			out bool filled, out bool moved, out bool ambiguous)
		{
			var result = SettleStretch(trade, start, newHigh, newLow, close, SameBarRule, out ambiguous);
			filled = trade.Pending && result.Filled;
			moved = result.BreakEvenActive && !trade.BreakEvenActive;

			if (filled)
			{
				trade.Pending = false;
				trade.FillBar = bar;
			}

			if (moved)
			{
				trade.BreakEvenActive = true;
				trade.BreakEvenBar = bar;
			}

			return result;
		}

		private void ExpireStaleTrades(int bar, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			for (var i = _openTrades.Count - 1; i >= 0; i--)
			{
				var trade = _openTrades[i];
				var outcome = StaleOutcome(trade, bar);

				if (outcome == TradeOutcome.Open)
					continue;

				CloseTrade(trade, outcome, bar, outcome == TradeOutcome.Missed ? trade.EntryPrice : candle.Close, ref alerts);
				_openTrades.RemoveAt(i);
			}
		}

		// Whether a trade runs out of time on a closed bar: a limit order that waited its bars
		// without a fill is cancelled (Missed), a trade Max bars in trade old expires. Open: neither.
		private TradeOutcome StaleOutcome(SignalTrade trade, int bar)
		{
			if (trade.Pending)
				return bar >= trade.ExpiryBar ? TradeOutcome.Missed : TradeOutcome.Open;

			return MaxBarsInTrade > 0 && bar - trade.FillBar >= MaxBarsInTrade ? TradeOutcome.Expired : TradeOutcome.Open;
		}

		private void ExpireAllOpenTrades(int bar, IndicatorCandle candle, ref List<PendingAlert> alerts)
		{
			foreach (var trade in _openTrades)
			{
				if (trade.Pending)
					CloseTrade(trade, TradeOutcome.Missed, bar, trade.EntryPrice, ref alerts);
				else
					CloseTrade(trade, TradeOutcome.Expired, bar, candle.Close, ref alerts);
			}

			_openTrades.Clear();
		}

		private void CloseTrade(SignalTrade trade, TradeOutcome outcome, int bar, decimal exitPrice, ref List<PendingAlert> alerts)
		{
			EndTrade(trade, outcome, bar, exitPrice);

			// a limit order that never filled is no trade: nothing made, nothing to learn
			if (outcome == TradeOutcome.Missed)
			{
				if (trade.IsShown)
					_missed++;

				return;
			}

			var ticks = ResultTicks(trade);

			// only a real ending (TP, break-even or SL) teaches the model - an expired trade reached none
			if (outcome != TradeOutcome.Expired)
			{
				_model.Record(ModelPath(trade), outcome);

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

		private static void EndTrade(SignalTrade trade, TradeOutcome outcome, int bar, decimal exitPrice)
		{
			trade.Outcome = outcome;
			trade.ExitBar = bar;
			trade.ExitPrice = exitPrice;
			trade.Pending = false;
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

		// A limit order waiting for its fill fills where the path first trades a tick through
		// it, at its own price; from there the trade walks the rest of the path.
		private static PathResult WalkPath(SignalTrade trade, IReadOnlyList<decimal> path)
		{
			if (!trade.Pending)
				return WalkTrade(trade, path, trade.BreakEvenActive);

			for (var i = 0; i < path.Count; i++)
			{
				if (trade.IsLong ? path[i] > trade.ThroughPrice : path[i] < trade.ThroughPrice)
					continue;

				var rest = new List<decimal>(path.Count - i + 1) { trade.EntryPrice };

				for (var k = i; k < path.Count; k++)
					rest.Add(path[k]);

				return WalkTrade(trade, rest, false);
			}

			return new PathResult(TradeOutcome.Open, false, 0, false);
		}

		// Follows one trade along a price path. Moving in its favour can reach the break-even
		// trigger (which moves the stop) and then the TP; moving against it can reach whichever
		// stop is active. The first price is checked both ways, which also settles a gap
		// through a level at the open.
		private static PathResult WalkTrade(SignalTrade trade, IReadOnlyList<decimal> path, bool active)
		{
			var direction = trade.IsLong ? 1 : -1;
			var takeProfit = (trade.TakeProfitPrice - trade.EntryPrice) * direction;
			var trigger = (trade.TriggerPrice - trade.EntryPrice) * direction;
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

		#region Execution

		// The executor turns the chart's own signals into orders. It decides nothing the chart has not:
		//   * it takes the real-time signals the chart shows, so the signal hours, One trade at a time
		//     and the show filters apply to it as they do to the chart;
		//   * it enters as the signal's trade does: at market for an entry at the close, with a limit
		//     order at the trade's price for a limit entry, cancelled when the chart's order expires;
		//   * once filled it rests the trade's own stop and take profit (the stop first), moves the
		//     stop to the break-even price when the trade reaches its trigger, and closes at market
		//     whenever the chart's trade ends while the position is still open (a take profit only
		//     touched, a trade that expired or closed at the session end);
		//   * it holds one position at a time and only trades in real time, never history.
		// It follows its own copy of the chart's trade, walked through the same prices by the same
		// rules (Walk, StaleOutcome, SessionEndsAfter), so recalculating the chart never changes what
		// happens to a position that is already open.
		//
		// Paper trading, the default, runs the same code against a simulator: market and stop orders
		// fill Slippage ticks worse than the price that reached them, limit orders at their own price
		// once price trades a tick through them (as the chart's limit entries do). Live orders go
		// through ATAS's TradingManager only with Paper trading off, the chart's account being the
		// Live account and a daily loss limit set. The chart's odds and the backtest don't make the
		// signals profitable - see the README - so paper is where to start.

		// Once per real-time update, after the chart's trades have moved: follow the prices, manage
		// the position, then decide on the signals of the bar that just closed
		private void SyncExecution(int bar)
		{
			if (!ExecuteSignals && _execPosition == null)
			{
				_execCandidates.Clear();
				_execBar = -1;
				return;
			}

			try
			{
				var candle = GetCandle(bar);
				_execNow = candle.LastTime > candle.Time ? candle.LastTime : candle.Time;
				CurrentDay(PaperTrading);

				if (_execConfigDirty)
				{
					_execConfigDirty = false;
					LogConfiguration();
				}

				FollowPrices(bar);
				_execPrice = candle.Close;
				ManagePosition();

				foreach (var trade in _execCandidates)
					ConsiderSignal(trade);
			}
			catch (Exception ex)
			{
				// a fault in the executor halts it, never the chart
				ExecutionFault(ex);
			}

			_execCandidates.Clear();
		}

		// Walks the executor's orders and its copy of the chart's trade through what traded since the
		// last update, the way AdvanceOpenTrades walks the chart's trades: the rest of the bar it saw
		// last, then each newer one. Paper orders fill on the way.
		private void FollowPrices(int bar)
		{
			// the chart reloaded its bars under the executor: find the bar it was on
			if (_execBar >= 0 && (_execBar >= CurrentBar || GetCandle(_execBar).Time != _execBarTime))
				RelocateExecution();

			var pos = _execPosition;
			var first = pos == null || _execBar < 0 ? bar : Math.Min(_execBar, bar);

			for (var b = first; b <= bar; b++)
			{
				var candle = GetCandle(b);
				var start = candle.Open;
				decimal? newHigh = candle.High;
				decimal? newLow = candle.Low;

				if (b == _execBar)
				{
					start = _execClose;
					newHigh = candle.High > _execHigh ? candle.High : (decimal?)null;
					newLow = candle.Low < _execLow ? candle.Low : (decimal?)null;
				}

				_execBar = b;
				_execBarTime = candle.Time;
				_execHigh = candle.High;
				_execLow = candle.Low;
				_execClose = candle.Close;

				if (pos == null)
					continue;

				// the chart's side first, so a position closed on the same prices logs how the chart's trade ended
				FollowTrade(pos.Trade, b, start, newHigh, newLow, candle.Close, b < bar);

				if (pos.Paper && !pos.Done)
					PaperWalk(pos, start, newHigh, newLow, candle.Close);
			}
		}

		// The executor's copy of the chart's trade, moved as the chart moves its own: along the
		// stretch, then - once its bar has closed - through the same time limits
		private void FollowTrade(SignalTrade trade, int bar, decimal start, decimal? newHigh, decimal? newLow, decimal close, bool barClosed)
		{
			if (trade.Outcome != TradeOutcome.Open || bar <= trade.EntryBar)
				return;

			var result = Walk(trade, bar, start, newHigh, newLow, close, out _, out _, out var ambiguous);

			if (result.Outcome != TradeOutcome.Open)
			{
				trade.AmbiguousExit = ambiguous;
				EndTrade(trade, result.Outcome, bar, result.ExitPrice);
				return;
			}

			if (!barClosed)
				return;

			var outcome = StaleOutcome(trade, bar);

			if (outcome == TradeOutcome.Open && SessionEndsAfter(bar))
				outcome = trade.Pending ? TradeOutcome.Missed : TradeOutcome.Expired;

			if (outcome != TradeOutcome.Open)
				EndTrade(trade, outcome, bar, outcome == TradeOutcome.Missed ? trade.EntryPrice : GetCandle(bar).Close);
		}

		// The chart reloaded its bars (more history, another time frame): the bar the executor was on
		// has another number. Found again by its time, the trade copy's bar numbers move with it.
		private void RelocateExecution()
		{
			var found = -1;

			for (var b = CurrentBar - 1; b >= 0 && found < 0; b--)
			{
				var time = GetCandle(b).Time;

				if (time == _execBarTime)
					found = b;
				else if (time < _execBarTime)
					break;
			}

			var pos = _execPosition;

			if (found < 0)
			{
				if (pos != null && !pos.Done)
					Log("WARN", pos, "the chart reloaded its bars and the one the position was on is gone: its bar counts (limit validity, Max bars in trade) may be off");

				_execBar = -1;
				return;
			}

			var shift = found - _execBar;
			_execBar = found;

			if (pos == null || shift == 0)
				return;

			var trade = pos.Trade;
			trade.EntryBar += shift;

			if (trade.FillBar >= 0)
				trade.FillBar += shift;

			if (trade.ExpiryBar >= 0)
				trade.ExpiryBar += shift;

			if (trade.BreakEvenBar >= 0)
				trade.BreakEvenBar += shift;

			if (trade.ExitBar >= 0)
				trade.ExitBar += shift;
		}

		// What the chart's trade means for the position now
		private void ManagePosition()
		{
			var pos = _execPosition;

			if (pos == null)
				return;

			var trade = pos.Trade;

			if (trade.Outcome != TradeOutcome.Open && !pos.ChartEnded)
			{
				pos.ChartEnded = true;
				var row = LogRow("CHART", pos, null, pos.Paper);
				row["chart_outcome"] = OutcomeBadge(trade.Outcome);
				row["chart_ticks"] = trade.Outcome == TradeOutcome.Missed ? string.Empty : Number(ResultTicks(trade));
				row["note"] = ResultText(trade);
				WriteLog(row, pos.Paper);
			}

			if (!pos.Done)
			{
				if (!pos.Paper)
				{
					CheckAccount(pos);
					CheckProtection(pos);
				}

				if (trade.Outcome != TradeOutcome.Open)
				{
					// the chart's trade is over, or its limit order expired: so is the position
					CancelExecOrder(pos, pos.Entry, trade.Outcome == TradeOutcome.Missed ? "the chart's limit order expired unfilled" : "the chart's trade ended");

					if (pos.Open > 0)
						BeginExit(pos, $"the chart's trade ended ({OutcomeBadge(trade.Outcome)})");
				}
				else if (pos.Open == 0 && !ExecuteSignals)
					CancelExecOrder(pos, pos.Entry, "Execute signals was switched off");
				else if (pos.Open > 0 && trade.BreakEvenActive && !pos.StopMoved && !pos.Exiting)
					MoveStopToBreakEven(pos);

				ContinueExit(pos);
				CheckDone(pos);
			}

			if (pos.Done && pos.ChartEnded)
				_execPosition = null;
		}

		// A real-time signal: taken, or skipped with the reason in the log
		private void ConsiderSignal(SignalTrade trade)
		{
			var paper = PaperTrading;
			var reason = EntryBlocker(trade, paper);

			if (reason != null)
			{
				var row = LogRow("SKIPPED", null, trade, paper);
				row["note"] = reason;
				WriteLog(row, paper);
				Notify($"{Side(trade)} {SetupName(trade)} skipped: {reason}", DimTextColor, false, false);
				return;
			}

			RetireExecPosition();

			var pos = new ExecPosition
			{
				SignalId = SignalId(trade),
				Trade = trade.Clone(),
				Paper = paper,
				IsLong = trade.IsLong,
				Contracts = Contracts,
				TickValue = EffectiveTickValue(),
				Account = paper ? "paper" : TradingManager.Portfolio.AccountID,
				StopPrice = trade.StopLossPrice,
				FlatSince = DateTime.MinValue,
				UnprotectedSince = DateTime.MinValue
			};

			_execPosition = pos;
			Log("SIGNAL", pos, trade.LimitEntry ? "taken: limit entry" : "taken: market entry");
			Notify($"{ModeName(pos)} {Side(trade)} {SetupText(trade)}: {(trade.LimitEntry ? "limit" : "market")} order sent", pos.IsLong ? BuyColor : ShortColor,
				false, false);

			var type = trade.LimitEntry ? OrderTypes.Limit : OrderTypes.Market;
			pos.Entry = NewOrder(pos, OrderRole.Entry, pos.IsLong, type, pos.Contracts, trade.EntryPrice, null);
			Place(pos, pos.Entry);
		}

		// Why a real-time signal is not taken (null: it is)
		private string EntryBlocker(SignalTrade trade, bool paper)
		{
			if (!trade.IsShown)
				return "hidden on the chart (One trade at a time, Min TP probability or Min expected ticks)";

			if (_execHalted)
				return $"execution is halted: {_execHaltReason}";

			if (_execPosition != null && !_execPosition.Done)
				return "a position is already open (execution holds one at a time)";

			if (trade.Outcome != TradeOutcome.Open)
				return "the chart's trade ended before an order could go in";

			if (!paper)
			{
				var notArmed = LiveBlocker();

				if (notArmed != null)
					return $"live trading is not armed: {notArmed}";

				var account = TradingManager.Position;

				if (account != null && account.Volume != 0)
					return $"the account already holds a position ({Qty(account.Volume)}) in this instrument";
			}

			var tickValue = EffectiveTickValue();

			if (tickValue <= 0)
				return "no tick value: set Tick value";

			var day = CurrentDay(paper);

			if (DailyLossLimit > 0 && day.Pnl <= -DailyLossLimit)
				return $"the daily loss limit is reached ({Money(day.Pnl)} today, limit {Money(-DailyLossLimit)})";

			var price = _execPrice;
			var direction = trade.IsLong ? 1 : -1;

			if ((price - trade.StopLossPrice) * direction <= 0 || (trade.TakeProfitPrice - price) * direction <= 0)
				return $"price ({FormatPrice(price)}) is already beyond the stop or the target";

			if (DailyLossLimit > 0 && SkipTradesBeyondLimit)
			{
				var worst = WorstLoss(trade, price, tickValue);
				var room = DailyLossLimit + day.Pnl;

				if (worst > room)
					return $"its stop could lose {Money(worst)}, more than the {Money(room)} left before the daily loss limit";
			}

			// the last check, as passing it takes the account for this chart
			if (!paper && !HoldLiveAccount(out var holder))
				return $"another chart ({holder}) has a live trade on this account: one live trade per account at a time";

			return null;
		}

		// Why live orders can't go out now (null: armed)
		private string LiveBlocker()
		{
			var manager = TradingManager;

			if (manager == null)
				return "the chart has no trading connection";

			var portfolio = manager.Portfolio;
			var security = manager.Security;

			if (portfolio == null || security == null)
				return "no account or instrument is selected on the chart";

			if (LiveAccount.Length == 0)
				return "Live account is empty";

			if (!string.Equals(portfolio.AccountID?.Trim(), LiveAccount, StringComparison.OrdinalIgnoreCase))
				return $"the chart's account ({portfolio.AccountID}) is not the Live account ({LiveAccount})";

			if (DailyLossLimit <= 0)
				return "live trading needs a daily loss limit";

			if (security.TickSize > 0 && security.TickSize != TickSize)
				return $"the instrument's tick size ({security.TickSize.ToString(CultureInfo.InvariantCulture)}) is not the chart's";

			return null;
		}

		// Two charts armed for one account would each keep their own position and loss limit, so a
		// live entry first takes the account; another chart's signal is skipped until this position
		// is done, or this indicator is removed
		private bool HoldLiveAccount(out string holder)
		{
			var account = TradingManager?.Portfolio?.AccountID?.Trim() ?? string.Empty;

			lock (LiveAccountHolders)
			{
				if (LiveAccountHolders.TryGetValue(account, out var other) && !ReferenceEquals(other, this))
				{
					holder = string.IsNullOrWhiteSpace(other.InstrumentInfo?.Instrument) ? "another instrument" : other.InstrumentInfo.Instrument;
					return false;
				}

				LiveAccountHolders[account] = this;
				holder = null;
				return true;
			}
		}

		private void ReleaseLiveAccount()
		{
			lock (LiveAccountHolders)
			{
				foreach (var account in LiveAccountHolders.Where(h => ReferenceEquals(h.Value, this)).Select(h => h.Key).ToList())
					LiveAccountHolders.Remove(account);
			}
		}

		// what the trade loses if its stop is hit: from where it enters, with the slippage of a market
		// entry and of the stop, and the commission
		private decimal WorstLoss(SignalTrade trade, decimal price, decimal tickValue)
		{
			var direction = trade.IsLong ? 1 : -1;
			var entry = !trade.LimitEntry ? price : trade.IsLong ? Math.Min(price, trade.EntryPrice) : Math.Max(price, trade.EntryPrice);
			var ticks = (entry - trade.StopLossPrice) * direction / TickSize + (trade.LimitEntry ? 1 : 2) * SlippageTicks;
			return (ticks * tickValue + CommissionPerContract) * Contracts;
		}

		// $ per tick and contract: the setting, or what the trading connection says when that is more,
		// so a wrong setting can only make the loss limit stricter
		private decimal EffectiveTickValue()
		{
			return Math.Max(TickValue, TradingManager?.Security?.TickCost ?? 0m);
		}

		// a new position takes over from the last one, whose chart trade may still be running
		private void RetireExecPosition()
		{
			var old = _execPosition;

			if (old != null && !old.ChartEnded)
				Log("CHART", old, "no longer followed: a new position started before the chart's trade ended");

			_execPosition = null;
			_execFillIds.Clear();
		}

		private ExecOrder NewOrder(ExecPosition pos, OrderRole role, bool isBuy, OrderTypes type, decimal quantity, decimal price, string ocoGroup)
		{
			var order = new Order
			{
				Direction = isBuy ? OrderDirections.Buy : OrderDirections.Sell,
				Type = type,
				QuantityToFill = quantity,
				Comment = $"FVG {pos.SignalId} {RoleName(role)}"
			};

			if (type == OrderTypes.Limit)
				order.Price = price;
			else if (type == OrderTypes.Stop)
				order.TriggerPrice = price;

			if (ocoGroup != null)
				order.OCOGroup = ocoGroup;

			if (!pos.Paper)
			{
				order.Portfolio = TradingManager?.Portfolio;
				order.Security = TradingManager?.Security;
			}

			var exec = new ExecOrder { Order = order, Role = role, IsBuy = isBuy, Quantity = quantity };
			pos.Orders.Add(exec);
			return exec;
		}

		// sends an order: to the simulator for a paper position, to ATAS (once the lock is released)
		// for a live one
		private void Place(ExecPosition pos, ExecOrder order)
		{
			LogOrder("ORDER", pos, order, order.Quantity, OrderPrice(order.Order));

			if (pos.Paper)
				PaperPlace(pos, order);
			else
				Request(new LiveRequest(LiveAction.Place, order.Order));
		}

		private void CancelExecOrder(ExecPosition pos, ExecOrder order, string reason)
		{
			if (order == null || !order.Working || order.CancelSent)
				return;

			order.CancelSent = true;
			LogOrder("CANCEL", pos, order, order.Quantity - order.Filled, OrderPrice(order.Order), reason);

			if (!pos.Paper)
			{
				Request(new LiveRequest(LiveAction.Cancel, order.Order));
				return;
			}

			_paperBook.Remove(order);
			order.Order.State = OrderStates.Done;
			order.Working = false;
			LogOrder("CANCELLED", pos, order, order.Quantity - order.Filled, OrderPrice(order.Order));
		}

		// moves an order's price or resizes it; a live order is replaced by an updated copy
		private void ModifyExecOrder(ExecPosition pos, ExecOrder order, decimal price, decimal quantity, string reason)
		{
			var old = order.Order;
			var updated = pos.Paper ? old : old.Clone();

			if (updated.Type == OrderTypes.Stop)
				updated.TriggerPrice = price;
			else if (updated.Type == OrderTypes.Limit)
				updated.Price = price;

			updated.QuantityToFill = quantity;
			order.Quantity = quantity;

			if (pos.Paper)
				updated.Unfilled = quantity - order.Filled;
			else
			{
				order.Replaced.Add(old);
				order.Order = updated;
				Request(new LiveRequest(LiveAction.Modify, old, updated));
			}

			LogOrder("MODIFY", pos, order, quantity, price, reason);
		}

		// A fill of one of the executor's orders, paper or live
		private void ApplyFill(ExecPosition pos, ExecOrder order, decimal price, decimal quantity)
		{
			var filled = Math.Min(quantity, order.Quantity - order.Filled);

			if (filled <= 0)
			{
				LogOrder("WARN", pos, order, quantity, price, "a fill beyond the order's size was ignored");
				return;
			}

			order.Filled += filled;

			if (order.Filled >= order.Quantity)
			{
				order.Working = false;
				order.FillPending = false;
			}

			if (order.Role == OrderRole.Entry)
			{
				pos.AverageEntry = (pos.AverageEntry * pos.Entered + price * filled) / (pos.Entered + filled);
				pos.Entered += filled;
				pos.Open += filled;
				LogOrder("FILL", pos, order, filled, price);
				Notify($"{ModeName(pos)} {Side(pos.Trade)} {Qty(filled)} filled @ {FormatPrice(price)}", pos.IsLong ? BuyColor : ShortColor);
				Protect(pos);
				return;
			}

			var closing = Math.Min(filled, pos.Open);
			pos.ExitTicks += (price - pos.AverageEntry) / TickSize * (pos.IsLong ? 1 : -1) * closing;
			pos.Open -= closing;
			LogOrder("FILL", pos, order, filled, price);

			if (closing < filled)
				Halt(pos, $"the {RoleName(order.Role)} order filled {Qty(filled - closing)} more than the position held: the account may hold that much the other way, check it in ATAS");

			if (pos.Open > 0)
				ResizeBracket(pos);
			else
			{
				// flat: nothing that could open a new position may stay behind
				foreach (var other in pos.Orders)
					CancelExecOrder(pos, other, "the position is flat");
			}

			ContinueExit(pos);
			CheckDone(pos);
		}

		// After an entry fill: the chart trade's own stop and take profit for what is held (the stop
		// first), or resized when more of the entry fills
		private void Protect(ExecPosition pos)
		{
			var trade = pos.Trade;

			if (pos.Exiting || trade.Outcome != TradeOutcome.Open)
			{
				BeginExit(pos, "the chart's trade ended before the entry filled");
				ContinueExit(pos);
				return;
			}

			var stop = pos.StopMoved ? trade.BreakEvenPrice : trade.StopLossPrice;

			// price has reached the stop by the time the entry filled: no stop order could rest there
			if (pos.IsLong ? _execPrice <= stop : _execPrice >= stop)
			{
				BeginExit(pos, "price was through the stop when the entry filled");
				return;
			}

			pos.StopPrice = stop;

			if (pos.Stop != null)
			{
				ResizeBracket(pos);
				return;
			}

			var group = $"FVG {pos.SignalId}";
			pos.Stop = NewOrder(pos, OrderRole.StopLoss, !pos.IsLong, OrderTypes.Stop, pos.Open, stop, group);
			pos.TakeProfit = NewOrder(pos, OrderRole.TakeProfit, !pos.IsLong, OrderTypes.Limit, pos.Open, trade.TakeProfitPrice, group);
			Place(pos, pos.Stop);
			Place(pos, pos.TakeProfit);
		}

		// the stop and the take profit always cover what is held
		private void ResizeBracket(ExecPosition pos)
		{
			foreach (var order in new[] { pos.Stop, pos.TakeProfit })
			{
				if (order == null || !order.Working || order.CancelSent)
					continue;

				var size = order.Filled + pos.Open;

				if (size != order.Quantity)
					ModifyExecOrder(pos, order, OrderPrice(order.Order), size, "resized to the position");
			}
		}

		// The chart's trade reached its break-even trigger: the stop moves to its break-even price
		private void MoveStopToBreakEven(ExecPosition pos)
		{
			var trade = pos.Trade;
			var price = trade.BreakEvenPrice;
			pos.StopMoved = true;

			if (pos.IsLong ? _execPrice <= price : _execPrice >= price)
			{
				BeginExit(pos, "price was back through the break-even price before the stop could move");
				return;
			}

			pos.StopPrice = price;

			if (pos.Stop != null && pos.Stop.Working && !pos.Stop.CancelSent)
				ModifyExecOrder(pos, pos.Stop, price, pos.Stop.Quantity, $"break-even: +{trade.TriggerTicks}t reached, the stop moves to +{trade.LockedTicks}t");
		}

		// Closes the position at market: cancels the stop and the take profit first, and sends the
		// closing order once neither can fill any more (a leg that fills meanwhile closes it anyway)
		private void BeginExit(ExecPosition pos, string reason)
		{
			if (pos.Exiting || pos.Done)
				return;

			pos.Exiting = true;
			Log("EXIT", pos, reason);
			CancelExecOrder(pos, pos.Entry, reason);
			CancelExecOrder(pos, pos.TakeProfit, reason);
			CancelExecOrder(pos, pos.Stop, reason);
			ContinueExit(pos);
		}

		private void ContinueExit(ExecPosition pos)
		{
			if (!pos.Exiting || pos.Done || pos.Open <= 0 || pos.ExitFailed)
				return;

			// the closing order goes once nothing else can fill
			if (pos.Orders.Any(o => o.Working || o.FillPending))
				return;

			pos.Exit = NewOrder(pos, OrderRole.Exit, !pos.IsLong, OrderTypes.Market, pos.Open, 0, null);
			Place(pos, pos.Exit);
		}

		// Flat with nothing working: the position is over and counts toward the day
		private void CheckDone(ExecPosition pos)
		{
			if (pos.Done || pos.Open > 0 || pos.Orders.Any(o => o.Working || o.FillPending))
				return;

			pos.Done = true;

			if (!pos.Paper)
				ReleaseLiveAccount();

			if (pos.Entered == 0)
			{
				Log("NOFILL", pos, "the entry was not filled: no trade");
				Notify($"{ModeName(pos)} {Side(pos.Trade)}: the entry was not filled", NeutralColor, false, false);
				return;
			}

			var ticks = pos.ExitTicks / pos.Entered;
			var dollars = pos.ExitTicks * pos.TickValue - CommissionPerContract * pos.Entered;
			var day = CurrentDay(pos.Paper);
			day.Pnl += dollars;
			day.Trades++;

			var row = LogRow("CLOSED", pos, null, pos.Paper);
			row["pnl_ticks"] = Number(ticks);
			row["pnl_usd"] = Cents(dollars);
			row["day_pnl_usd"] = Cents(day.Pnl);
			row["chart_outcome"] = OutcomeBadge(pos.Trade.Outcome);
			row["chart_ticks"] = pos.Trade.Outcome == TradeOutcome.Open || pos.Trade.Outcome == TradeOutcome.Missed ? string.Empty : Number(ResultTicks(pos.Trade));
			row["note"] = $"{Qty(pos.Entered)} contract{(pos.Entered == 1 ? string.Empty : "s")}, average entry {Number(pos.AverageEntry)}, tick value {Cents(pos.TickValue)}, commission {Cents(CommissionPerContract)} each";
			WriteLog(row, pos.Paper);

			var color = dollars > 0 ? BullColor : dollars < 0 ? BearColor : NeutralColor;
			Notify($"{ModeName(pos)} {Side(pos.Trade)} closed {ticks.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)}t, {Money(dollars)} · today {Money(day.Pnl)}", color);

			if (DailyLossLimit > 0 && day.Pnl <= -DailyLossLimit && !day.Tripped)
			{
				day.Tripped = true;
				Log("BREAKER", pos, $"daily loss limit reached: {Money(day.Pnl)} today, limit {Money(-DailyLossLimit)}; no new entries until the next trading day");
				Notify($"Daily loss limit reached ({Money(day.Pnl)} today): no new {(pos.Paper ? "paper" : "live")} entries until the next trading day", BearColor, true);
			}
		}

		// Live: the account has to hold what the executor holds. When it shows no position for a few
		// seconds while the executor holds one, and none of the executor's own exits is filling, the
		// position was closed outside the indicator: its orders are cancelled and execution halts.
		private void CheckAccount(ExecPosition pos)
		{
			var account = TradingManager?.Position;

			bool Filling(ExecOrder o)
			{
				return o.FillPending || new[] { o.Order }.Concat(o.Replaced).Any(v => v.Status() == OrderStatus.Filled || v.Status() == OrderStatus.PartlyFilled);
			}

			if (pos.Open <= 0 || account == null || account.Volume != 0 || pos.Orders.Any(o => o.Role != OrderRole.Entry && Filling(o)))
			{
				pos.FlatSince = DateTime.MinValue;
				return;
			}

			if (pos.FlatSince == DateTime.MinValue)
			{
				pos.FlatSince = _execNow;
				return;
			}

			if (_execNow - pos.FlatSince < AccountFlatGrace)
				return;

			// the fill that closed it was not the executor's: its result is estimated at the last price
			pos.ExitTicks += (_execPrice - pos.AverageEntry) / TickSize * (pos.IsLong ? 1 : -1) * pos.Open;
			pos.Open = 0;
			Halt(pos, "the account shows no position while the indicator held one: it was closed outside the indicator (result estimated at the last price). Check the account, then switch Execute signals off and on");

			foreach (var order in pos.Orders)
				CancelExecOrder(pos, order, "the position was closed outside the indicator");
		}

		// Live: an open position always has its stop working. One whose stop went away without the
		// executor asking (cancelled by hand, expired at the broker) is closed at market and execution
		// halts - after a few seconds, which leaves time for a take profit that filled as its OCO
		// partner was cancelled to report its fill.
		private void CheckProtection(ExecPosition pos)
		{
			if (pos.Open <= 0 || pos.Exiting || pos.Stop == null || pos.Stop.Working || pos.Stop.FillPending)
			{
				pos.UnprotectedSince = DateTime.MinValue;
				return;
			}

			if (pos.UnprotectedSince == DateTime.MinValue)
			{
				pos.UnprotectedSince = _execNow;
				return;
			}

			if (_execNow - pos.UnprotectedSince < AccountFlatGrace)
				return;

			Halt(pos, "the position's stop was cancelled outside the indicator: the unprotected position is closed at market");
			BeginExit(pos, "unprotected: the stop was cancelled outside the indicator");
		}

		// stops new entries until Execute signals is switched off and on again
		private void Halt(ExecPosition pos, string reason)
		{
			if (!_execHalted)
			{
				_execHalted = true;
				_execHaltReason = reason;
			}

			Log("HALT", pos, reason);
			Notify($"Execution halted: {reason}", BearColor, true);
		}

		private void ExecutionFault(Exception ex)
		{
			try
			{
				Halt(_execPosition, $"internal error: {ex.Message}");
			}
			catch (Exception)
			{
				// nothing more can be done here, and the chart must keep working
				_execHalted = true;
				_execHaltReason = ex.Message;
			}
		}

		// ---- paper orders: the simulator ----

		private void PaperPlace(ExecPosition pos, ExecOrder order)
		{
			var o = order.Order;
			o.Id = $"PAPER-{++_paperOrderCount}";
			o.State = OrderStates.Active;
			o.Unfilled = order.Quantity;

			// a limit order through the market fills at once, at the market's better price
			decimal? fill;

			if (o.Type == OrderTypes.Limit)
				fill = (order.IsBuy ? _execPrice < o.Price : _execPrice > o.Price) ? _execPrice : (decimal?)null;
			else
				fill = PaperFillPrice(order, _execPrice);

			if (fill.HasValue)
				PaperFill(pos, order, fill.Value);
			else
				_paperBook.Add(order);
		}

		// Paper fills along one stretch of trading. When one update carries both a new high and a
		// new low, the side of the position's stop comes first.
		private void PaperWalk(ExecPosition pos, decimal start, decimal? newHigh, decimal? newLow, decimal close)
		{
			var path = new List<decimal>(4) { start };

			if (newHigh.HasValue && newLow.HasValue)
			{
				path.Add(pos.IsLong ? newLow.Value : newHigh.Value);
				path.Add(pos.IsLong ? newHigh.Value : newLow.Value);
			}
			else if (newHigh.HasValue || newLow.HasValue)
				path.Add(newHigh ?? newLow.Value);

			path.Add(close);

			foreach (var price in path)
			{
				_execPrice = price;

				// orders placed on the way wait for the next price
				foreach (var order in _paperBook.ToList())
				{
					if (!_paperBook.Contains(order))
						continue;

					var fill = PaperFillPrice(order, price);

					if (fill.HasValue)
						PaperFill(pos, order, fill.Value);
				}
			}
		}

		// where a paper order fills at this price, if it does
		private decimal? PaperFillPrice(ExecOrder order, decimal price)
		{
			var o = order.Order;
			var slippage = SlippageTicks * TickSize;

			switch (o.Type)
			{
				case OrderTypes.Limit:
					// at its own price, once price trades a tick through it
					if (order.IsBuy ? price <= o.Price - TickSize : price >= o.Price + TickSize)
						return o.Price;

					return null;

				case OrderTypes.Stop:
					// once price reaches it, Slippage ticks worse than the price that did
					if (order.IsBuy ? price < o.TriggerPrice : price > o.TriggerPrice)
						return null;

					return order.IsBuy ? Math.Max(price, o.TriggerPrice) + slippage : Math.Min(price, o.TriggerPrice) - slippage;

				default:
					return order.IsBuy ? price + slippage : price - slippage;
			}
		}

		private void PaperFill(ExecPosition pos, ExecOrder order, decimal price)
		{
			_paperBook.Remove(order);
			order.Order.State = OrderStates.Done;
			order.Order.Unfilled = 0;
			ApplyFill(pos, order, price, order.Quantity - order.Filled);
		}

		// ---- live orders: ATAS's TradingManager ----

		private void Request(LiveRequest request)
		{
			if (_execRequests == null)
				_execRequests = new List<LiveRequest>();

			_execRequests.Add(request);
		}

		// what an execution step raised: its alerts join the calculation's, its live orders are returned
		private List<LiveRequest> TakeExecutionOutput(ref List<PendingAlert> alerts)
		{
			if (_execAlerts != null)
			{
				if (alerts == null)
					alerts = _execAlerts;
				else
					alerts.AddRange(_execAlerts);

				_execAlerts = null;
			}

			var requests = _execRequests;
			_execRequests = null;
			return requests;
		}

		// Live order calls, made outside the lock and one after the other (the stop before the take
		// profit), with ATAS's async calls - the synchronous ones are obsolete. ATAS reports back on
		// its own threads; a call that fails counts as a rejection of its order.
		private async Task SendLiveRequests(List<LiveRequest> requests)
		{
			foreach (var request in requests)
			{
				try
				{
					var manager = TradingManager ?? throw new InvalidOperationException("the chart has no trading connection");

					switch (request.Action)
					{
						case LiveAction.Place:
							await manager.OpenOrderAsync(request.Order, setDefaultQuantity: false, askConfirmation: false).ConfigureAwait(false);
							break;

						case LiveAction.Modify:
							await manager.ModifyOrderAsync(request.Order, request.NewOrder, askConfirmation: false).ConfigureAwait(false);
							break;

						default:
							await manager.CancelOrderAsync(request.Order, askConfirmation: false).ConfigureAwait(false);
							break;
					}
				}
				catch (Exception ex)
				{
					var failed = request;
					ExecutionCallback(() => OrderCallFailed(failed, ex.Message));
				}
			}
		}

		// an execution step on one of ATAS's threads: under the lock, then its alerts and orders
		private void ExecutionCallback(Action step)
		{
			List<PendingAlert> alerts = null;
			List<LiveRequest> requests;

			lock (_sync)
			{
				try
				{
					step();
				}
				catch (Exception ex)
				{
					ExecutionFault(ex);
				}

				requests = TakeExecutionOutput(ref alerts);
			}

			if (alerts != null)
				FireAlerts(alerts);

			if (requests != null)
				_ = SendLiveRequests(requests);
		}

		private void OrderCallFailed(LiveRequest request, string message)
		{
			var pos = _execPosition;
			var exec = pos?.Find(request.NewOrder ?? request.Order, null);

			if (exec == null || pos.Paper)
				return;

			switch (request.Action)
			{
				case LiveAction.Place:
					OrderFailed(pos, exec, message);
					break;

				case LiveAction.Modify:
					ModifyFailed(pos, exec, request.Order, message);
					break;

				default:
					CancelFailed(pos, exec, message);
					break;
			}
		}

		// ATAS: a new fill on the chart's account and instrument
		protected override void OnNewMyTrade(MyTrade myTrade)
		{
			base.OnNewMyTrade(myTrade);
			ExecutionCallback(() => LiveFill(myTrade));
		}

		protected override void OnOrderChanged(Order order)
		{
			base.OnOrderChanged(order);
			ExecutionCallback(() => LiveOrderChanged(order));
		}

		protected override void OnOrderRegisterFailed(Order order, string message)
		{
			base.OnOrderRegisterFailed(order, message);

			ExecutionCallback(() =>
			{
				var pos = _execPosition;
				var exec = order == null || pos == null || pos.Paper ? null : pos.Find(order, order.Id);

				if (exec != null && (exec.Working || exec.FillPending))
					OrderFailed(pos, exec, message);
			});
		}

		protected override void OnOrderModifyFailed(Order order, Order newOrder, string error)
		{
			base.OnOrderModifyFailed(order, newOrder, error);

			ExecutionCallback(() =>
			{
				var pos = _execPosition;
				var exec = pos == null || pos.Paper ? null : pos.Find(newOrder, newOrder?.Id) ?? pos.Find(order, order?.Id);

				if (exec != null && order != null)
					ModifyFailed(pos, exec, order, error);
			});
		}

		protected override void OnOrderCancelFailed(Order order, string message)
		{
			base.OnOrderCancelFailed(order, message);

			ExecutionCallback(() =>
			{
				var pos = _execPosition;
				var exec = order == null || pos == null || pos.Paper ? null : pos.Find(order, order.Id);

				if (exec != null)
					CancelFailed(pos, exec, message);
			});
		}

		// The indicator is removed (or ATAS closes): a waiting entry must not fill later without its
		// stop, so it is cancelled; an open live position keeps its stop and take profit
		protected override void OnDispose()
		{
			ExecutionCallback(() =>
			{
				var pos = _execPosition;

				// nothing here manages an account any more
				ReleaseLiveAccount();

				if (pos == null || pos.Done)
					return;

				CancelExecOrder(pos, pos.Entry, "the indicator was removed");

				if (!pos.Paper && pos.Open > 0)
					Log("WARN", pos, "the indicator was removed with this live position open: its stop and take profit stay with the broker, but nothing moves the stop or closes it any more; manage it in ATAS");
			});

			base.OnDispose();
		}

		private void LiveFill(MyTrade myTrade)
		{
			var pos = _execPosition;
			var exec = myTrade == null || pos == null || pos.Paper ? null : pos.Find(myTrade.Order, myTrade.OrderId);

			// not one of the executor's orders (a manual trade, another strategy), or reported twice
			if (exec == null || (!string.IsNullOrEmpty(myTrade.Id) && !_execFillIds.Add(myTrade.Id)))
				return;

			ApplyFill(pos, exec, myTrade.Price, Math.Abs(myTrade.Volume));
		}

		private void LiveOrderChanged(Order order)
		{
			var pos = _execPosition;
			var exec = order == null || pos == null || pos.Paper ? null : pos.Find(order, order.Id);

			// an earlier version of a modified order doesn't speak for it
			if (exec == null || (!ReferenceEquals(exec.Order, order) && order.Id != exec.Order.Id))
				return;

			var status = order.Status();

			if (order.State == OrderStates.Failed)
			{
				if (exec.Working)
					OrderFailed(pos, exec, "rejected");
			}
			else if (status == OrderStatus.Canceled)
			{
				if (exec.Working || exec.FillPending)
				{
					exec.Working = false;
					exec.FillPending = false;
					LogOrder("CANCELLED", pos, exec, exec.Quantity - exec.Filled, OrderPrice(exec.Order),
						exec.CancelSent ? null : "not asked for by the indicator (by hand, the OCO partner's fill, or the broker)");
				}
			}
			else if (status == OrderStatus.Filled && exec.Filled < exec.Quantity)
			{
				// its fills are on their way
				exec.Working = false;
				exec.FillPending = true;
			}

			ContinueExit(pos);
			CheckDone(pos);
		}

		// A live order ATAS or the broker refused
		private void OrderFailed(ExecPosition pos, ExecOrder exec, string message)
		{
			exec.Working = false;
			exec.FillPending = false;
			LogOrder("REJECTED", pos, exec, exec.Quantity - exec.Filled, OrderPrice(exec.Order), message);

			switch (exec.Role)
			{
				case OrderRole.Entry:
					Notify($"Live {Side(pos.Trade)} entry rejected: {message}", BearColor, true);
					break;

				case OrderRole.Exit:
					pos.ExitFailed = true;
					Halt(pos, $"the closing order was rejected ({message}): close the position in ATAS");
					break;

				default:
					if (pos.Open > 0)
					{
						Halt(pos, $"the {RoleName(exec.Role)} order was rejected ({message}): the unprotected position is closed at market");
						BeginExit(pos, "unprotected: a bracket order was rejected");
					}

					break;
			}

			ContinueExit(pos);
			CheckDone(pos);
		}

		// the order keeps its old price and size
		private void ModifyFailed(ExecPosition pos, ExecOrder exec, Order previous, string message)
		{
			if (exec.Replaced.Remove(previous))
				exec.Order = previous;

			exec.Quantity = previous.QuantityToFill;

			if (exec.Role == OrderRole.StopLoss)
				pos.StopPrice = previous.TriggerPrice;

			LogOrder("REJECTED", pos, exec, exec.Quantity, OrderPrice(previous), $"the change was rejected: {message}");
			Notify($"The live {RoleName(exec.Role)} order could not be changed ({message}): it stays at {FormatPrice(OrderPrice(previous))}", BearColor, true);
		}

		// A cancel that didn't go through. Usually the order filled meanwhile (a take profit touched as
		// the chart's trade ended) and its fill says so; one the broker still shows working is asked
		// again, twice at most.
		private void CancelFailed(ExecPosition pos, ExecOrder exec, string message)
		{
			LogOrder("WARN", pos, exec, exec.Quantity - exec.Filled, OrderPrice(exec.Order), $"cancel failed: {message}");
			var status = exec.Order.Status();

			if (!exec.Working || exec.Order.State != OrderStates.Active || (status != OrderStatus.Placed && status != OrderStatus.PartlyFilled))
				return;

			exec.CancelSent = false;

			if (++exec.CancelAttempts < 3)
			{
				CancelExecOrder(pos, exec, "the broker still shows it working: asked again");
				return;
			}

			Halt(pos, $"the {RoleName(exec.Role)} order could not be cancelled ({message}): manage the position in ATAS");
		}

		// ---- the log and the panel ----

		// "20260302-093000-BUY": the signal bar's New York open time and the side, as the chart's
		// tooltip names the signal
		private static string SignalId(SignalTrade trade)
		{
			return $"{NewYorkTime(trade.EntryTime).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Side(trade)}";
		}

		// A MODE row whenever what the executor does changes: on or off, paper or live, the size, the
		// limits, and the chart settings that shape its trades
		private void LogConfiguration()
		{
			var platform = TradingManager?.Security?.TickCost ?? 0m;
			var tickValue = platform > 0 && platform != TickValue
				? $"tick value {Money(TickValue)} (the connection says {Money(platform)}; using {Money(EffectiveTickValue())})"
				: $"tick value {Money(TickValue)}";

			var config = string.Join(" · ", new[]
			{
				!ExecuteSignals ? "execution off (an open position is still managed)" : PaperTrading ? "paper" : $"live, account {LiveAccount}",
				$"{Contracts} contract{(Contracts == 1 ? string.Empty : "s")}",
				DailyLossLimit > 0
					? $"daily loss limit {Money(DailyLossLimit)}{(SkipTradesBeyondLimit ? ", skipping trades that could breach it" : string.Empty)}"
					: "no daily loss limit",
				tickValue,
				$"commission {Money(CommissionPerContract)}",
				$"slippage {SlippageTicks}t",
				BracketText(),
				Entry == EntryRule.LimitPullback ? $"limit entry {PullbackPercent}% back, valid {LimitValidBars} bars" : "entry at the close",
				$"signals {HoursText()}",
				OneTradeAtATime ? "one trade at a time" : "overlapping signals on the chart"
			});

			if (config == _execConfig)
				return;

			_execConfig = config;
			Log("MODE", null, config);
		}

		// The day's result in one mode. A new trading day (18:00 New York) starts from zero, or from
		// the day's log when the indicator was restarted during it.
		private ExecDay CurrentDay(bool paper)
		{
			var day = paper ? _paperDay : _liveDay;

			if (_execNow.Year < 2)
				return day;

			var today = TradingDayOf(NewYorkTime(_execNow));

			if (day.Day != today)
			{
				day.Day = today;
				day.Pnl = 0;
				day.Trades = 0;
				day.Tripped = false;
				RestoreDay(day, paper);

				// each day's file starts with the settings
				if (paper == PaperTrading)
				{
					_execConfig = string.Empty;
					_execConfigDirty = true;
				}
			}

			return day;
		}

		// the closed trades already in the day's log, so a restart can't reset the daily loss limit
		private void RestoreDay(ExecDay day, bool paper)
		{
			try
			{
				var path = LogPath(day.Day, paper);

				if (!File.Exists(path))
					return;

				var lines = File.ReadAllLines(path);
				var header = lines.Length > 0 ? SplitCsv(lines[0]) : Array.Empty<string>();
				var eventColumn = Array.IndexOf(header, "event");
				var dayColumn = Array.IndexOf(header, "day_pnl_usd");

				if (eventColumn < 0 || dayColumn < 0)
					return;

				foreach (var line in lines.Skip(1))
				{
					var cells = SplitCsv(line);

					if (cells.Length > Math.Max(eventColumn, dayColumn) && cells[eventColumn] == "CLOSED"
						&& decimal.TryParse(cells[dayColumn], NumberStyles.Number, CultureInfo.InvariantCulture, out var pnl))
					{
						day.Pnl = pnl;
						day.Trades++;
					}
				}
			}
			catch (Exception ex)
			{
				_execLogError = $"Execution log not read: {ex.Message}";
				return;
			}

			if (day.Trades == 0)
				return;

			day.Tripped = DailyLossLimit > 0 && day.Pnl <= -DailyLossLimit;
			Log("RESTORE", null, $"{day.Trades} closed trade{(day.Trades == 1 ? string.Empty : "s")} today, {Money(day.Pnl)}: read back from this log", paper);
		}

		// One row of the execution log. Every row names the signal behind it - setup, patterns,
		// confirmations, the odds on its label - so it can be matched with the chart's scoreboard.
		private Dictionary<string, string> LogRow(string evt, ExecPosition pos, SignalTrade trade, bool paper)
		{
			var newYork = NewYorkTime(_execNow);
			var row = new Dictionary<string, string>
			{
				["logged_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
				["bar_time_ny"] = newYork.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
				["trading_day"] = TradingDayOf(newYork).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				["mode"] = paper ? "paper" : "live",
				["account"] = pos?.Account ?? (paper ? "paper" : TradingManager?.Portfolio?.AccountID ?? string.Empty),
				["instrument"] = InstrumentInfo?.Instrument ?? string.Empty,
				["event"] = evt
			};

			trade = trade ?? pos?.Trade;

			if (trade != null)
			{
				var odds = LabelPercents(trade);
				row["signal"] = SignalId(trade);
				row["signal_bar"] = trade.EntryBar.ToString(CultureInfo.InvariantCulture);
				row["side"] = Side(trade);
				row["setup"] = SetupName(trade);
				row["trigger"] = TriggerLabel(trade.Trigger);
				row["patterns"] = string.Join("; ", PatternNames(trade.CandlePatterns));
				row["confirmations"] = trade.Confirmations.ToString(CultureInfo.InvariantCulture);
				row["trend"] = YesNo(trade.WithTrend);
				row["delta"] = YesNo(trade.DeltaConfirms);
				row["order_flow"] = YesNo(trade.FillConfirms);
				row["pattern"] = YesNo(trade.CandlePatterns != CandlePattern.None);
				row["odds_tp"] = odds[0].ToString(CultureInfo.InvariantCulture);
				row["odds_be"] = trade.HasBreakEven ? odds[1].ToString(CultureInfo.InvariantCulture) : string.Empty;
				row["odds_sl"] = odds[odds.Length - 1].ToString(CultureInfo.InvariantCulture);
				row["ev_ticks"] = LabelExpectedTicks(trade).ToString(CultureInfo.InvariantCulture);
				row["entry"] = Number(trade.EntryPrice);
				row["take_profit"] = Number(trade.TakeProfitPrice);
				row["stop_loss"] = Number(trade.StopLossPrice);
				row["be_trigger"] = trade.HasBreakEven ? Number(trade.TriggerPrice) : string.Empty;
				row["be_stop"] = trade.HasBreakEven ? Number(trade.BreakEvenPrice) : string.Empty;
			}

			if (pos != null)
				row["position"] = Qty(pos.IsLong ? pos.Open : -pos.Open);

			return row;
		}

		private void Log(string evt, ExecPosition pos, string note, bool? paper = null)
		{
			var mode = paper ?? pos?.Paper ?? PaperTrading;
			var row = LogRow(evt, pos, null, mode);
			row["note"] = note;
			WriteLog(row, mode);
		}

		private void LogOrder(string evt, ExecPosition pos, ExecOrder order, decimal quantity, decimal price, string note = null)
		{
			var row = LogRow(evt, pos, null, pos.Paper);
			row["order"] = RoleName(order.Role);
			row["type"] = $"{(order.IsBuy ? "buy" : "sell")} {order.Order.Type.ToString().ToLowerInvariant()}";
			row["qty"] = Qty(quantity);
			row["price"] = price > 0 ? Number(price) : string.Empty;
			row["note"] = note;
			WriteLog(row, pos.Paper);
		}

		// appends a row to the day's file (one per trading day, instrument and mode). A log that can't
		// be written never stops the executor; the panel says so.
		private void WriteLog(Dictionary<string, string> row, bool paper)
		{
			try
			{
				var path = LogPath(TradingDayOf(NewYorkTime(_execNow)), paper);
				Directory.CreateDirectory(Path.GetDirectoryName(path));

				// the header, with a byte-order mark so spreadsheets read the file as UTF-8
				if (!File.Exists(path))
					File.WriteAllText(path, string.Join(",", ExecutionLogColumns) + Environment.NewLine, new UTF8Encoding(true));

				var line = string.Join(",", ExecutionLogColumns.Select(c => Csv(row.TryGetValue(c, out var value) ? value : null)));
				File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
				_execLogError = null;
			}
			catch (Exception ex)
			{
				_execLogError = $"Execution log not written: {ex.Message}";
			}
		}

		private string LogPath(DateTime day, bool paper)
		{
			var folder = ExecutionLogFolder.Length > 0
				? ExecutionLogFolder
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "FvgExecution");
			var invalid = Path.GetInvalidFileNameChars();
			var name = string.IsNullOrWhiteSpace(InstrumentInfo?.Instrument) ? "chart" : InstrumentInfo.Instrument;
			var instrument = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
			return Path.Combine(folder, $"{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{instrument}_{(paper ? "paper" : "live")}.csv");
		}

		// The panel's last line, and an ATAS alert when asked for (errors, halts and the daily loss
		// limit always)
		private void Notify(string message, Color color, bool critical = false, bool alert = true)
		{
			_execLastEvent = message;
			_execLastEventColor = color;

			if (!_realtime || !(critical || (alert && AlertOnOrders)))
				return;

			if (_execAlerts == null)
				_execAlerts = new List<PendingAlert>();

			_execAlerts.Add(new PendingAlert(message, color));
		}

		// the panel's lines while execution is on or a position is still managed
		private List<(string Text, Color Color)> ExecutionPanelLines()
		{
			var pos = _execPosition;

			if (!ExecuteSignals && pos == null)
				return null;

			var paper = PaperTrading;
			var day = paper ? _paperDay : _liveDay;
			var today = _execNow.Year > 1 && day.Day == TradingDayOf(NewYorkTime(_execNow)) ? day.Pnl : 0m;
			var mode = !ExecuteSignals ? "off" : paper ? "PAPER" : "LIVE";
			var limit = DailyLossLimit > 0 ? $" of {Money(-DailyLossLimit)}" : " (no loss limit)";
			var head = ($"Execution {mode} · {Contracts} contract{(Contracts == 1 ? string.Empty : "s")} · today {Money(today)}{limit}", DimTextColor);

			if (_execHalted)
				head = ($"Execution HALTED: {_execHaltReason}", BearColor);
			else if (ExecuteSignals && DailyLossLimit > 0 && today <= -DailyLossLimit)
				head = ($"Execution {mode} stopped for today: {Money(today)} reached the {Money(-DailyLossLimit)} limit", BearColor);
			else if (ExecuteSignals && !paper && LiveBlocker() is string blocked)
				head = ($"Execution LIVE not armed: {blocked}", BearColor);

			var lines = new List<(string Text, Color Color)> { head };

			if (pos != null && !pos.Done)
				lines.Add((PositionLine(pos), pos.IsLong ? BuyColor : ShortColor));

			if (_execLastEvent != null)
				lines.Add(($"Last: {_execLastEvent}", _execLastEventColor));

			if (_execLogError != null)
				lines.Add((_execLogError, BearColor));

			return lines;
		}

		// "Paper BUY 1 @ 103.75 · TP 123.50 · stop 108.50 (break-even) · +12t"
		private string PositionLine(ExecPosition pos)
		{
			var name = $"{ModeName(pos)} {Side(pos.Trade)}";

			if (pos.Open == 0)
			{
				return pos.Entry.Order.Type == OrderTypes.Limit
					? $"{name} limit {Qty(pos.Contracts)} @ {FormatPrice(pos.Entry.Order.Price)}: waiting for a fill"
					: $"{name} {Qty(pos.Contracts)} at market: waiting for the fill";
			}

			var ticks = (_execPrice - pos.AverageEntry) / TickSize * (pos.IsLong ? 1 : -1);
			var stop = $"stop {FormatPrice(pos.StopPrice)}{(pos.StopMoved ? " (break-even)" : string.Empty)}";

			return $"{name} {Qty(pos.Open)} @ {FormatPrice(pos.AverageEntry)} · TP {FormatPrice(pos.Trade.TakeProfitPrice)} · {stop} · "
				+ $"{ticks.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t{(pos.Exiting ? " · closing" : string.Empty)}";
		}

		private static decimal OrderPrice(Order order)
		{
			return order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Type == OrderTypes.Limit ? order.Price : 0;
		}

		private static string RoleName(OrderRole role)
		{
			switch (role)
			{
				case OrderRole.TakeProfit:
					return "take profit";

				case OrderRole.StopLoss:
					return "stop";

				case OrderRole.Exit:
					return "exit";

				default:
					return "entry";
			}
		}

		private static string ModeName(ExecPosition pos)
		{
			return pos.Paper ? "Paper" : "Live";
		}

		// -$1,234.50
		private static string Money(decimal value)
		{
			return value.ToString("$#,##0.00;-$#,##0.00", CultureInfo.InvariantCulture);
		}

		// -1234.50, for the log
		private static string Cents(decimal value)
		{
			return value.ToString("0.00", CultureInfo.InvariantCulture);
		}

		private static string Number(decimal value)
		{
			return value.ToString("0.########", CultureInfo.InvariantCulture);
		}

		private static string Qty(decimal value)
		{
			return value.ToString("0.####", CultureInfo.InvariantCulture);
		}

		private static string Csv(string value)
		{
			if (string.IsNullOrEmpty(value))
				return string.Empty;

			return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
		}

		private static string[] SplitCsv(string line)
		{
			var cells = new List<string>();
			var cell = new StringBuilder();
			var quoted = false;

			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];

				if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
				{
					cell.Append('"');
					i++;
				}
				else if (c == '"')
					quoted = !quoted;
				else if (c == ',' && !quoted)
				{
					cells.Add(cell.ToString());
					cell.Clear();
				}
				else
					cell.Append(c);
			}

			cells.Add(cell.ToString());
			return cells.ToArray();
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
			List<KeyLevel> levels = null;
			List<SignalTrade> trades;
			PanelStats stats = null;
			var mouse = MouseLocationInfo.LastPosition;

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

				if (DrawKeyLevels)
				{
					levels = _keyLevels.Where(l => l.FromBar <= lastBar)
						.Concat(_endedLevels.Where(l => l.FromBar <= lastBar && l.EndBar >= firstBar))
						.Select(l => l.Clone())
						.ToList();
				}

				trades = _trades
					.Where(t => t.IsShown && t.EntryBar <= lastBar && (t.Outcome == TradeOutcome.Open || t.ExitBar >= firstBar))
					.Select(t => t.Clone())
					.ToList();

				// the scoreboard is only worked out when it is on screen
				if (ShowStatsPanel)
					stats = BuildPanelStats(ShowScoreboard || _lastPanel.Contains(mouse));
			}

			var hover = new Hover(mouse);
			var priceTags = new List<(string Text, int X, int Y, Color Color)>();

			if (zones != null)
				RenderZones(context, zones);

			if (levels != null)
				RenderKeyLevels(context, levels, hover);

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

			var overPanel = false;

			if (stats != null)
			{
				_lastPanel = RenderStatsPanel(context, stats);
				overPanel = _lastPanel.Contains(mouse);

				if (stats.Board != null && (ShowScoreboard || overPanel))
					RenderScoreboard(context, stats.Board, _lastPanel);
			}
			else
				_lastPanel = Rectangle.Empty;

			// over the panel the scoreboard is the tooltip
			if (hover.Lines != null && !overPanel)
				RenderTooltip(context, hover.Lines(), hover.Mouse);
		}

		private PanelStats BuildPanelStats(bool withScoreboard)
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
				Missed = _missed,
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
				Largest = resting.OrderByDescending(o => o.Volume).FirstOrDefault()?.Clone(),
				FillThreshold = FillSize == FillSizeRule.FixedContracts ? FillMinVolume : _fillThreshold,
				RestingThreshold = RestingMinimum(),
				LastBarTime = CurrentBar > 0 ? GetCandle(CurrentBar - 1).Time : DateTime.MinValue,
				Board = withScoreboard ? BuildScoreboard() : null,
				Execution = ExecutionPanelLines()
			};
		}

		// every closed signal, hidden ones included (expired ones are left out, as in the odds)
		private Scoreboard BuildScoreboard()
		{
			var board = new Scoreboard();
			var triggers = new Dictionary<TriggerType, ScoreRow>();
			var patterns = new Dictionary<CandlePattern, ScoreRow>();
			var noPattern = new ScoreRow { Name = "No pattern" };
			var buckets = new[] { (0, 20), (20, 30), (30, 40), (40, 50), (50, 60), (60, 101) }
				.Select(r => new OddsBucket { From = r.Item1, To = r.Item2 })
				.ToList();

			foreach (var trade in _trades)
			{
				if (trade.Outcome == TradeOutcome.Open || trade.Outcome == TradeOutcome.Expired || trade.Outcome == TradeOutcome.Missed)
					continue;

				board.Closed++;
				var ticks = ResultTicks(trade);

				if (!triggers.TryGetValue(trade.Trigger, out var row))
					triggers[trade.Trigger] = row = new ScoreRow { Name = TriggerLabel(trade.Trigger) };

				Score(row, trade, ticks);

				if (trade.CandlePatterns == CandlePattern.None)
					Score(noPattern, trade, ticks);

				foreach (var info in CandlePatternInfo)
				{
					if ((trade.CandlePatterns & info.Pattern) == 0)
						continue;

					if (!patterns.TryGetValue(info.Pattern, out var patternRow))
						patterns[info.Pattern] = patternRow = new ScoreRow { Name = info.Name };

					Score(patternRow, trade, ticks);
				}

				var chance = LabelPercents(trade)[0];
				var bucket = buckets.First(b => chance >= b.From && chance < b.To);
				bucket.Count++;

				if (trade.Outcome == TradeOutcome.TakeProfit)
					bucket.Hits++;
			}

			board.Triggers = triggers.OrderBy(kv => (int)kv.Key).Select(kv => kv.Value).ToList();
			board.Patterns = patterns.Values
				.Concat(noPattern.Count > 0 ? new[] { noPattern } : Array.Empty<ScoreRow>())
				.OrderByDescending(r => r.Count)
				.ThenBy(r => r.Name, StringComparer.Ordinal)
				.Take(ScoreboardPatterns)
				.ToList();
			board.OddsCheck = buckets.Where(b => b.Count > 0).ToList();
			return board;
		}

		private static void Score(ScoreRow row, SignalTrade trade, decimal ticks)
		{
			row.Count++;
			row.Ticks += ticks;

			if (trade.Outcome == TradeOutcome.TakeProfit)
				row.Wins++;
			else if (trade.Outcome == TradeOutcome.BreakEven)
				row.BreakEvens++;
			else
				row.Losses++;
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

		// Key levels as thin lines from the bar they count from: solid while waiting, ending in a
		// dot where a sweep took them, dashed where price broke through, faint once expired
		private void RenderKeyLevels(RenderContext context, List<KeyLevel> levels, Hover hover)
		{
			var right = ChartInfo.Region.Width;
			var region = ChartInfo.PriceChartContainer.Region;
			var font = SmallFont;
			var labels = new List<Rectangle>();

			foreach (var level in levels.OrderBy(l => l.FromBar))
			{
				var fresh = level.State == KeyLevelState.Fresh;
				var x1 = ChartInfo.GetXByBar(level.FromBar);
				var x2 = fresh ? right : ChartInfo.GetXByBar(level.EndBar, false);
				var y = ChartInfo.GetYByPrice(level.Price, false);

				if (x2 <= x1)
					continue;

				var alpha = fresh ? 190 : level.State == KeyLevelState.Swept ? 130 : 70;
				var pen = new RenderPen(WithAlpha(LevelColor, alpha)) { DashStyle = level.State == KeyLevelState.Broken ? DashStyle.Dash : DashStyle.Solid };
				context.DrawLine(pen, x1, y, x2, y);

				if (level.State == KeyLevelState.Swept)
					context.FillEllipse(WithAlpha(level.IsHigh ? BearColor : BullColor, 220), new Rectangle(x2 - 3, y - 3, 6, 6));

				// its name above a high, below a low, at the start of its visible part
				var text = LevelTag(level);
				var size = context.MeasureString(text, font);
				var labelX = Math.Max(x1, region.X) + 3;
				var rect = new Rectangle(labelX, level.IsHigh ? y - size.Height - 1 : y + 1, size.Width + 4, size.Height);

				if (rect.Right > x2 || labels.Any(r => r.IntersectsWith(rect)))
					continue;

				labels.Add(rect);
				context.DrawString(text, font, WithAlpha(LevelColor, Math.Max(alpha, 150)), rect.X + 2, rect.Y);

				var captured = level;

				if (rect.Contains(hover.Mouse))
					hover.Offer(1, () => KeyLevelTooltip(captured));
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

				if (trade.Pending || trade.Outcome == TradeOutcome.Missed)
				{
					var color = trade.IsLong ? BuyColor : ShortColor;
					var xa = ChartInfo.GetXByBar(trade.EntryBar, false);
					var xb = Math.Max(ChartInfo.GetXByBar(endBar, false), xa + 4);
					var yLimit = ChartInfo.GetYByPrice(trade.EntryPrice, false);
					context.DrawLine(new RenderPen(WithAlpha(color, trade.Pending ? 200 : 70)) { DashStyle = DashStyle.Dash }, xa, yLimit, xb, yLimit);

					if (trade.Pending)
						priceTags.Add(($"LMT {FormatPrice(trade.EntryPrice)}", xb + 4, yLimit, color));

					continue;
				}

				var x1 = ChartInfo.GetXByBar(trade.FillBar, false);
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
				var badge = trade.Pending ? "LIMIT" : OutcomeBadge(trade.Outcome);
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
			var text = trade.Outcome == TradeOutcome.Missed
				? "No fill"
				: $"{OutcomeBadge(trade.Outcome)} {SignedTicks((int)Math.Round(ResultTicks(trade), MidpointRounding.AwayFromZero))}t";
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

		// returns where it was drawn
		private Rectangle RenderStatsPanel(RenderContext context, PanelStats stats)
		{
			var breakEven = BracketHasBreakEven;
			var bracket = BracketText();

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
				($"Open {stats.Open} · Expired {stats.Expired}{(Entry == EntryRule.LimitPullback || stats.Missed > 0 ? $" · No fill {stats.Missed}" : string.Empty)}"
					+ $" · Hidden {stats.Filtered} · Model n={stats.ModelResolved}", DimTextColor),

				// were the labels right? what signals labelled with a positive / not positive
				// expected result really made (every settled signal, hidden ones included)
				($"Labelled EV>0: {TrackRecord(stats.PositiveEvTicks, stats.PositiveEvCount)}   "
					+ $"EV≤0: {TrackRecord(stats.NegativeEvTicks, stats.NegativeEvCount)}", DimTextColor),
				($"Fills {stats.Fills}: {stats.BullishFills} bullish · {stats.BearishFills} bearish reactions"
					+ (stats.FillThreshold > 0 ? $" · big ≥{FormatVolume(stats.FillThreshold)}" : string.Empty), DimTextColor)
			};

			// when signals may fire, and the New York time of the last bar to check it against
			var hours = $"Signals: {HoursText()}";

			if (stats.LastBarTime > DateTime.MinValue)
				hours += $" · last bar {NewYorkTime(stats.LastBarTime).ToString("HH:mm", CultureInfo.InvariantCulture)} New York";

			footer.Insert(1, (hours, DimTextColor));

			var restingSize = RestingSize == RestingSizeRule.FixedContracts
				? $"≥{RestingOrderMin}"
				: $"≥{FormatVolume(stats.RestingThreshold)} ({RestingMultiplier.ToString("0.#", CultureInfo.InvariantCulture)}× typical)";
			var restingLine = $"Resting {restingSize}: {stats.RestingBids} bids · {stats.RestingAsks} offers";

			if (stats.Largest != null)
				restingLine += $" · largest {FormatVolume(stats.Largest.Volume)} {(stats.Largest.IsBid ? "bid" : "offer")} @ {FormatPrice(stats.Largest.Price)}";

			footer.Add((restingLine, DimTextColor));

			var open = stats.OpenTrade;

			if (open != null && open.Pending)
			{
				footer.Add(($"Limit {Side(open)} @ {FormatPrice(open.EntryPrice)}: waiting for a fill until bar {open.ExpiryBar}",
					open.IsLong ? BuyColor : ShortColor));
			}
			else if (open != null)
			{
				// how far the open trade has moved, and the odds from here
				var excursion = (stats.LastPrice - open.EntryPrice) / TickSize * (open.IsLong ? 1 : -1);
				var stop = open.BreakEvenActive ? $", stop +{open.LockedTicks}t" : string.Empty;
				var odds = OddsText(open.HasBreakEven, LiveOdds(open, stats.LastPrice), " · ");

				footer.Add(($"Live {Side(open)} {excursion.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t{stop}:  {odds}",
					open.IsLong ? BuyColor : ShortColor));
			}

			if (stats.Execution != null)
				footer.AddRange(stats.Execution);

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

			return panel;
		}

		// The scoreboard, under the statistics panel (above it when the panel sits at the bottom):
		// every closed signal by trigger and by candlestick pattern, then how the labelled TP odds
		// held up
		private void RenderScoreboard(RenderContext context, Scoreboard board, Rectangle panel)
		{
			const int pad = 8;
			const int gap = 12;
			var font = LabelFont.RenderObject;
			var breakEven = BracketHasBreakEven;
			var title = board.Closed == 0
				? "Scoreboard: no closed signals yet"
				: $"Scoreboard: {board.Closed} closed signal{(board.Closed == 1 ? string.Empty : "s")}, hidden ones included";

			string[] Header(string first) => breakEven ? new[] { first, "n", "TP", "BE", "SL", "avg" } : new[] { first, "n", "TP", "SL", "avg" };

			string[] Cells(ScoreRow row)
			{
				var average = $"{(row.Ticks / row.Count).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}t";
				var n = row.Count.ToString(CultureInfo.InvariantCulture);

				return breakEven
					? new[] { row.Name, n, $"{row.Wins}", $"{row.BreakEvens}", $"{row.Losses}", average }
					: new[] { row.Name, n, $"{row.Wins}", $"{row.Losses}", average };
			}

			// (cells, is a header, average ticks for the color)
			var rows = new List<(string[] Cells, bool Header, decimal Average)>();

			if (board.Closed > 0)
			{
				rows.Add((Header("Setup"), true, 0));
				rows.AddRange(board.Triggers.Select(r => (Cells(r), false, r.Ticks / r.Count)));
				rows.Add((Header("Pattern"), true, 0));
				rows.AddRange(board.Patterns.Select(r => (Cells(r), false, r.Ticks / r.Count)));
			}

			// "said TP 20–30%: hit 24% of 41"
			var odds = board.OddsCheck
				.Select(b => $"Said TP {b.From}–{Math.Min(b.To, 100)}%: hit {(100.0 * b.Hits / b.Count).ToString("0", CultureInfo.InvariantCulture)}% of {b.Count}")
				.ToList();

			var titleSize = context.MeasureString(title, font);
			var rowHeight = titleSize.Height;
			var columns = Header(string.Empty).Length;
			var widths = new int[columns];

			foreach (var (cells, _, _) in rows)
			{
				for (var i = 0; i < columns; i++)
					widths[i] = Math.Max(widths[i], context.MeasureString(cells[i], font).Width);
			}

			var tableWidth = rows.Count > 0 ? widths.Sum() + gap * (columns - 1) : 0;
			var oddsWidth = odds.Count > 0 ? odds.Max(o => context.MeasureString(o, font).Width) : 0;
			var width = Math.Max(titleSize.Width, Math.Max(tableWidth, oddsWidth)) + pad * 2;
			var height = pad * 2 + rowHeight * (1 + rows.Count + (odds.Count > 0 ? odds.Count + 1 : 0)) + 8;

			var region = ChartInfo.PriceChartContainer.Region;
			var left = StatsPanelLocation == PanelCorner.TopLeft || StatsPanelLocation == PanelCorner.BottomLeft;
			var top = StatsPanelLocation == PanelCorner.TopLeft || StatsPanelLocation == PanelCorner.TopRight;
			var x = left ? panel.X : panel.Right - width;
			var y = top ? panel.Bottom + 6 : panel.Y - 6 - height;
			x = Math.Max(region.X, Math.Min(x, region.X + region.Width - width));
			y = Math.Max(region.Y, Math.Min(y, region.Y + region.Height - height));

			context.FillRectangle(CardColor, new Rectangle(x, y, width, height), 6);

			var lineY = y + pad;
			context.DrawString(title, font, TextColor, x + pad, lineY);
			lineY += rowHeight + 4;

			foreach (var (cells, header, average) in rows)
			{
				if (header)
					context.DrawLine(new RenderPen(CardBorderColor), x + pad, lineY - 2, x + width - pad, lineY - 2);

				var cellX = x + pad;

				for (var i = 0; i < columns; i++)
				{
					var cellWidth = context.MeasureString(cells[i], font).Width;
					var textX = i == 0 ? cellX : cellX + widths[i] - cellWidth;
					var color = header ? DimTextColor
						: i == columns - 1 ? (average > 0 ? BullColor : average < 0 ? BearColor : TextColor)
						: TextColor;

					context.DrawString(cells[i], font, color, textX, lineY);
					cellX += widths[i] + gap;
				}

				lineY += rowHeight;
			}

			if (odds.Count == 0)
				return;

			lineY += 4;
			context.DrawLine(new RenderPen(CardBorderColor), x + pad, lineY - 2, x + width - pad, lineY - 2);
			context.DrawString("Odds check (were the TP odds right?)", font, DimTextColor, x + pad, lineY);
			lineY += rowHeight;

			foreach (var line in odds)
			{
				context.DrawString(line, font, TextColor, x + pad, lineY);
				lineY += rowHeight;
			}
		}

		// hover details for a signal: what the probabilities were built from and how the trade ended
		private List<(string Text, Color Color)> SignalTooltip(SignalTrade trade)
		{
			var estimate = trade.Estimate;
			var percents = LabelPercents(trade.HasBreakEven, estimate.Odds);
			var side = trade.IsLong ? "buys" : "shorts";
			var time = trade.EntryTime.Add(ChartTimeZoneOffset).ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
			var expected = estimate.ExpectedTicks.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

			var odds = trade.HasBreakEven
				? $"P(TP) {percents[0]}%   P(BE) {percents[1]}%   P(SL) {percents[2]}%   EV {expected}t"
				: $"P(TP) {percents[0]}%   P(SL) {percents[1]}%   EV {expected}t";

			var lines = new List<(string Text, Color Color)>
			{
				($"{Side(trade)} @ {FormatPrice(trade.EntryPrice)}   {time}", trade.IsLong ? BuyColor : ShortColor),
				($"TP {FormatPrice(trade.TakeProfitPrice)} (+{trade.TargetTicks}t)   SL {FormatPrice(trade.StopLossPrice)} (-{trade.RiskTicks}t)", TextColor)
			};

			if (trade.HasBreakEven)
			{
				lines.Add(($"Break-even: at +{trade.TriggerTicks}t the stop moves to {FormatPrice(trade.BreakEvenPrice)} (+{trade.LockedTicks}t)",
					TextColor));
			}

			if (trade.LimitEntry)
			{
				var state = trade.Pending ? $"waiting until bar {trade.ExpiryBar}"
					: trade.FillBar >= 0 ? $"filled on bar {trade.FillBar}"
					: $"not filled by bar {trade.ExitBar}";
				lines.Add(($"Limit entry {PullbackPercent}% of the signal bar back from its {FormatPrice(trade.SignalClose)} close: {state}", DimTextColor));
			}

			lines.Add((odds, TextColor));
			// the groups the odds came from, narrowest first
			var names = OddsGroupNames(trade, side);

			for (var i = estimate.Levels.Length - 1; i >= 0; i--)
			{
				var name = i == estimate.Levels.Length - 1 && i >= 2 ? "This setup" : names[i];
				lines.Add(($"{name}: {TallyText(estimate.Levels[i], trade.HasBreakEven)}", DimTextColor));
			}
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

			if (trade.SweptLevel != null)
			{
				lines.Add(($"Swept the {LevelName(trade.SweptLevel)} {FormatPrice(trade.SweptLevel.Price)} on bar {trade.SweptLevel.EndBar}",
					DimTextColor));
			}

			if (trade.FillPrice > 0)
			{
				var who = trade.FillBidsFilled ? "bids" : "offers";
				lines.Add(($"Reacted to {FormatVolume(trade.FillVolume)} filled @ {FormatPrice(trade.FillPrice)} (resting {who})", DimTextColor));
			}

			if (trade.BreakEvenActive)
				lines.Add(($"Stop moved to +{trade.LockedTicks}t on bar {trade.BreakEvenBar}", BreakEvenPen.Color.Convert()));

			lines.Add((ResultText(trade), OutcomeColor(trade.Outcome)));
			return lines;
		}

		// "All buys", "All FVG buys", "FVG buys in the first 30 minutes", "... in busy markets", ...
		private List<string> OddsGroupNames(SignalTrade trade, string side)
		{
			var trigger = TriggerLabel(trade.Trigger);
			var names = new List<string> { $"All {side}", $"All {trigger} {side}" };
			var name = $"{trigger} {side}";

			void Split(bool on, string words)
			{
				if (!on)
					return;

				name += words;
				names.Add(name);
			}

			var times = new[] { "in the first 30 minutes", "in the morning", "at midday", "in the afternoon", "outside regular hours" };
			var markets = new[] { "quiet", "normal", "busy" };
			Split(OddsByTime, $" {times[trade.TimeBucket]}");
			Split(OddsByVolatility, $", {markets[trade.VolatilityBucket]} markets");
			Split(OddsByTrend, trade.WithTrend ? ", with the trend" : ", against it");
			Split(OddsByConfirmations, $", {trade.Confirmations} confirmation{(trade.Confirmations == 1 ? string.Empty : "s")}");
			return names;
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

		private List<(string Text, Color Color)> KeyLevelTooltip(KeyLevel level)
		{
			var name = LevelName(level);
			var lines = new List<(string Text, Color Color)>
			{
				($"{char.ToUpperInvariant(name[0])}{name.Substring(1)} {FormatPrice(level.Price)}", LevelColor)
			};

			if (level.FirstSwingBar >= 0)
				lines.Add(($"Swings on bars {level.FirstSwingBar} and {level.SecondSwingBar}", DimTextColor));

			switch (level.State)
			{
				case KeyLevelState.Swept:
					lines.Add(($"Swept on bar {level.EndBar}: a wick beyond it, a close back inside", level.IsHigh ? BearColor : BullColor));
					break;

				case KeyLevelState.Broken:
					lines.Add(($"Broken on bar {level.EndBar}: a close beyond it", DimTextColor));
					break;

				case KeyLevelState.Expired:
					lines.Add(($"Counted until bar {level.EndBar}, untouched", DimTextColor));
					break;

				default:
					lines.Add(($"Waiting since bar {level.FromBar}: price has not traded beyond it", DimTextColor));
					break;
			}

			return lines;
		}

		private List<(string Text, Color Color)> OrderTooltip(RestingOrder order)
		{
			var kind = order.IsBid ? "bid" : "offer";
			var time = order.FirstTime.Add(ChartTimeZoneOffset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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

		// classic ATAS's Indicator has a TickSize of its own; this one falls back to 0.01 when the
		// chart has no instrument yet
#pragma warning disable CS0108
		private decimal TickSize => InstrumentInfo != null && InstrumentInfo.TickSize > 0 ? InstrumentInfo.TickSize : 0.01m;
#pragma warning restore CS0108

		// the chart's time zone: a TimeSpan, or whole hours (TimeZone) on older ATAS versions - the
		// build reads which one the installed ATAS has
#if ATAS_TIMEZONE_HOURS
		private TimeSpan ChartTimeZoneOffset => TimeSpan.FromHours(InstrumentInfo.TimeZone);
#else
		private TimeSpan ChartTimeZoneOffset => InstrumentInfo.TimeZoneOffset;
#endif

		// the stop only moves if the trigger sits between the entry and the take profit
		private bool BreakEvenEnabled => BreakEvenTriggerTicks > 0 && BreakEvenTriggerTicks < TakeProfitTicks;

		// the break-even stop has to stay below its trigger
		private int EffectiveBreakEvenStopTicks => Math.Min(BreakEvenStopTicks, Math.Max(0, BreakEvenTriggerTicks - 1));

		// whether trades get a break-even stop with the bracket in use
		private bool BracketHasBreakEven => Bracket == BracketRule.FixedTicks ? BreakEvenEnabled : BreakEvenTriggerPercent > 0;

		// "TP 80t · SL 80t · BE +40t → +20t", "TP 3× ATR · SL 3× ATR · BE at 50% → 25% of TP"
		private string BracketText()
		{
			string Times(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
			var share = BreakEvenTriggerPercent > 0
				? $" · BE at {BreakEvenTriggerPercent}% → {Math.Min(BreakEvenStopPercent, BreakEvenTriggerPercent - 1)}% of TP"
				: string.Empty;

			switch (Bracket)
			{
				case BracketRule.AtrMultiple:
					return $"TP {Times(TakeProfitAtr)}× ATR · SL {Times(StopLossAtr)}× ATR{share}";

				case BracketRule.BeyondSignalBar:
					return $"SL beyond the signal bar +{StopBufferTicks}t · TP {Times(RewardRisk)}× the stop{share}";

				default:
					return $"TP {TakeProfitTicks}t · SL {StopLossTicks}t" + (BreakEvenEnabled ? $" · BE +{BreakEvenTriggerTicks}t → +{EffectiveBreakEvenStopTicks}t" : string.Empty);
			}
		}

		// Odds of a driftless random walk - no edge at all - which every estimate starts from.
		// Without break-even, P(TP) = SL / (TP + SL). With it, price first has to reach the
		// trigger before the stop, P = SL / (trigger + SL), and from there the TP before the
		// break-even stop, P = (trigger - stop) / (TP - stop). For 80 / 80 with the stop
		// moving to +20 at +40 that is TP 2/9, BE 4/9, SL 1/3: an expected 0 ticks.
		private static OutcomeOdds PriorOdds(double tp, double sl, double trigger, double stop, bool breakEven)
		{
			if (!breakEven)
				return new OutcomeOdds(sl / (tp + sl), 0, tp / (tp + sl));

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
			double tp = trade.TargetTicks;
			double sl = trade.RiskTicks;

			if (!trade.HasBreakEven)
			{
				var p = LiveProbability(odds.TakeProfit, tp, sl, excursion);
				return new OutcomeOdds(p, 0, 1 - p);
			}

			double trigger = trade.TriggerTicks;
			double stop = trade.LockedTicks;
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

				case TradeOutcome.Missed:
					return $"No fill: price did not trade through the limit by bar {trade.ExitBar}";

				default:
					return trade.Pending ? $"Waiting for the limit to fill, until bar {trade.ExpiryBar}"
						: trade.BreakEvenActive ? "Open - stop at break-even, waiting for TP or BE" : "Open - waiting for TP or SL";
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
				case TradeOutcome.Missed:
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

				case TradeOutcome.Missed:
					return "NO FILL";

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

				case TriggerType.KeySweep:
					return "Key sweep";

				case TriggerType.KeySweepThenFvg:
					return "Key sweep+FVG";

				default:
					return "FVG";
			}
		}

		private static bool IsZoneTrigger(TriggerType trigger)
		{
			return trigger == TriggerType.Fvg || trigger == TriggerType.SweepThenFvg || trigger == TriggerType.KeySweepThenFvg;
		}

		// the trigger, naming the key level a key-level sweep took: "Sweep PDH", "ONL sweep+FVG"
		private static string SetupName(SignalTrade trade)
		{
			if (trade.SweptLevel == null)
				return TriggerLabel(trade.Trigger);

			return trade.Trigger == TriggerType.KeySweep ? $"Sweep {LevelTag(trade.SweptLevel)}" : $"{LevelTag(trade.SweptLevel)} sweep+FVG";
		}

		private static string LevelTag(KeyLevel level)
		{
			switch (level.Kind)
			{
				case KeyLevelKind.PriorDayHigh:
					return "PDH";

				case KeyLevelKind.PriorDayLow:
					return "PDL";

				case KeyLevelKind.OvernightHigh:
					return "ONH";

				case KeyLevelKind.OvernightLow:
					return "ONL";

				case KeyLevelKind.OpeningRangeHigh:
					return "ORH";

				case KeyLevelKind.OpeningRangeLow:
					return "ORL";

				case KeyLevelKind.EqualHighs:
					return "EQH";

				default:
					return "EQL";
			}
		}

		private static string LevelName(KeyLevel level)
		{
			switch (level.Kind)
			{
				case KeyLevelKind.PriorDayHigh:
					return "prior day high";

				case KeyLevelKind.PriorDayLow:
					return "prior day low";

				case KeyLevelKind.OvernightHigh:
					return "overnight high";

				case KeyLevelKind.OvernightLow:
					return "overnight low";

				case KeyLevelKind.OpeningRangeHigh:
					return "opening range high";

				case KeyLevelKind.OpeningRangeLow:
					return "opening range low";

				case KeyLevelKind.EqualHighs:
					return "equal highs";

				default:
					return "equal lows";
			}
		}

		private static string Side(SignalTrade trade)
		{
			return trade.IsLong ? "BUY" : "SHORT";
		}

		// "FVG · Hammer", "Fill · Bullish engulfing +1", "Sweep PDH"
		private static string SetupText(SignalTrade trade)
		{
			var pattern = PatternSummary(trade.CandlePatterns);
			return pattern.Length == 0 ? SetupName(trade) : $"{SetupName(trade)} · {pattern}";
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

		// "regular hours 09:30–16:00", "all hours"
		private string HoursText()
		{
			string Clock(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

			switch (SignalHours)
			{
				case SignalHoursRule.AllHours:
					return "all hours";

				case SignalHoursRule.FirstTwoHours:
					var twoHours = RegularHoursStart + TimeSpan.FromHours(2);
					return $"first two hours {Clock(RegularHoursStart)}–{Clock(twoHours < RegularHoursEnd ? twoHours : RegularHoursEnd)}";

				case SignalHoursRule.RegularHoursNoLunch:
					return $"regular hours {Clock(RegularHoursStart)}–{Clock(RegularHoursEnd)}, not 11:30–13:30";

				case SignalHoursRule.Custom:
					return CustomHoursStart == CustomHoursEnd ? "custom window, all day" : $"custom {Clock(CustomHoursStart)}–{Clock(CustomHoursEnd)}";

				default:
					return $"regular hours {Clock(RegularHoursStart)}–{Clock(RegularHoursEnd)}";
			}
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

// Checks for FvgReactionLiquiditySweep, run against the ATAS API stubs:
//   dotnet run -c Release --project tests/IndicatorTests
// Exit code 0 = all checks passed.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;

using ATAS.Indicators;
using ATAS.Indicators.Technical;

using OFT.Rendering.Context;

internal static class Program
{
	private const decimal Tick = 0.25m;
	private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
	private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

	private static readonly Type IndicatorType = typeof(FvgReactionLiquiditySweep);
	private static readonly List<string> Failures = new List<string>();
	private static int _checks;

	public static int Main()
	{
		Run("Bar settlement: TP / SL and the same-bar rules", SettleWithoutBreakEven);
		Run("Bar settlement: break-even trigger and stop", SettleWithBreakEven);
		Run("Hit probability: closed forms", HitProbabilityClosedForms);
		Run("Hit probability: Monte Carlo with drift", HitProbabilityMonteCarlo);
		Run("Probability model: three-outcome shrinkage", ModelShrinkage);
		Run("Prior odds and label percentages", PriorOddsAndPercentages);
		Run("Live odds of an open trade", LiveOddsOfOpenTrade);
		Run("Candlestick patterns: shapes and near misses, both ways", CandlePatternShapes);
		Run("Candlestick patterns: hammer shapes read by what came before", CandlePatternContext);
		Run("FVG: the completing candle is not a retest", FvgCompletingCandleIsNotReaction);
		Run("FVG: hammer at the retest -> bullish reaction, BUY, TP 80 ticks later", FvgRetestBuyHitsTakeProfit);
		Run("FVG: bearish engulfing at a bullish gap -> bearish reaction, SHORT", FvgBearishReactionAtBullishGap);
		Run("FVG: a reaction needs a pattern that includes the touch", FvgReactionNeedsPatternAtZone);
		Run("FVG: used, filled and expired gaps stop being watched", FvgZoneLifecycle);
		Run("Sweep of highs: SHORT, same-bar rules", SweepShortSameBarRule);
		Run("Sweep: a shooting star on the sweep bar confirms it", SweepPatternConfirmation);
		Run("Break-even: stop moves at +40t, exits at +20t", BreakEvenStopAfterTrigger);
		Run("Live ticks: signal only after the bar closes", LiveSignalAppearsAfterClose);
		Run("Live ticks: a dip before the trigger is not a break-even exit", LiveBreakEvenFollowsTheTicks);
		Run("Fills: footprint fill, its reaction and the order-flow confirmation", FootprintFillConfirmation);
		Run("Fills: a reaction after a big fill is a signal", FillReactionSignal);
		Run("Order book: resting orders, filled vs pulled", OrderBookRestingOrders);
		Run("Order book: fills merge with the footprint and get a reaction", OrderBookFillReaction);
		Run("Order book: a fill on the other side of the footprint stays apart", OrderBookFillSides);
		Run("Defaults of the newer features", DefaultSettings);
		Run("New York time, trading days and signal hours", NewYorkTimeAndHours);
		Run("Key levels: overnight, opening range and prior day, swept and broken", KeyLevelDaysScenario);
		Run("Key levels: equal highs, their near misses and expiry", EqualHighsScenario);
		Run("Adaptive sizes: big fills among the biggest of recent bars", AdaptiveFills);
		Run("Adaptive sizes: resting orders against the typical level", AdaptiveRestingOrders);
		Run("Scoreboard: results per setup and pattern, and the odds check", ScoreboardCounts);
		Run("Fuzz: historical run vs oracle", () => FuzzHistorical(seed: 7, configure: null));
		Run("Fuzz: expiry + session end + cooldown 0", () => FuzzHistorical(seed: 11, configure: i =>
		{
			i.MaxBarsInTrade = 25;
			i.ExpireAtSessionEnd = true;
			i.SignalCooldownBars = 0;
			i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection;
		}));
		Run("Fuzz: all trades at once, confluence mode", () => FuzzHistorical(seed: 23, configure: i =>
		{
			i.OneTradeAtATime = false;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.SweepThenFvg;
		}));
		Run("Fuzz: no break-even, worst case, gaps filled by a close", () => FuzzHistorical(seed: 29, configure: i =>
		{
			i.BreakEvenTriggerTicks = 0;
			i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
			i.FvgFill = FvgReactionLiquiditySweep.FvgFillRule.CloseBeyond;
		}));
		Run("Fuzz: tight bracket, reactions may close inside, gaps filled at 50%", () => FuzzHistorical(seed: 37, configure: i =>
		{
			i.TakeProfitTicks = 40;
			i.StopLossTicks = 120;
			i.BreakEvenTriggerTicks = 20;
			i.BreakEvenStopTicks = 10;
			i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
			i.RequireCloseThroughZone = false;
			i.FvgFill = FvgReactionLiquiditySweep.FvgFillRule.Middle;
		}));
		Run("Fuzz: break-even at entry, fill reactions only", () => FuzzHistorical(seed: 31, configure: i =>
		{
			i.TakeProfitTicks = 120;
			i.StopLossTicks = 60;
			i.BreakEvenTriggerTicks = 30;
			i.BreakEvenStopTicks = 0;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FillReactionOnly;
			i.ReactionBars = 5;
		}));
		Run("Fuzz: sweeps need a pattern and order flow, other shapes", () => FuzzHistorical(seed: 43, configure: i =>
		{
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.LiquiditySweepOnly;
			i.RequireCandlePattern = true;
			i.PinBarWickRatio = 1.5;
			i.PatternAverageBars = 5;
			i.TweezerToleranceTicks = 0;
			i.PatternHarami = false;
			i.PatternMarubozu = false;
			i.FillMinVolume = 250;
			i.FillVolumeMultiplier = 2;
		}));
		Run("Fuzz: the defaults - key levels, regular hours, adaptive fills", () => FuzzHistorical(seed: 53, configure: Defaults));
		Run("Fuzz: key levels around the clock, sweeps only, other sizes", () => FuzzHistorical(seed: 59, configure: i =>
		{
			Defaults(i);
			i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.AllHours;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.LiquiditySweepOnly;
			i.EqualToleranceTicks = 1;
			i.EqualLookbackBars = 60;
			i.OpeningRangeMinutes = 15;
			i.FillTopPercent = 20;
			i.FillLookbackBars = 50;
		}));
		Run("Fuzz: key-level sweeps only, around the clock", () => FuzzHistorical(seed: 61, configure: i =>
		{
			Defaults(i);
			i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.AllHours;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.KeyLevelSweeps;
			i.OneTradeAtATime = false;
			i.SignalCooldownBars = 0;
			i.EqualToleranceTicks = 8;
			i.EqualLookbackBars = 400;
		}, rich: false));
		Run("Fuzz: the first two hours of regular hours", () => FuzzHistorical(seed: 71, configure: i =>
		{
			Defaults(i);
			i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.FirstTwoHours;
			i.OneTradeAtATime = false;
			i.SignalCooldownBars = 1;
		}));
		Run("Fuzz: no lunch, confluence mode, other session times", () => FuzzHistorical(seed: 67, configure: i =>
		{
			Defaults(i);
			i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.RegularHoursNoLunch;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.SweepThenFvg;
			i.RegularHoursStart = new TimeSpan(8, 30, 0);
			i.RegularHoursEnd = new TimeSpan(15, 15, 0);
			i.OneTradeAtATime = false;
			i.LevelOvernight = false;
		}));
		Run("Fuzz: filters hide signals, the model still learns", FuzzFilters);
		Run("Fuzz: live ticks and a live order book match history", () => FuzzLiveMatchesHistory());
		Run("Fuzz: live matches history with key levels", () => FuzzLiveMatchesHistory(i =>
		{
			Defaults(i);
			i.FillSize = FvgReactionLiquiditySweep.FillSizeRule.FixedContracts;
			i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.AllHours;
			i.EqualToleranceTicks = 4;
		}, seed: 83));
		Run("Recalculate is deterministic", RecalculateIsDeterministic);
		Run("Render: zones, fills, orders, sweeps, labels, panel, tooltips", RenderSmoke);

		Console.WriteLine();
		Console.WriteLine($"{_checks} checks, {Failures.Count} failed");

		foreach (var failure in Failures.Take(40))
			Console.WriteLine("FAIL: " + failure);

		return Failures.Count == 0 ? 0 : 1;
	}

	#region Unit checks

	private static void SettleWithoutBreakEven()
	{
		var worst = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
		var byCandle = FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection;
		var nearest = FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst;
		var longTrade = MakeTrade(true, breakEven: false);
		var shortTrade = MakeTrade(false, breakEven: false);

		// long: entry 100, TP 120, SL 80
		Expect(Settle(longTrade, 100, 119, 81, 110, worst), "Open", false, "long untouched");
		Expect(Settle(longTrade, 100, 120, 90, 118, worst), "TakeProfit", false, "long TP touched exactly");
		Expect(Settle(longTrade, 100, 110, 80, 90, worst), "StopLoss", false, "long SL touched exactly");
		Expect(Settle(longTrade, 100, 121, 79, 110, worst), "StopLoss", true, "long both, worst case");
		Expect(Settle(longTrade, 100, 121, 79, 110, byCandle), "StopLoss", true, "long both, bullish bar -> low first");
		Expect(Settle(longTrade, 100, 121, 79, 90, byCandle), "TakeProfit", true, "long both, bearish bar -> high first");
		Expect(Settle(longTrade, 100, 121, 79, 110, nearest), "StopLoss", true, "long both, equal distance -> worst case");
		Expect(Settle(longTrade, 100, 121, 80, 110, nearest), "StopLoss", true, "long both, low nearer the open");
		Expect(Settle(longTrade, 100, 120, 79, 110, nearest), "TakeProfit", true, "long both, high nearer the open");
		Expect(Settle(longTrade, 125, 126, 70, 75, worst), "TakeProfit", false, "long gaps over TP at the open");
		Expect(Settle(longTrade, 75, 130, 70, 128, worst), "StopLoss", false, "long gaps under SL at the open");

		// short: entry 100, TP 80, SL 120
		Expect(Settle(shortTrade, 100, 119, 80, 90, worst), "TakeProfit", false, "short TP");
		Expect(Settle(shortTrade, 100, 120, 81, 110, worst), "StopLoss", false, "short SL");
		Expect(Settle(shortTrade, 100, 121, 79, 110, byCandle), "TakeProfit", true, "short both, bullish bar -> low first");
		Expect(Settle(shortTrade, 100, 121, 79, 90, byCandle), "StopLoss", true, "short both, bearish bar -> high first");
		Expect(Settle(shortTrade, 100, 121, 79, 90, worst), "StopLoss", true, "short both, worst case");
		Expect(Settle(shortTrade, 100, 121, 79.5m, 110, nearest), "TakeProfit", true, "short both, low nearer the open");
	}

	private static void SettleWithBreakEven()
	{
		var worst = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
		var byCandle = FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection;
		var nearest = FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst;

		// long: entry 100, TP 120, SL 80, trigger 110 moves the stop to 105
		var t = MakeTrade(true, breakEven: true);
		ExpectWalk(Walk(t, 100, 111, 104), "BreakEven", true, 105, "trigger, then back through the break-even stop");
		ExpectWalk(Walk(t, 100, 110, 105), "BreakEven", true, 105, "trigger and stop touched exactly");
		ExpectWalk(Walk(t, 100, 109, 81), "Open", false, "", "no trigger, SL not reached");
		ExpectWalk(Walk(t, 100, 109, 80), "StopLoss", false, 80, "no trigger, full stop");
		ExpectWalk(Walk(t, 100, 111, 121), "TakeProfit", true, 120, "trigger, then TP");
		ExpectWalk(Walk(t, 112, 104), "BreakEven", true, 105, "gap over the trigger at the open");
		ExpectWalk(Walk(t, 100, 104, 109, 104.5m), "Open", false, "", "dip before the trigger is harmless");

		var active = MakeTrade(true, breakEven: true, active: true);
		ExpectWalk(Walk(active, 108, 104), "BreakEven", true, 105, "stop already at break-even");
		ExpectWalk(Walk(active, 108, 120), "TakeProfit", true, 120, "already at break-even, then TP");

		// first bar after entry: dips a tick, runs through the trigger, closes near the high.
		// Only the worst case assumes the dip came after the high.
		ExpectBar(Settle(t, 100, 112, 99.75m, 111, worst), "BreakEven", true, true, "worst case -> stopped at break-even");
		ExpectBar(Settle(t, 100, 112, 99.75m, 111, byCandle), "Open", true, true, "bullish bar -> low first -> still open");
		ExpectBar(Settle(t, 100, 112, 99.75m, 111, nearest), "Open", true, true, "low nearer the open -> still open");

		// opens at its low: only the worst case lets price come back to the open after the trigger
		ExpectBar(Settle(t, 100, 112, 100, 111, worst), "BreakEven", true, true, "worst case: open revisited after the trigger");
		ExpectBar(Settle(t, 100, 112, 100, 111, nearest), "Open", true, true, "nearer extreme: the low was the open");
		ExpectBar(Settle(t, 100, 112, 100, 111, byCandle), "Open", true, true, "bullish bar: the low was the open");

		// falls back through the break-even stop after the trigger in either order
		ExpectBar(Settle(t, 106, 112, 104, 104.5m, worst), "BreakEven", true, false, "both orders hit break-even");

		// short: entry 100, TP 80, SL 120, trigger 90 moves the stop to 95
		var s = MakeTrade(false, breakEven: true);
		ExpectWalk(Walk(s, 100, 89, 96), "BreakEven", true, 95, "short: trigger, then back through the stop");
		ExpectWalk(Walk(s, 100, 91, 119), "Open", false, "", "short: no trigger, SL not reached");
		ExpectWalk(Walk(s, 100, 89, 80), "TakeProfit", true, 80, "short: trigger, then TP");
	}

	private static void HitProbabilityClosedForms()
	{
		// driftless walk: P = (x + SL) / (TP + SL)
		CheckClose(Hit(0, 0, 80, 80), 0.5, 1e-12, "driftless symmetric");
		CheckClose(Hit(0, 40, 80, 80), 0.75, 1e-12, "driftless +40t");
		CheckClose(Hit(0, 0, 160, 80), 1.0 / 3, 1e-12, "driftless 2:1 bracket");

		// symmetric bracket with drift: P = u / (1 + u), u = exp(theta * D)
		var theta = Math.Log(0.65 / 0.35) / 80;
		CheckClose(Hit(theta, 0, 80, 80), 0.65, 1e-9, "symmetric with drift");

		// LiveProbability reproduces the entry probability at the entry price
		foreach (var p in new[] { 0.05, 0.3, 0.5, 0.62, 0.9, 0.97 })
		{
			CheckClose(Live(p, 80, 80, 0), p, 1e-6, $"live at entry, p={p}");
			CheckClose(Live(p, 200, 40, 0), p, 1e-6, $"live at entry, 200/40 bracket, p={p}");
		}

		CheckClose(Live(0.5, 80, 80, 40), 0.75, 1e-6, "live driftless +40t");
		Check(Live(0.6, 80, 80, 80) == 1 && Live(0.6, 80, 80, -80) == 0, "live at the levels");

		var last = -1.0;
		for (var x = -79; x <= 79; x += 3)
		{
			var p = Live(0.62, 80, 80, x);
			Check(p > last && p > 0 && p < 1, $"live probability not increasing at {x}");
			last = p;
		}

		foreach (var p in new[] { 0.0, 1.0, 0.0005, 0.9995 })
		{
			var v = Live(p, 1000, 1, 0);
			Check(!double.IsNaN(v) && v >= 0 && v <= 1, $"live probability out of range for extreme bracket, p={p}: {v}");
		}
	}

	private static void HitProbabilityMonteCarlo()
	{
		// +-1 tick steps with P(up) = q: the exact discrete answer is gambler's ruin,
		// and the continuous formula with theta = ln(q / (1 - q)) matches it.
		var rng = new Random(3);
		const int tp = 30;
		const int sl = 20;

		foreach (var q in new[] { 0.5, 0.53, 0.47 })
		{
			var wins = 0;
			const int runs = 40000;

			for (var r = 0; r < runs; r++)
			{
				var x = 0;

				while (x < tp && x > -sl)
					x += rng.NextDouble() < q ? 1 : -1;

				if (x >= tp)
					wins++;
			}

			var theta = Math.Log(q / (1 - q));
			var expected = Hit(theta, 0, tp, sl);
			var observed = (double)wins / runs;
			CheckClose(observed, expected, 0.012, $"Monte Carlo q={q}");
		}
	}

	private static void ModelShrinkage()
	{
		var modelType = IndicatorType.GetNestedType("ProbabilityModel", BindingFlags.NonPublic);
		var triggerType = IndicatorType.GetNestedType("TriggerType", BindingFlags.NonPublic);
		var outcomeType = IndicatorType.GetNestedType("TradeOutcome", BindingFlags.NonPublic);
		var model = Activator.CreateInstance(modelType);
		var record = modelType.GetMethod("Record");
		var estimate = modelType.GetMethod("Estimate");
		var fvg = Enum.Parse(triggerType, "Fvg");
		var sweep = Enum.Parse(triggerType, "Sweep");
		var prior = (2.0 / 9, 4.0 / 9, 1.0 / 3);

		(double Tp, double Be, double Sl, double Ev) Estimate(bool isLong, object trigger, int confirmations)
		{
			var e = estimate.Invoke(model, new[] { isLong, trigger, confirmations, Odds(prior.Item1, prior.Item2, prior.Item3), 10.0, 80.0, 20.0, 80.0 });
			double P(string name) => (double)e.GetType().GetProperty(name).GetValue(e);
			return (P("TakeProfit"), P("BreakEven"), P("StopLoss"), P("ExpectedTicks"));
		}

		void Record(bool isLong, object trigger, int confirmations, string outcome)
		{
			record.Invoke(model, new[] { isLong, trigger, confirmations, Enum.Parse(outcomeType, outcome) });
		}

		var none = Estimate(true, fvg, 1);
		CheckClose(none.Tp, prior.Item1, 1e-12, "no history = prior TP");
		CheckClose(none.Be, prior.Item2, 1e-12, "no history = prior BE");
		CheckClose(none.Ev, 0, 1e-12, "no edge = 0 expected ticks");

		Record(true, fvg, 1, "TakeProfit");

		// each level: (count + 10 * parent) / (n + 10)
		(double, double, double) Level((double Tp, double Be, double Sl) parent, int tp, int be, int sl)
		{
			double n = tp + be + sl + 10;
			return ((tp + 10 * parent.Tp) / n, (be + 10 * parent.Be) / n, (sl + 10 * parent.Sl) / n);
		}

		var direction = Level(prior, 1, 0, 0);
		var trigger = Level(direction, 1, 0, 0);
		var setup = Level(trigger, 1, 0, 0);
		var one = Estimate(true, fvg, 1);
		CheckClose(one.Tp, setup.Item1, 1e-12, "one TP, same setup: TP");
		CheckClose(one.Be, setup.Item2, 1e-12, "one TP, same setup: BE");
		CheckClose(one.Sl, setup.Item3, 1e-12, "one TP, same setup: SL");
		CheckClose(one.Tp + one.Be + one.Sl, 1, 1e-12, "odds add up to 1");
		CheckClose(one.Ev, one.Tp * 80 + one.Be * 20 - one.Sl * 80, 1e-12, "expected ticks");
		CheckClose(Estimate(true, fvg, 2).Tp, trigger.Item1, 1e-12, "one TP, same trigger, other confirmations");
		CheckClose(Estimate(true, fvg, 4).Tp, trigger.Item1, 1e-12, "all four confirmations is a setup of its own");
		CheckClose(Estimate(true, sweep, 1).Tp, direction.Item1, 1e-12, "one TP, other trigger");
		CheckClose(Estimate(true, Enum.Parse(triggerType, "Fill"), 1).Tp, direction.Item1, 1e-12, "one TP, the fill trigger");
		CheckClose(Estimate(false, fvg, 1).Tp, prior.Item1, 1e-12, "shorts are unaffected by longs");

		Record(true, fvg, 1, "BreakEven");
		Check(Estimate(true, fvg, 1).Be > setup.Item2, "a break-even exit raises the BE share");

		for (var i = 0; i < 40; i++)
			Record(true, sweep, 0, "StopLoss");

		var losing = Estimate(true, sweep, 0);
		Check(losing.Sl > 0.8 && losing.Ev < -40, "40 full stops -> mostly SL, clearly negative EV");
		Check(Estimate(true, fvg, 1).Ev > losing.Ev, "winning setup stays above the losing one");
	}

	private static void PriorOddsAndPercentages()
	{
		(double Tp, double Be, double Sl) Prior(Action<FvgReactionLiquiditySweep> configure)
		{
			var ind = NewIndicator(configure);
			var odds = IndicatorType.GetMethod("PriorOdds", Private).Invoke(ind, null);
			double P(string name) => (double)odds.GetType().GetProperty(name).GetValue(odds);
			return (P("TakeProfit"), P("BreakEven"), P("StopLoss"));
		}

		var defaults = Prior(null);
		CheckClose(defaults.Tp, 2.0 / 9, 1e-12, "80/80 with +40 -> +20: TP 2/9");
		CheckClose(defaults.Be, 4.0 / 9, 1e-12, "80/80 with +40 -> +20: BE 4/9");
		CheckClose(defaults.Sl, 1.0 / 3, 1e-12, "80/80 with +40 -> +20: SL 1/3");

		var off = Prior(i => i.BreakEvenTriggerTicks = 0);
		Check(Math.Abs(off.Tp - 0.5) < 1e-12 && off.Be == 0 && Math.Abs(off.Sl - 0.5) < 1e-12, "break-even off -> 50 / 0 / 50");

		var beyondTarget = Prior(i => i.BreakEvenTriggerTicks = 80);
		Check(beyondTarget.Be == 0, "a trigger at the TP never fires");

		// a stop at or above the trigger is kept one tick below it: (40 - 39) / (80 - 39)
		var clamped = Prior(i => i.BreakEvenStopTicks = 50);
		CheckClose(clamped.Tp, 80.0 / 120 * (1.0 / 41), 1e-12, "break-even stop kept below the trigger");

		var percentages = IndicatorType.GetMethod("Percentages", PrivateStatic);
		int[] Split(params double[] p) => (int[])percentages.Invoke(null, new object[] { p });

		Check(Split(2.0 / 9, 4.0 / 9, 1.0 / 3).SequenceEqual(new[] { 22, 45, 33 }), "2/9, 4/9, 1/3 -> 22 / 45 / 33");
		Check(Split(0.505, 0.495).SequenceEqual(new[] { 51, 49 }), "a half rounds up on the first");
		Check(Split(1.0 / 3, 1.0 / 3, 1.0 / 3).SequenceEqual(new[] { 34, 33, 33 }), "thirds -> 34 / 33 / 33");

		var rng = new Random(9);

		for (var i = 0; i < 2000; i++)
		{
			var a = rng.NextDouble();
			var b = rng.NextDouble() * (1 - a);
			var p = new[] { a, b, 1 - a - b };
			var split = Split(p);
			Check(split.Sum() == 100 && split.Select((v, k) => Math.Abs(v - p[k] * 100) < 1).All(x => x), $"split of {a:R}/{b:R} is off");
		}
	}

	private static void LiveOddsOfOpenTrade()
	{
		var ind = NewIndicator(null);
		var liveOdds = IndicatorType.GetMethod("LiveOdds", Private);

		// entry 100 with the default 80 / 80 bracket in 0.25 ticks: TP 120, SL 80, +40t = 110, +20t = 105
		var trade = MakeTrade(true, breakEven: true);
		SetEstimate(trade, 0.3, 0.4, 0.3);

		(double Tp, double Be, double Sl) At(decimal price, bool activated)
		{
			Set(trade, "BreakEvenActive", activated);
			var odds = liveOdds.Invoke(ind, new[] { trade, (object)price });
			double P(string name) => (double)odds.GetType().GetProperty(name).GetValue(odds);
			return (P("TakeProfit"), P("BreakEven"), P("StopLoss"));
		}

		var entry = At(100, false);
		Check(Math.Abs(entry.Tp - 0.3) < 1e-6 && Math.Abs(entry.Be - 0.4) < 1e-6 && Math.Abs(entry.Sl - 0.3) < 1e-6, "entry odds as labelled");

		var trigger = At(110, true);
		Check(Math.Abs(trigger.Tp - 0.3 / 0.7) < 1e-6 && trigger.Sl == 0, "at the trigger: TP share of the non-SL odds, no SL left");

		Check(At(105, true).Be == 1 && At(120, true).Tp == 1 && At(80, false).Sl == 1, "at the levels");
		Check(At(109.75m, false).Sl < 0.05, "a tick below the trigger, the full stop is almost out of reach");

		// no edge (the prior odds) means no drift: at +20t, reaching +40t before -80t is
		// (20 + 80) / (40 + 80), then TP before the break-even stop is (40 - 20) / (80 - 20)
		SetEstimate(trade, 2.0 / 9, 4.0 / 9, 1.0 / 3);
		var driftless = At(105, false);
		CheckClose(driftless.Sl, 1.0 / 6, 1e-6, "driftless SL odds at +20t");
		CheckClose(driftless.Tp, 5.0 / 6 * (1.0 / 3), 1e-6, "driftless TP odds at +20t");
		CheckClose(driftless.Be, 5.0 / 6 * (2.0 / 3), 1e-6, "driftless BE odds at +20t");
		CheckClose(At(112.5m, true).Tp, 0.5, 1e-6, "driftless, stop moved, +50t is halfway between +20t and +80t");

		var last = 2.0;

		for (var price = 80.25m; price < 110; price += 1.25m)
		{
			var sl = At(price, false).Sl;
			Check(sl < last && sl > 0 && sl < 1, $"SL odds should fall as price rises ({price})");
			last = sl;
		}
	}

	// Hand-built cases for every pattern and the near misses that fall just outside it, read
	// after a decline. Each case is checked for buys, again upside down (mirrored, read after a
	// rally) for shorts, where it must give the bearish twin, and with its pattern's switch off.
	private static void CandlePatternShapes()
	{
		var cases = new (string Name, decimal[][] Candles, string[] Expected, Action<FvgReactionLiquiditySweep> Configure)[]
		{
			("hammer", new[] { C(100, 101, 97, 100.75m) }, new[] { "Hammer" }, null),
			("hammer, upper wick over a tenth of the range", new[] { C(100, 101.5m, 97, 100.75m) }, new string[0], null),
			("hammer, lower wick 1.67x the body", new[] { C(100, 100.75m, 98.75m, 100.75m) }, new string[0], null),
			("... is a hammer with wick / body 1.5", new[] { C(100, 100.75m, 98.75m, 100.75m) }, new[] { "Hammer" }, i => i.PinBarWickRatio = 1.5),
			("dragonfly doji", new[] { C(100, 100.25m, 97, 100) }, new[] { "DragonflyDoji" }, null),
			("inverted hammer", new[] { C(100, 103, 99.75m, 100.75m) }, new[] { "InvertedHammer" }, null),
			("gravestone shape after a decline is an inverted hammer", new[] { C(100, 103, 99.75m, 100) }, new[] { "InvertedHammer" }, null),
			("marubozu", new[] { C(100, 102, 100, 102) }, new[] { "BullishMarubozu" }, null),
			("marubozu with a 1-tick wick", new[] { C(100, 102.25m, 100, 102) }, new string[0], null),
			("marubozu under the average body", new[] { C(100, 100.75m, 100, 100.75m) }, new string[0], null),
			("bullish engulfing", new[] { C(101, 101.25m, 99.25m, 100), C(100, 101.75m, 99.75m, 101.5m) }, new[] { "BullishEngulfing" }, null),
			("... that also takes out the low is an outside reversal too", new[] { C(101, 101.25m, 99.75m, 100), C(100, 101.75m, 99.25m, 101.5m) },
				new[] { "BullishEngulfing", "BullishOutsideReversal" }, null),
			("engulfing after a gap, closing right on the open", new[] { C(101, 101.25m, 99.75m, 100), C(99.5m, 101.25m, 99.25m, 101) },
				new[] { "BullishEngulfing" }, null),
			("engulfing opening above the close before", new[] { C(101, 101.25m, 99.75m, 100), C(100.25m, 101.75m, 100.25m, 101.5m) }, new string[0], null),
			("short of the open: piercing line and harami", new[] { C(101, 101.25m, 99.75m, 100), C(100, 101, 99.25m, 100.75m) },
				new[] { "BullishHarami", "PiercingLine" }, null),
			("engulfing body no bigger", new[] { C(101, 101.25m, 99.75m, 100), C(100, 101.25m, 99.25m, 101) }, new string[0], null),
			("engulfing under the average body", new[] { C(100.25m, 100.5m, 99.75m, 100), C(100, 100.75m, 99.25m, 100.5m) }, new string[0], null),
			("piercing line", new[] { C(102, 102.25m, 99.75m, 100), C(100, 101.5m, 99.25m, 101.25m) }, new[] { "PiercingLine" }, null),
			("piercing line closing on the middle", new[] { C(102, 102.25m, 99.75m, 100), C(100, 101.25m, 99.25m, 101) }, new string[0], null),
			("piercing line opening above the close before", new[] { C(102, 102.25m, 99.75m, 100), C(100.25m, 101.5m, 100.25m, 101.25m) }, new string[0], null),
			("bullish harami", new[] { C(102, 102.25m, 99.75m, 100), C(100.5m, 101.25m, 100.25m, 101) }, new[] { "BullishHarami" }, null),
			("harami body poking out above", new[] { C(102, 102.25m, 99.75m, 100), C(101.5m, 102.5m, 101.25m, 102.25m) }, new string[0], null),
			("harami body poking out below", new[] { C(102, 102.25m, 99.75m, 100), C(99.75m, 100.75m, 99.25m, 100.5m) }, new string[0], null),
			("harami body not small", new[] { C(102, 102.25m, 99.75m, 100), C(100.25m, 101.75m, 100.25m, 101.5m) }, new string[0], null),
			("tweezer bottom", new[] { C(101, 101.25m, 99.5m, 100.25m), C(100.25m, 101, 99.75m, 100.75m) }, new[] { "TweezerBottom" }, null),
			("tweezer lows 2 ticks apart", new[] { C(101, 101.25m, 99.5m, 100.25m), C(100.25m, 101, 100, 100.75m) }, new string[0], null),
			("... match within 2 ticks", new[] { C(101, 101.25m, 99.5m, 100.25m), C(100.25m, 101, 100, 100.75m) }, new[] { "TweezerBottom" },
				i => i.TweezerToleranceTicks = 2),
			("tweezer, second candle bearish", new[] { C(101, 101.25m, 99.5m, 100.25m), C(100.75m, 101, 99.75m, 100.25m) }, new string[0], null),
			("bullish outside reversal", new[] { C(101, 101.25m, 100, 100.25m), C(100.5m, 101.75m, 99.5m, 101.5m) }, new[] { "BullishOutsideReversal" }, null),
			("outside bar closing inside the range before", new[] { C(101, 101.25m, 100, 100.25m), C(100.5m, 101.75m, 99.5m, 101) }, new string[0], null),
			("morning star", new[] { C(103, 103.25m, 100.75m, 101), C(101, 101.5m, 100.5m, 101.25m), C(101.25m, 102.75m, 101, 102.5m) },
				new[] { "MorningStar" }, null),
			("morning doji star", new[] { C(103, 103.25m, 100.75m, 101), C(101, 101.5m, 100.5m, 101), C(101.25m, 102.75m, 101, 102.5m) },
				new[] { "MorningDojiStar" }, null),
			("star above the middle of the first body", new[] { C(103, 103.25m, 100.75m, 101), C(102.25m, 102.75m, 102, 102.5m), C(101.25m, 102.75m, 101, 102.5m) },
				new string[0], null),
			("star with an average body", new[] { C(103, 103.25m, 100.75m, 101), C(101, 102.25m, 100.75m, 102), C(101.75m, 103, 101.5m, 102.75m) },
				new string[0], null),
			("third candle closing on the middle", new[] { C(103, 103.25m, 100.75m, 101), C(101, 101.5m, 100.5m, 101.25m), C(101, 102.25m, 100.75m, 102) },
				new string[0], null),
			("first candle under the average body", new[] { C(102, 102.25m, 100.75m, 101.25m), C(101.25m, 101.75m, 100.75m, 101.5m), C(101.5m, 103, 101.25m, 102.75m) },
				new string[0], null),
			("three inside up", new[] { C(103, 103.25m, 100.75m, 101), C(102, 102.5m, 101.75m, 102.25m), C(102.25m, 103.5m, 102, 103.25m) },
				new[] { "ThreeInsideUp" }, null),
			("three inside up without closing above the first open", new[] { C(103, 103.25m, 100.75m, 101), C(102, 102.5m, 101.75m, 102.25m), C(102.25m, 103, 102, 102.75m) },
				new string[0], null),
			("three outside up", new[] { C(101, 101.25m, 99.75m, 100), C(100, 101.75m, 99.75m, 101.5m), C(101.5m, 102.25m, 101.25m, 102) },
				new[] { "ThreeOutsideUp" }, null),
			("three outside up without a higher close", new[] { C(101, 101.25m, 99.75m, 100), C(100, 101.75m, 99.75m, 101.5m), C(101.5m, 101.75m, 101, 101.25m) },
				new string[0], null),
			("three white soldiers", new[] { C(100, 101.5m, 99.75m, 101.25m), C(101, 102.75m, 100.75m, 102.5m), C(102.25m, 104, 102, 103.75m) },
				new[] { "ThreeWhiteSoldiers" }, null),
			("soldier opening above the body before", new[] { C(100, 101.5m, 99.75m, 101.25m), C(101.5m, 102.75m, 101.25m, 102.5m), C(102.25m, 104, 102, 103.75m) },
				new string[0], null),
			("soldier closing far from its high", new[] { C(100, 101.5m, 99.75m, 101.25m), C(101, 102.75m, 100.75m, 102.5m), C(102.25m, 104.75m, 102, 103.75m) },
				new string[0], null),
			("soldiers not closing higher", new[] { C(100, 101.5m, 99.75m, 101.25m), C(101, 102.75m, 100.75m, 102.5m), C(101.25m, 102.75m, 101, 102.5m) },
				new string[0], null),
			("bullish three-line strike (with its engulfing and tweezer)",
				new[] { C(103, 103.25m, 102.25m, 102.5m), C(102.5m, 102.75m, 101.75m, 102), C(102, 102.25m, 101.25m, 101.5m), C(101.5m, 103.5m, 101.25m, 103.25m) },
				new[] { "BullishEngulfing", "BullishThreeLineStrike", "TweezerBottom" }, null),
			("strike stopping short of the first open",
				new[] { C(103, 103.25m, 102.25m, 102.5m), C(102.5m, 102.75m, 101.75m, 102), C(102, 102.25m, 101.25m, 101.5m), C(101.5m, 103, 101.25m, 102.75m) },
				new[] { "BullishEngulfing", "TweezerBottom" }, null),
			("strike after two equal closes",
				new[] { C(103, 103.25m, 102.25m, 102.5m), C(102.75m, 103, 102.25m, 102.5m), C(102, 102.25m, 101.25m, 101.5m), C(101.5m, 103.5m, 101.25m, 103.25m) },
				new[] { "BullishEngulfing", "TweezerBottom" }, null)
		};

		// the switch that covers each pattern
		var family = new Dictionary<string, Action<FvgReactionLiquiditySweep>>
		{
			["Hammer"] = i => i.PatternHammer = false,
			["DragonflyDoji"] = i => i.PatternHammer = false,
			["InvertedHammer"] = i => i.PatternInvertedHammer = false,
			["BullishMarubozu"] = i => i.PatternMarubozu = false,
			["BullishEngulfing"] = i => i.PatternEngulfing = false,
			["PiercingLine"] = i => i.PatternPiercingLine = false,
			["BullishHarami"] = i => i.PatternHarami = false,
			["TweezerBottom"] = i => i.PatternTweezers = false,
			["BullishOutsideReversal"] = i => i.PatternOutsideReversal = false,
			["MorningStar"] = i => i.PatternStars = false,
			["MorningDojiStar"] = i => i.PatternStars = false,
			["ThreeInsideUp"] = i => i.PatternThreeInside = false,
			["ThreeOutsideUp"] = i => i.PatternThreeOutside = false,
			["ThreeWhiteSoldiers"] = i => i.PatternThreeSoldiers = false,
			["BullishThreeLineStrike"] = i => i.PatternThreeLineStrike = false
		};

		void AllOff(FvgReactionLiquiditySweep i)
		{
			foreach (var off in family.Values)
				off(i);
		}

		foreach (var (name, candles, expected, configure) in cases)
		{
			var bars = PatternBars(candles);
			var buy = PatternsAt(bars, true, true, configure);
			Check(buy.SequenceEqual(expected), $"{name}: got [{string.Join(",", buy)}]");

			// upside down it is the bearish twin, read after a rally
			var twin = expected.Select(n => BearishTwin[n]).OrderBy(n => n, StringComparer.Ordinal).ToList();
			var sell = PatternsAt(Mirror(bars, 400), false, false, configure);
			Check(sell.SequenceEqual(twin), $"{name}, mirrored: got [{string.Join(",", sell)}]");

			foreach (var pattern in expected)
			{
				var off = PatternsAt(bars, true, true, i => { configure?.Invoke(i); family[pattern](i); });
				Check(!off.Contains(pattern), $"{name}: still found with its switch off");

				var twinOff = PatternsAt(Mirror(bars, 400), false, false, i => { configure?.Invoke(i); family[pattern](i); });
				Check(!twinOff.Contains(BearishTwin[pattern]), $"{name}, mirrored: still found with its switch off");
			}

			Check(PatternsAt(bars, true, true, i => { configure?.Invoke(i); AllOff(i); }).Count == 0, $"{name}: found with every switch off");
		}

		// label order: strongest first
		var patternType = IndicatorType.GetNestedType("CandlePattern", BindingFlags.NonPublic);
		var namesOf = IndicatorType.GetMethod("PatternNames", PrivateStatic);
		List<string> Names(string flags) => (List<string>)namesOf.Invoke(null, new[] { Enum.Parse(patternType, flags) });
		Check(Names("Hammer, TweezerBottom, MorningStar").SequenceEqual(new[] { "Morning star", "Hammer", "Tweezer bottom" }), "label order");
		Check(Names("None").Count == 0, "no pattern, no names");

		// the stronger pattern decides between a bullish and a bearish one; equals cancel out
		var decide = IndicatorType.GetMethod("DecideReaction", PrivateStatic);
		int Decide(string bull, string bear) => (int)decide.Invoke(null, new[] { Enum.Parse(patternType, bull), Enum.Parse(patternType, bear) });
		Check(Decide("Hammer", "BearishEngulfing") == -1, "an engulfing beats a hammer");
		Check(Decide("MorningStar, BullishHarami", "BearishEngulfing") == 1, "a morning star beats an engulfing");
		Check(Decide("BullishEngulfing", "BearishEngulfing") == 0 && Decide("None", "None") == 0, "twins and nothing are a tie");
		Check(Decide("InvertedHammer", "None") == 1 && Decide("None", "HangingMan") == -1, "one side only");

		// the average body only counts the candles before the pattern that exist
		var average = IndicatorType.GetMethod("AverageBody", Private);
		var short1 = NewIndicator(null);
		short1.Candles.AddRange(new[] { Bar(100, 102, 99, 101), Bar(101, 103, 100, 103), Bar(103, 104, 102, 103.5m) });
		Check((decimal)average.Invoke(short1, new object[] { 2 }) == 1.5m && (decimal)average.Invoke(short1, new object[] { 0 }) == 0,
			"average body of the bars that exist");
	}

	// The hammer shapes depend on what came before; every other pattern does not.
	private static void CandlePatternContext()
	{
		var hammer = PatternBars(new[] { C(100, 101, 97, 100.75m) });
		Check(PatternsAt(hammer, true, true, null).SequenceEqual(new[] { "Hammer" }), "after a decline: hammer");
		Check(PatternsAt(hammer, false, false, null).SequenceEqual(new[] { "HangingMan" }), "after a rally: hanging man");
		Check(PatternsAt(hammer, true, false, null).Count == 0 && PatternsAt(hammer, false, true, null).Count == 0, "no hammer reading the other way");

		var star = PatternBars(new[] { C(100, 103, 99.75m, 100.75m) });
		Check(PatternsAt(star, true, true, null).SequenceEqual(new[] { "InvertedHammer" }), "after a decline: inverted hammer");
		Check(PatternsAt(star, false, false, null).SequenceEqual(new[] { "ShootingStar" }), "after a rally: shooting star");
		Check(PatternsAt(star, true, false, null).Count == 0 && PatternsAt(star, false, true, null).Count == 0, "no star reading the other way");

		var dragonfly = PatternBars(new[] { C(100, 100.25m, 97, 100) });
		Check(PatternsAt(dragonfly, false, false, null).SequenceEqual(new[] { "HangingMan" }), "a dragonfly doji after a rally is a hanging man");

		var engulfing = PatternBars(new[] { C(101, 101.25m, 99.25m, 100), C(100, 101.75m, 99.75m, 101.5m) });
		Check(PatternsAt(engulfing, true, true, null).SequenceEqual(new[] { "BullishEngulfing" })
			&& PatternsAt(engulfing, true, false, null).SequenceEqual(new[] { "BullishEngulfing" }), "an engulfing reads the same after a rally");
	}

	private static decimal[] C(decimal open, decimal high, decimal low, decimal close)
	{
		return new[] { open, high, low, close };
	}

	// 20 identical candles with a 1.00 body far above the pattern, so they only set the
	// average body, then the pattern's candles
	private static List<IndicatorCandle> PatternBars(decimal[][] pattern)
	{
		var bars = Enumerable.Range(0, 20).Select(_ => Bar(200, 201.5m, 199.5m, 201)).ToList();
		bars.AddRange(pattern.Select(p => Bar(p[0], p[1], p[2], p[3])));
		return bars;
	}

	// the patterns the indicator finds on the last of `bars`
	private static List<string> PatternsAt(List<IndicatorCandle> bars, bool bullish, bool afterDecline, Action<FvgReactionLiquiditySweep> configure)
	{
		var ind = NewIndicator(configure);
		ind.Candles.AddRange(bars);
		return PatternList(IndicatorType.GetMethod("FindCandlePatterns", Private).Invoke(ind, new object[] { bars.Count - 1, bullish, afterDecline }));
	}

	#endregion

	#region Scenarios

	// 15 identical bars (no sweeps, no gaps), then a clean bullish FVG:
	// bar 15 H 100.5 | bar 16 impulse | bar 17 L 102.5 -> zone 100.5 - 102.5
	private static List<IndicatorCandle> FvgSetup()
	{
		var bars = Enumerable.Range(0, 15).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
		bars.Add(Bar(100, 100.5m, 99.5m, 100.25m));        // 15 left
		bars.Add(Bar(100.25m, 104, 100.25m, 103.75m));      // 16 impulse
		bars.Add(Bar(103.75m, 105, 102.5m, 104.75m));       // 17 right: bullish, low sits on the zone top
		bars.Add(Bar(104.75m, 104.75m, 103.5m, 103.75m));   // 18 drift, no sweep of the 105 high
		return bars;
	}

	// bar 19: dips into the gap and closes back above it as a hammer - 2-tick body at the top,
	// 5-tick lower wick, no upper wick
	private static IndicatorCandle HammerRetest(decimal delta = 50)
	{
		return Bar(103, 103.5m, 101.75m, 103.5m, delta);
	}

	private static void FvgCompletingCandleIsNotReaction()
	{
		var bars = FvgSetup();
		bars.Add(Bar(103.75m, 104.5m, 103.25m, 104));       // 19 no retest
		var ind = RunHistorical(bars);

		Check(Series(ind, "_bullReaction")[17] == 0, "the gap's own right candle is not a reaction");
		Check(Trades(ind).Count == 0, "no signal without a retest");

		var zones = Zones(ind);
		Check(zones.Count == 1 && zones[0].State == "Active" && zones[0].Top == 102.5m && zones[0].Bottom == 100.5m, "exactly one live zone");
	}

	private static void FvgRetestBuyHitsTakeProfit()
	{
		var bars = FvgSetup();
		bars.Add(HammerRetest());                                    // 19
		bars.Add(Bar(103.5m, 110, 103, 109));                        // 20
		bars.Add(Bar(109, 124, 108, 123));                           // 21 high 124 >= TP 123.5
		bars.Add(Bar(123, 124, 122, 123.5m));                        // 22

		var ind = RunHistorical(bars);
		var trades = Trades(ind);

		Check(trades.Count == 1, $"expected 1 trade, got {trades.Count}");

		if (trades.Count != 1)
			return;

		var t = trades[0];
		Check(t.IsLong && t.IsShown && t.EntryBar == 19, "BUY on the retest bar");
		Check(t.EntryPrice == 103.5m && t.TakeProfitPrice == 123.5m && t.StopLossPrice == 83.5m, "80-tick bracket from the close");
		Check(t.HasBreakEven && t.TriggerPrice == 113.5m && t.BreakEvenPrice == 108.5m, "break-even at +40t moves the stop to +20t");
		Check(t.Trigger == "Fvg" && t.Confirmations == 2 && t.DeltaConfirms && t.CandlePatterns.SequenceEqual(new[] { "Hammer" }),
			$"trigger/confirmations {t.Trigger}/{t.Confirmations} [{string.Join(",", t.CandlePatterns)}]");
		Check(t.ZoneTop == 102.5m && t.ZoneBottom == 100.5m, "the reaction's zone");
		CheckClose(t.TakeProfit, 2.0 / 9, 1e-12, "no history -> TP 2/9");
		CheckClose(t.BreakEven, 4.0 / 9, 1e-12, "no history -> BE 4/9");
		CheckClose(t.ExpectedTicks, 0, 1e-12, "no history -> 0 expected ticks");
		Check(t.Outcome == "TakeProfit" && t.ExitBar == 21 && t.BreakEvenBar == 21, $"outcome {t.Outcome} on bar {t.ExitBar}");
		Check(Series(ind, "_buySignal")[19] == 101.25m, "buy arrow 2 ticks under the low");
		Check(Series(ind, "_bullReaction")[19] == 101.25m, "the (hidden) reaction series marks the bar");
		Check((int)IndicatorType.GetField("_longWins", Private).GetValue(ind) == 1, "panel counts the win");

		var zone = Zones(ind).SingleOrDefault(z => z.Bottom == 100.5m);
		Check(zone != null && zone.State == "Used" && zone.EndBar == 19 && zone.ReactionBullish && zone.ReactionPatterns.SequenceEqual(new[] { "Hammer" })
			&& zone.SignalShown, "the zone is used by the reaction and stops being watched");
	}

	private static void FvgBearishReactionAtBullishGap()
	{
		var bars = FvgSetup();
		bars.Add(Bar(102.75m, 103.25m, 102, 103));                 // 19 dips into the gap, no pattern
		bars.Add(Bar(103, 103.25m, 100, 100.25m, delta: -30));     // 20 bearish engulfing, closes under the gap (and a tweezer top)
		bars.Add(Bar(100.25m, 100.5m, 100, 100.25m));              // 21 forming
		var ind = RunHistorical(bars);
		var trades = Trades(ind);

		Check(trades.Count == 1, $"expected 1 trade, got {trades.Count}");

		if (trades.Count != 1)
			return;

		var t = trades[0];
		Check(!t.IsLong && t.EntryBar == 20 && t.Trigger == "Fvg" && t.Confirmations == 2,
			$"SHORT {t.IsLong}/{t.EntryBar}/{t.Trigger}/{t.Confirmations}");
		Check(t.CandlePatterns.SequenceEqual(new[] { "BearishEngulfing", "TweezerTop" }), $"patterns [{string.Join(",", t.CandlePatterns)}]");

		var zone = Zones(ind).Single();
		Check(zone.State == "Used" && zone.EndBar == 20 && !zone.ReactionBullish && zone.LastTouchBar == 20, "a bearish reaction used the bullish gap");

		// without the close-through rule a pattern inside the gap is enough - but bar 19 still has none
		var loose = Trades(RunHistorical(bars, i => i.RequireCloseThroughZone = false));
		Check(loose.Count == 1 && loose[0].EntryBar == 20, "no pattern on the touch itself");
	}

	private static void FvgReactionNeedsPatternAtZone()
	{
		// (a) a plain dip into the gap - its 1-tick upper wick on an 8-tick bar is no hammer
		var plain = FvgSetup();
		plain.Add(Bar(103, 103.75m, 101.75m, 103.5m, delta: 50));  // 19
		plain.Add(Bar(103.5m, 104, 103, 103.75m));                 // 20 forming
		var ind = RunHistorical(plain);
		Check(Trades(ind).Count == 0, "no pattern, no reaction, no signal");
		var zone = Zones(ind).Single();
		Check(zone.State == "Active" && zone.LastTouchBar == 19, "the gap is still watched");

		// (b) a morning star whose star touched the gap: the third candle stays above the gap,
		// but the pattern includes the touch
		var star = FvgSetup();
		star.Add(Bar(104, 104, 102.75m, 103));                     // 19 long bearish, above the gap
		star.Add(Bar(103, 103.25m, 102.25m, 102.75m));             // 20 the star, in the gap
		star.Add(Bar(102.75m, 104.25m, 102.75m, 104));             // 21 long bullish, back above the first body's middle
		star.Add(Bar(104, 104.25m, 103.75m, 104));                 // 22 forming
		var trades = Trades(RunHistorical(star));
		Check(trades.Count == 1 && trades[0].EntryBar == 21 && trades[0].IsLong
			&& trades[0].CandlePatterns.SequenceEqual(new[] { "BullishEngulfing", "MorningStar" }),
			$"morning star off the gap: [{string.Join(",", trades.SelectMany(t => t.CandlePatterns))}]");

		// (c) a hammer four bars after the touch, above the gap: too far from it to count
		var late = FvgSetup();
		late.Add(Bar(103, 103.75m, 101.75m, 103.5m));              // 19 touch, no pattern
		late.Add(Bar(103.5m, 104, 103.25m, 103.75m));              // 20
		late.Add(Bar(103.75m, 104.25m, 103.5m, 104));              // 21
		late.Add(Bar(104, 104.5m, 103.75m, 104.25m));              // 22
		late.Add(Bar(104.25m, 104.5m, 103.25m, 104.5m));           // 23 hammer, not touching
		late.Add(Bar(104.5m, 104.75m, 104.25m, 104.5m));           // 24 forming
		ind = RunHistorical(late);
		Check(Trades(ind).Count == 0 && Zones(ind).Single().State == "Active", "a pattern that does not include the touch is no reaction");
	}

	private static void FvgZoneLifecycle()
	{
		// used: the hammer reaction retires the gap
		var used = FvgSetup();
		used.Add(HammerRetest());
		used.Add(Bar(103.5m, 104, 103, 103.75m));
		var zones = Zones(RunHistorical(used));
		Check(zones.Count == 1 && zones[0].State == "Used" && zones[0].EndBar == 19, "used");

		// filled: price reaches the far edge (100.5) without a pattern
		List<IndicatorCandle> Through(decimal low)
		{
			var bars = FvgSetup();
			bars.Add(Bar(103, 103.25m, low, 101));                  // 19 no pattern
			bars.Add(Bar(101, 101.5m, 100.75m, 101.25m));           // 20 forming
			return bars;
		}

		zones = Zones(RunHistorical(Through(100.5m)));
		Check(zones.Count == 1 && zones[0].State == "Filled" && zones[0].EndBar == 19, "filled at the far edge");

		zones = Zones(RunHistorical(Through(100.5m), i => i.FvgFill = FvgReactionLiquiditySweep.FvgFillRule.CloseBeyond));
		Check(zones.Count == 1 && zones[0].State == "Active", "close-beyond rule: a wick to the edge is not a fill");

		zones = Zones(RunHistorical(Through(101.5m), i => i.FvgFill = FvgReactionLiquiditySweep.FvgFillRule.Middle));
		Check(zones.Count == 1 && zones[0].State == "Filled", "middle rule: reaching 101.5 fills it");

		zones = Zones(RunHistorical(Through(101.75m), i => i.FvgFill = FvgReactionLiquiditySweep.FvgFillRule.Middle));
		Check(zones.Count == 1 && zones[0].State == "Active", "middle rule: 101.75 does not");

		// expired: never touched, older than Zone Max Age
		var old = FvgSetup();
		old.AddRange(Enumerable.Range(0, 12).Select(_ => Bar(104, 104.5m, 103.5m, 104)));   // 19 - 30, above the gap
		zones = Zones(RunHistorical(old, i => i.MaxZoneAgeBars = 10));
		Check(zones.Count == 1 && zones[0].State == "Expired" && zones[0].EndBar == 27, $"expired on bar {zones.FirstOrDefault()?.EndBar}");
	}

	private static void SweepShortSameBarRule()
	{
		// short at 100 from the sweep: TP 80, SL 120; bar 16 (forming) spans both
		List<IndicatorCandle> Bars(decimal low)
		{
			var bars = Enumerable.Range(0, 15).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
			bars.Add(Bar(100, 101, 99.75m, 100));   // 15 wicks over the 100.5 high, closes back under
			bars.Add(Bar(100, 121, low, 110));      // 16 bullish bar through both levels
			return bars;
		}

		string Outcome(decimal low, FvgReactionLiquiditySweep.SameBarHitRule? rule)
		{
			var trades = Trades(RunHistorical(Bars(low), rule.HasValue ? (Action<FvgReactionLiquiditySweep>)(i => i.SameBarRule = rule.Value) : null));
			return trades.Count == 1 && !trades[0].IsLong && trades[0].Trigger == "Sweep" ? trades[0].Outcome : "no single SHORT";
		}

		Check(Outcome(79, FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst) == "StopLoss", "worst case -> SL");
		Check(Outcome(79, FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection) == "TakeProfit", "bullish bar trades the low first -> TP");
		Check(Outcome(79, null) == "StopLoss", "default nearer-extreme rule, 21 points each way -> tie -> worst case");
		Check(Outcome(79.5m, null) == "TakeProfit", "low nearer the open -> low first -> TP");
	}

	private static void SweepPatternConfirmation()
	{
		// the bar sweeping the 100.5 highs is a shooting star: 1-tick body at its low, 5-tick
		// upper wick, after a doji - so no two-candle pattern
		var bars = Enumerable.Range(0, 15).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
		bars.Add(Bar(100.25m, 101.5m, 100, 100));
		bars.Add(Bar(100, 100.25m, 99.75m, 100));

		var shorts = Trades(RunHistorical(bars));
		Check(shorts.Count == 1 && !shorts[0].IsLong && shorts[0].Trigger == "Sweep" && shorts[0].CandlePatterns.SequenceEqual(new[] { "ShootingStar" })
			&& shorts[0].Confirmations == 1, "shooting star confirms the sweep short");
		Check(Trades(RunHistorical(bars, i => i.RequireCandlePattern = true)).Count == 1, "RequireCandlePattern keeps it");

		var off = Trades(RunHistorical(bars, i => i.PatternHammer = false));
		Check(off.Count == 1 && off[0].CandlePatterns.Count == 0 && off[0].Confirmations == 0, "without shooting stars: no confirmation");
		Check(Trades(RunHistorical(bars, i => { i.PatternHammer = false; i.RequireCandlePattern = true; })).Count == 0,
			"RequireCandlePattern drops the sweep without a pattern");

		var sweeps = (IList)IndicatorType.GetField("_sweeps", Private).GetValue(RunHistorical(bars));
		Check(sweeps.Count == 1 && (int)Get(sweeps[0], "Bar") == 15 && (int)Get(sweeps[0], "SwingBar") == 14 && (decimal)Get(sweeps[0], "Level") == 100.5m
			&& !(bool)Get(sweeps[0], "SweptLows"), "the sweep line runs from the last bar at the high");
	}

	private static void BreakEvenStopAfterTrigger()
	{
		// BUY at 103.5: TP 123.5, SL 83.5, trigger 113.5 (+40t), break-even stop 108.5 (+20t)
		List<IndicatorCandle> Bars()
		{
			var bars = FvgSetup();
			bars.Add(HammerRetest());                                  // 19 BUY
			bars.Add(Bar(103.5m, 114, 103.25m, 113.75m));              // 20 dips a tick, then runs through the trigger
			bars.Add(Bar(113.75m, 113.75m, 108, 108.25m));             // 21 falls back through the break-even stop
			bars.Add(Bar(108.25m, 109, 107, 108));                     // 22 forming
			return bars;
		}

		var ind = RunHistorical(Bars());
		var trades = Trades(ind);
		Check(trades.Count == 1, $"expected 1 trade, got {trades.Count}");

		if (trades.Count != 1)
			return;

		var t = trades[0];
		Check(t.BreakEvenActive && t.BreakEvenBar == 20, "stop moved on the bar that reached +40t");
		Check(t.Outcome == "BreakEven" && t.ExitBar == 21 && t.ExitPrice == 108.5m, $"exit {t.Outcome} on bar {t.ExitBar} at {t.ExitPrice}");
		Check((t.ExitPrice - t.EntryPrice) / Tick == 20, "a break-even exit is worth +20 ticks");
		Check((int)IndicatorType.GetField("_longBreakEvens", Private).GetValue(ind) == 1, "panel counts the break-even exit");

		// worst case: bar 20's low may have come after its high, i.e. after the trigger
		var worst = Trades(RunHistorical(Bars(), i => i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst));
		Check(worst.Count == 1 && worst[0].Outcome == "BreakEven" && worst[0].ExitBar == 20 && worst[0].AmbiguousExit,
			"worst case stops the trade at break-even on bar 20");

		var plain = Trades(RunHistorical(Bars(), i => i.BreakEvenTriggerTicks = 0));
		Check(plain.Count == 1 && !plain[0].HasBreakEven && plain[0].Outcome == "Open", "without break-even the trade is still open");
	}

	private static void LiveBreakEvenFollowsTheTicks()
	{
		var ind = LiveIndicator(FvgSetup());

		StreamBar(ind, new[] { 103m, 102.5m, 101.75m, 102.75m, 103.25m, 103.5m }, delta: 50); // 19: hammer, BUY at 103.5

		// 20: trades under the break-even stop BEFORE reaching the trigger, then holds above it -
		// the bar's low (103.25) is below 108.5, but it came first
		StreamBar(ind, new[] { 103.5m, 103.25m, 108m, 110m, 113.5m, 112m, 110m, 113m });
		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].BreakEvenActive && trades[0].BreakEvenBar == 20 && trades[0].Outcome == "Open",
			"a dip before the trigger does not stop the trade");

		// 21: back to the break-even stop
		StreamBar(ind, new[] { 113m, 111m, 109m, 108.5m, 109m });
		trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].Outcome == "BreakEven" && trades[0].ExitBar == 21 && trades[0].ExitPrice == 108.5m,
			"stopped at break-even on the tick that reached it");
	}

	private static void LiveSignalAppearsAfterClose()
	{
		var ind = LiveIndicator(FvgSetup(), i => i.UseAlerts = true);

		// bar 19 forms tick by tick: into the zone, then back above it as a hammer
		StreamBar(ind, new[] { 103m, 102.5m, 101.75m, 102.75m, 103.25m, 103.5m }, delta: 50);
		Check(Series(ind, "_buySignal")[19] == 0 && Trades(ind).Count == 0, "no signal while the bar is still forming");

		// first tick of bar 20 closes bar 19
		StreamBar(ind, new[] { 103.5m, 104m, 110m, 109m });
		Check(Series(ind, "_buySignal")[19] != 0 && Trades(ind).Count == 1, "signal appears once the bar has closed");
		Check(ind.Alerts.Count == 1 && ind.Alerts[0].StartsWith("BUY @ 103.50 (FVG, Hammer): TP 22% / BE 45% / SL 33%"),
			$"alert names the setup: {string.Join(" / ", ind.Alerts)}");

		// bar 21 runs through TP intrabar; resolution happens on the tick, before the bar closes
		StreamBar(ind, new[] { 109m, 118m, 123.5m });
		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].Outcome == "TakeProfit" && trades[0].ExitBar == 21, "TP resolved intrabar");

		var keys = Zones(ind).Select(z => $"{z.StartBar}/{z.IsBullish}").ToList();
		Check(keys.Count == keys.Distinct().Count(), "no duplicate zones from intrabar recalculation");
	}

	// the retest bar with a footprint: thin levels everywhere plus one heavy level whose side
	// and position vary per case
	private static List<IndicatorCandle> FootprintRetest(decimal heavyPrice, bool heavyOnBid, decimal heavyVolume = 400, decimal? bidShare = null)
	{
		var bars = FvgSetup();
		var retest = HammerRetest();

		for (var p = retest.Low; p <= retest.High; p += Tick)
		{
			var heavy = p == heavyPrice;
			var volume = heavy ? heavyVolume : 20;
			var bid = heavy ? volume * (bidShare ?? (heavyOnBid ? 0.85m : 0.15m)) : 10;
			retest.Levels.Add(new PriceVolumeInfo { Price = p, Volume = volume, Bid = bid, Ask = volume - bid });
		}

		bars.Add(retest);
		bars.Add(Bar(103.5m, 104, 103, 103.75m));
		return bars;
	}

	private static void FootprintFillConfirmation()
	{
		var ind = RunHistorical(FootprintRetest(101.75m, heavyOnBid: true));
		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].FillConfirms && trades[0].Confirmations == 3, "sellers filled into bids at the low confirm the buy");

		var fills = Fills(ind);
		Check(fills.Count == 1 && fills[0].Bar == 19 && fills[0].Price == 101.75m && fills[0].Volume == 400 && fills[0].BidsFilled && fills[0].FromFootprint,
			"the heavy level is the bar's big fill");
		Check(fills.Count == 1 && fills[0].Reaction == 1 && fills[0].ReactionBar == 19 && fills[0].ReactionPatterns.SequenceEqual(new[] { "Hammer" }),
			"the hammer is the bullish reaction to it, on the fill bar itself");

		var wrongSide = RunHistorical(FootprintRetest(101.75m, heavyOnBid: false));
		Check(Trades(wrongSide).Single().FillConfirms == false, "offers filled at the low do not confirm a buy");
		var offerFill = Fills(wrongSide).Single();
		Check(!offerFill.BidsFilled && offerFill.Reaction == 0 && !offerFill.Decided,
			"filled offers mean a rally into them: the hammer shape does not read bullish there, the window is still open");

		var wrongPlace = Trades(RunHistorical(FootprintRetest(103.5m, heavyOnBid: true)));
		Check(wrongPlace.Count == 1 && !wrongPlace[0].FillConfirms, "a fill near the high does not confirm a buy");

		Check(Trades(RunHistorical(FootprintRetest(103.5m, heavyOnBid: true), i => i.RequireFillConfirmation = true)).Count == 0,
			"RequireFillConfirmation drops the unconfirmed buy");

		// as much sold as bought at the busiest price: read as filled bids
		var even = Fills(RunHistorical(FootprintRetest(101.75m, true, bidShare: 0.5m))).Single();
		Check(even.BidsFilled && even.Reaction == 1, "an even split counts as filled bids");

		// thresholds: 8 levels, the heavy one plus 7 x 20 - with 150 the average is 36.25, so
		// 3x the average (108.75) is under the 150 minimum and the minimum decides
		Check(Fills(RunHistorical(FootprintRetest(101.75m, true), i => i.FillMinVolume = 500)).Count == 0, "under Min filled volume: no fill");
		Check(Fills(RunHistorical(FootprintRetest(101.75m, true, heavyVolume: 150))).Count == 1, "exactly the minimum counts");
		Check(Fills(RunHistorical(FootprintRetest(101.75m, true, heavyVolume: 140))).Count == 0, "just under it does not");
		Check(Fills(RunHistorical(FootprintRetest(101.75m, true, heavyVolume: 150), i => i.FillVolumeMultiplier = 5)).Count == 0,
			"5x the average (181.25) is more than 150");
	}

	private static void FillReactionSignal()
	{
		// a quiet market, then a bearish bar whose low fills a heavy bid, then the reaction bar
		List<IndicatorCandle> Bars(params IndicatorCandle[] after)
		{
			var bars = Enumerable.Range(0, 15).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
			var fillBar = Bar(100, 100.5m, 99.75m, 99.75m);                              // 15
			fillBar.Levels.Add(new PriceVolumeInfo { Price = 99.75m, Volume = 400, Bid = 340, Ask = 60 });

			foreach (var p in new[] { 100m, 100.25m, 100.5m })
				fillBar.Levels.Add(new PriceVolumeInfo { Price = p, Volume = 20, Bid = 10, Ask = 10 });

			bars.Add(fillBar);
			bars.AddRange(after);
			bars.Add(Bar(100.25m, 100.5m, 100, 100.25m));                                 // forming
			return bars;
		}

		// 16: bullish engulfing (and tweezer bottom) closing above the fill
		var engulfing = Bars(Bar(99.75m, 100.5m, 99.75m, 100.25m, delta: 20));
		var ind = RunHistorical(engulfing);
		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].IsLong && trades[0].EntryBar == 16 && trades[0].Trigger == "Fill", $"BUY from the fill reaction ({trades.Count})");

		if (trades.Count == 1)
		{
			Check(trades[0].CandlePatterns.SequenceEqual(new[] { "BullishEngulfing", "TweezerBottom" }), $"[{string.Join(",", trades[0].CandlePatterns)}]");
			Check(trades[0].FillConfirms && trades[0].DeltaConfirms && trades[0].Confirmations == 3, "delta, order flow and pattern");
			Check(trades[0].FillPrice == 99.75m && trades[0].FillVolume == 400, "the fill it reacted to");
		}

		var fill = Fills(ind).Single();
		Check(fill.Bar == 15 && fill.BidsFilled && fill.Reaction == 1 && fill.ReactionBar == 16 && fill.SignalShown && fill.Decided,
			"bullish reaction on the bar after the fill");

		Check(Trades(RunHistorical(engulfing, i => i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FillReactionOnly)).Count == 1,
			"fill reactions only: the same signal");
		Check(Trades(RunHistorical(engulfing, i => i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly)).Count == 0,
			"FVG reactions only: none");

		// no pattern in the window: no reaction once the window has closed
		var doji = Bar(99.75m, 100, 99.5m, 99.75m);
		ind = RunHistorical(Bars(doji, Bar(99.75m, 100, 99.5m, 99.75m), Bar(99.75m, 100, 99.5m, 99.75m)));
		fill = Fills(ind).Single();
		Check(fill.Reaction == 0 && fill.Decided && fill.WatchedBars == 3 && Trades(ind).Count == 0, "no clear reaction within 3 bars");
		Check(fill.UpTicks == 1 && fill.DownTicks == 1, $"follow-through +{fill.UpTicks}t / -{fill.DownTicks}t");

		ind = RunHistorical(Bars(doji, Bar(99.75m, 100, 99.5m, 99.75m)));
		Check(!Fills(ind).Single().Decided, "still waiting with a bar of the window left");
	}

	#endregion

	#region Order book

	private static void OrderBookRestingOrders()
	{
		var ind = LiveIndicator(FvgSetup());
		var live = ind.Candles.Count - 1;
		var t0 = new DateTime(2026, 3, 2, 15, 0, 0);
		ind.MarketTime = t0;

		Depth(ind, true, 103.5m, 40);    // too small to follow
		Depth(ind, true, 103m, 120);     // a big bid
		Depth(ind, true, 101m, 200);     // a deeper big bid - the deepest level
		Depth(ind, false, 105m, 95);     // a big offer
		Depth(ind, false, 107m, 20);     // a small offer behind it
		var orders = Orders(ind);
		Check(orders.Count == 3 && orders.All(o => o.State == "Active" && o.FirstBar == live), "three resting orders of 70+, from the live bar");

		// the 103 bid is hit: 80 of its 120 sold into it, then the level is gone
		Print(ind, 103m, 50, sell: true);
		Print(ind, 103m, 30, sell: true);
		Depth(ind, true, 103m, 0);
		var bid = Orders(ind).Single(o => o.Price == 103m);
		Check(bid.State == "Filled" && bid.EndBar == live && bid.Traded == 80 && bid.MaxVolume == 120, "filled at once: 80 of 120 traded against it");
		Check(Fills(ind).Count == 0, "80 traded is under Min filled volume: its band ends, no bubble of its own");

		// buys at 105 do not fill a bid; the offer leaves without prints of its own
		Print(ind, 105m, 30, sell: true);
		Depth(ind, false, 105m, 0);
		Check(Orders(ind).Single(o => o.Price == 105m).Leaving, "leaving, waiting for its prints");
		ind.MarketTime = t0.AddSeconds(1);
		Print(ind, 104m, 1, sell: false);
		Check(Orders(ind).Single(o => o.Price == 105m).State == "Active", "not decided within the 2 seconds");
		ind.MarketTime = t0.AddSeconds(3);
		Print(ind, 104m, 1, sell: false);
		Check(Orders(ind).Single(o => o.Price == 105m).State == "Pulled", "pulled: no buys against it in 2 seconds");

		// the book reports a level gone before its prints arrive
		Depth(ind, false, 106m, 150);
		Depth(ind, false, 106m, 0);
		ind.MarketTime = t0.AddSeconds(3.5);
		Print(ind, 106m, 100, sell: false);
		Check(Orders(ind).Single(o => o.Price == 106m).State == "Filled", "late prints still make it a fill");

		// a fill of its own takes Min filled volume (150) traded against the order
		Depth(ind, false, 106.25m, 200);
		Print(ind, 106.25m, 149, sell: false);
		Depth(ind, false, 106.25m, 0);
		Depth(ind, false, 106.5m, 200);
		Print(ind, 106.5m, 150, sell: false);
		Depth(ind, false, 106.5m, 0);
		var fills = Fills(ind);
		Check(Orders(ind).Count(o => (o.Price == 106.25m || o.Price == 106.5m) && o.State == "Filled") == 2
			&& fills.Count == 1 && fills[0].Price == 106.5m && fills[0].Volume == 150 && fills[0].RestingSize == 200 && !fills[0].BidsFilled
			&& !fills[0].FromFootprint && fills[0].Bar == live,
			"149 traded is no fill of its own, 150 is");

		// the deepest bid leaving the book scrolled out of view
		Depth(ind, true, 101m, 0);
		ind.MarketTime = t0.AddSeconds(6);
		Print(ind, 104m, 1, sell: false);
		Check(Orders(ind).Single(o => o.Price == 101m).State == "OutOfView", "out of view, not pulled");

		// dips under the size and comes back before it is settled: the same order
		Depth(ind, true, 102.5m, 90);
		Depth(ind, true, 102.5m, 30);
		Check(Orders(ind).Single(o => o.Price == 102.5m).Leaving, "under the size: leaving");
		Depth(ind, true, 102.5m, 110);
		var back = Orders(ind).Single(o => o.Price == 102.5m);
		Check(back.State == "Active" && !back.Leaving && back.MaxVolume == 110 && back.Volume == 110 && back.EndBar == -1, "back in time: still resting");

		// resting orders stay in the book across bars
		StreamBar(ind, new[] { 104m, 104.25m });
		Check(Orders(ind).Single(o => o.Price == 102.5m).FirstBar == live, "first seen on its own bar");

		// a new chart calculation reloads the book from its snapshot
		ind.Depth.Levels.Add(new MarketDataArg { Price = 102m, Volume = 130, DataType = MarketDataType.Bid });
		ind.Depth.Levels.Add(new MarketDataArg { Price = 102.25m, Volume = 20, DataType = MarketDataType.Bid });
		ind.Depth.Levels.Add(new MarketDataArg { Price = 104.5m, Volume = 75, DataType = MarketDataType.Ask });
		Recalculate(ind);
		orders = Orders(ind);
		Check(orders.Count == 2 && orders.All(o => o.State == "Active" && o.FirstBar == ind.Candles.Count - 1)
			&& orders.Any(o => o.IsBid && o.Price == 102m && o.Volume == 130) && orders.Any(o => !o.IsBid && o.Price == 104.5m), "reloaded from the snapshot");

		// a higher minimum follows fewer orders
		ind.RestingOrderMin = 100;
		Recalculate(ind);
		Check(Orders(ind).Select(o => o.Price).SequenceEqual(new[] { 102m }), "only the 130 bid is 100+");
	}

	private static void OrderBookFillReaction()
	{
		var history = Enumerable.Range(0, 17).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
		var ind = LiveIndicator(history);
		var t0 = new DateTime(2026, 3, 2, 15, 0, 0);
		ind.MarketTime = t0;

		// bar 17: a 200-lot bid at 99.5 is sold into and filled, and the bar closes as a hammer
		var candle = new IndicatorCandle { Open = 100, High = 100, Low = 100, Close = 100, Time = DateTime.MinValue };
		ind.Candles.Add(candle);
		AddTick(ind, candle, 100m);
		Depth(ind, true, 99.25m, 40);
		Depth(ind, true, 99.5m, 200);
		AddTick(ind, candle, 99.75m);
		AddTick(ind, candle, 99.5m);
		Print(ind, 99.5m, 180, sell: true);
		Depth(ind, true, 99.5m, 0);
		AddTick(ind, candle, 100m);
		AddTick(ind, candle, 100.25m);

		var fills = Fills(ind);
		Check(fills.Count == 1 && fills[0].Bar == 17 && !fills[0].FromFootprint && fills[0].RestingSize == 200 && fills[0].Volume == 180,
			"the order book saw the bid filled");

		// the footprint of bar 17 has its biggest level right there
		candle.Levels.Add(new PriceVolumeInfo { Price = 99.5m, Volume = 200, Bid = 180, Ask = 20 });

		foreach (var p in new[] { 99.75m, 100m, 100.25m })
			candle.Levels.Add(new PriceVolumeInfo { Price = p, Volume = 20, Bid = 10, Ask = 10 });

		StreamBar(ind, new[] { 100.25m, 100.5m });   // bar 18 closes bar 17
		fills = Fills(ind);
		Check(fills.Count == 1 && fills[0].FromFootprint && fills[0].RestingSize == 200 && fills[0].Volume == 200, "one fill: footprint and order book agree");
		Check(fills[0].Reaction == 1 && fills[0].ReactionBar == 17 && fills[0].ReactionPatterns.SequenceEqual(new[] { "Hammer" }), "hammer: bullish reaction");

		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].IsLong && trades[0].EntryBar == 17 && trades[0].Trigger == "Fill", "the footprint fill's reaction is a BUY");

		// bar 19: an order-book fill away from the footprint's biggest level stays a fill of its
		// own - shown, but no signal (history has no order book to repeat it)
		ind.MarketTime = t0.AddSeconds(10);
		Depth(ind, false, 101m, 200);
		var bar19 = new IndicatorCandle { Open = 100.5m, High = 100.5m, Low = 100.5m, Close = 100.5m, Time = DateTime.MinValue };
		ind.Candles.Add(bar19);
		AddTick(ind, bar19, 100.75m);
		AddTick(ind, bar19, 101m);
		Print(ind, 101m, 160, sell: false);
		Depth(ind, false, 101m, 0);
		AddTick(ind, bar19, 100.75m);
		fills = Fills(ind);
		Check(fills.Count == 2 && !fills[1].FromFootprint && !fills[1].BidsFilled && fills[1].RestingSize == 200 && fills[1].Bar == 19, "offer filled at 101");

		// a filled order with less than Min filled volume traded against it only ends its band
		Depth(ind, true, 100.5m, 90);
		AddTick(ind, bar19, 100.5m);
		Print(ind, 100.5m, 60, sell: true);
		Depth(ind, true, 100.5m, 0);
		Check(Fills(ind).Count == 2 && Orders(ind).Single(o => o.Price == 100.5m).State == "Filled", "a 90-lot filled with 60 traded is no bubble of its own");
		AddTick(ind, bar19, 100.75m);

		// its bar closes (as a shooting star) before the late prints arrive: the fill still gets
		// its reaction read from it
		ind.MarketTime = t0.AddSeconds(20);
		Depth(ind, false, 101.25m, 200);
		StreamBar(ind, new[] { 100.75m, 101m, 101.25m, 100.5m });            // bar 20 closes bar 19
		Depth(ind, false, 101.25m, 0);
		StreamBar(ind, new[] { 100.5m, 100.25m });                           // bar 21 closes bar 20 while it waits
		ind.MarketTime = t0.AddSeconds(21);
		Print(ind, 101.25m, 170, sell: false);
		var late = Fills(ind).SingleOrDefault(f => f.Price == 101.25m);
		Check(late != null && late.Bar == 20 && late.Reaction == -1 && late.ReactionBar == 20
			&& late.ReactionPatterns.SequenceEqual(new[] { "ShootingStar", "TweezerTop" }),
			"a late fill catches up on the bar that closed meanwhile");
		Check(Trades(ind).Count(t => t.Trigger == "Fill") == 1, "order-book fills never make signals");
	}

	// A resting order filled at the price of the bar's big fill merges with it only when it is
	// the side the footprint says was filled, whichever of the two is seen first
	private static void OrderBookFillSides()
	{
		var history = Enumerable.Range(0, 17).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
		var ind = LiveIndicator(history);
		ind.MarketTime = new DateTime(2026, 3, 2, 15, 0, 0);

		// the heaviest price of a bar at 99.5 - 100.5, where sellers hit the bids hardest
		void HeavyAt(IndicatorCandle candle, decimal price)
		{
			for (var p = 99.5m; p <= 100.5m; p += Tick)
			{
				candle.Levels.Add(p == price
					? new PriceVolumeInfo { Price = p, Volume = 500, Bid = 330, Ask = 170 }
					: new PriceVolumeInfo { Price = p, Volume = 20, Bid = 10, Ask = 10 });
			}
		}

		IndicatorCandle NewBar(decimal open)
		{
			var candle = new IndicatorCandle { Open = open, High = open, Low = open, Close = open, Time = DateTime.MinValue };
			ind.Candles.Add(candle);
			AddTick(ind, candle, open);
			return candle;
		}

		// bar 17: a 200-lot offer at 100.25 is lifted while the bar trades (seen first), then the
		// footprint of the closed bar says bids were filled there
		var bar17 = NewBar(100);
		Depth(ind, false, 100.5m, 20);
		Depth(ind, false, 100.25m, 200);
		AddTick(ind, bar17, 100.25m);
		Print(ind, 100.25m, 160, sell: false);
		Depth(ind, false, 100.25m, 0);

		foreach (var p in new[] { 99.5m, 100.5m, 100m })
			AddTick(ind, bar17, p);

		HeavyAt(bar17, 100.25m);
		StreamBar(ind, new[] { 100m, 100.25m });   // bar 18 closes bar 17
		var fills = Fills(ind).Where(f => f.Bar == 17).ToList();
		Check(fills.Count == 2
			&& fills.Any(f => !f.FromFootprint && !f.BidsFilled && f.RestingSize == 200 && f.Volume == 160)
			&& fills.Any(f => f.FromFootprint && f.BidsFilled && f.RestingSize == 0 && f.Volume == 500),
			"an offer filled where the footprint says bids were filled stays a fill of its own");

		// bar 19: the same, but the prints that fill the offer arrive after the bar has closed
		// (the footprint fill is seen first)
		var bar19 = NewBar(100.25m);                // closes bar 18
		Depth(ind, false, 100.25m, 200);

		foreach (var p in new[] { 99.5m, 100.5m, 100.25m })
			AddTick(ind, bar19, p);

		Depth(ind, false, 100.25m, 0);
		HeavyAt(bar19, 100.25m);
		StreamBar(ind, new[] { 100.25m, 100m });   // bar 20 closes bar 19
		Print(ind, 100.25m, 160, sell: false);
		fills = Fills(ind).Where(f => f.Bar == 19).ToList();
		Check(fills.Count == 2
			&& fills.Any(f => !f.FromFootprint && !f.BidsFilled && f.RestingSize == 200)
			&& fills.Any(f => f.FromFootprint && f.BidsFilled && f.RestingSize == 0),
			"a late offer fill does not mark the footprint's bid fill");

		// bar 21: a 200-lot bid at 100 whose prints arrive after the close marks the footprint's
		// bid fill at 100 instead of adding a second one
		var bar21 = NewBar(100);                    // closes bar 20
		Depth(ind, true, 99.75m, 20);
		Depth(ind, true, 100m, 200);

		foreach (var p in new[] { 99.5m, 100.5m, 100m })
			AddTick(ind, bar21, p);

		Depth(ind, true, 100m, 0);
		HeavyAt(bar21, 100m);
		StreamBar(ind, new[] { 100m, 100.25m });   // bar 22 closes bar 21
		Print(ind, 100m, 160, sell: true);
		fills = Fills(ind).Where(f => f.Bar == 21).ToList();
		Check(fills.Count == 1 && fills[0].FromFootprint && fills[0].BidsFilled && fills[0].RestingSize == 200,
			$"a late bid fill marks the footprint's bid fill ({fills.Count} fills)");

		// bar 23: a 90-lot bid filled with only 60 traded is no fill of its own, but it still
		// marks the footprint's bid fill at its price when the bar closes
		var bar23 = NewBar(100);                    // closes bar 22
		Depth(ind, true, 100m, 90);

		foreach (var p in new[] { 99.5m, 100.5m, 100m })
			AddTick(ind, bar23, p);

		Print(ind, 100m, 60, sell: true);
		Depth(ind, true, 100m, 0);
		Check(!Fills(ind).Any(f => f.Bar == 23), "no bubble for a small filled order");
		HeavyAt(bar23, 100m);
		StreamBar(ind, new[] { 100m, 100.25m });   // bar 24 closes bar 23
		fills = Fills(ind).Where(f => f.Bar == 23).ToList();
		Check(fills.Count == 1 && fills[0].FromFootprint && fills[0].RestingSize == 90, "the small filled bid marks the footprint fill");
		Check(Orders(ind).Count(o => o.State == "Filled") == 4, "all four orders were filled");
	}

	private static void DefaultSettings()
	{
		var fresh = new FvgReactionLiquiditySweep();
		Check(fresh.SignalHours == FvgReactionLiquiditySweep.SignalHoursRule.RegularHours && fresh.RegularHoursStart == new TimeSpan(9, 30, 0)
			&& fresh.RegularHoursEnd == new TimeSpan(16, 0, 0) && fresh.OpeningRangeMinutes == 30, "signals in regular hours 9:30 - 16:00 New York, 30-minute opening range");
		Check(fresh.LevelPriorDay && fresh.LevelOvernight && fresh.LevelOpeningRange && fresh.LevelEqual && fresh.EqualToleranceTicks == 2
			&& fresh.EqualLookbackBars == 120 && fresh.DrawKeyLevels, "every key level on");
		Check(fresh.FillSize == FvgReactionLiquiditySweep.FillSizeRule.TopOfRecentBars && fresh.FillTopPercent == 10 && fresh.FillLookbackBars == 200
			&& fresh.FillMinVolume == 150 && fresh.FillVolumeMultiplier == 3.0, "big fills among the top 10% of the last 200 bars");
		Check(fresh.RestingSize == FvgReactionLiquiditySweep.RestingSizeRule.FixedContracts && fresh.RestingOrderMin == 70 && fresh.RestingMultiplier == 5.0,
			"resting orders of 70 contracts");
		Check(!fresh.ShowScoreboard, "the scoreboard opens on hover");

		// every setting has its own place in the settings window
		var orders = IndicatorType.GetProperties()
			.Select(p => p.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>())
			.Where(d => d != null)
			.Select(d => d.GetOrder())
			.ToList();
		Check(orders.Count > 60 && orders.All(o => o.HasValue) && orders.Distinct().Count() == orders.Count, $"{orders.Count} settings, each with its own order");
	}

	private static void NewYorkTimeAndHours()
	{
		var toNewYork = IndicatorType.GetMethod("NewYorkTime", PrivateStatic);
		DateTime Ny(DateTime utc) => (DateTime)toNewYork.Invoke(null, new object[] { utc });

		// US daylight saving 2026: March 8 2:00 to November 1 2:00
		Check(Ny(new DateTime(2026, 3, 8, 6, 59, 0)) == new DateTime(2026, 3, 8, 1, 59, 0), "a minute before summer time: UTC-5");
		Check(Ny(new DateTime(2026, 3, 8, 7, 0, 0)) == new DateTime(2026, 3, 8, 3, 0, 0), "summer time: UTC-4");
		Check(Ny(new DateTime(2026, 11, 1, 5, 59, 0)) == new DateTime(2026, 11, 1, 1, 59, 0), "a minute before winter time");
		Check(Ny(new DateTime(2026, 11, 1, 6, 0, 0)) == new DateTime(2026, 11, 1, 1, 0, 0), "winter time again");
		Check(Ny(DateTime.MinValue) == DateTime.MinValue, "no time, no conversion");

		// every half hour of 2024 - 2028 against the system's New York time zone
		var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		var wrong = 0;

		for (var t = new DateTime(2024, 1, 1); t < new DateTime(2029, 1, 1); t = t.AddMinutes(30))
			wrong += Ny(t) != TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), zone) ? 1 : 0;

		Check(wrong == 0, $"{wrong} half hours differ from the system's New York time");

		var tradingDay = IndicatorType.GetMethod("TradingDayOf", PrivateStatic);
		DateTime Day(DateTime t) => (DateTime)tradingDay.Invoke(null, new object[] { t });
		Check(Day(new DateTime(2026, 1, 14, 17, 59, 0)) == new DateTime(2026, 1, 14) && Day(new DateTime(2026, 1, 14, 18, 0, 0)) == new DateTime(2026, 1, 15),
			"18:00 starts the next trading day");

		// the signal hours, New York time in January (UTC-5)
		var inHours = IndicatorType.GetMethod("InSignalHours", Private);

		bool At(FvgReactionLiquiditySweep.SignalHoursRule rule, int hour, int minute, Action<FvgReactionLiquiditySweep> more = null)
		{
			var ind = NewIndicator(i =>
			{
				i.SignalHours = rule;
				more?.Invoke(i);
			});

			return (bool)inHours.Invoke(ind, new object[] { new DateTime(2026, 1, 14, hour, minute, 0).AddHours(5) });
		}

		var regular = FvgReactionLiquiditySweep.SignalHoursRule.RegularHours;
		var firstTwo = FvgReactionLiquiditySweep.SignalHoursRule.FirstTwoHours;
		var noLunch = FvgReactionLiquiditySweep.SignalHoursRule.RegularHoursNoLunch;
		Check(!At(regular, 9, 29) && At(regular, 9, 30) && At(regular, 15, 59) && !At(regular, 16, 0), "regular hours 9:30 - 16:00");
		Check(!At(firstTwo, 9, 29) && At(firstTwo, 9, 30) && At(firstTwo, 11, 29) && !At(firstTwo, 11, 30), "first two hours 9:30 - 11:30");
		Check(At(noLunch, 11, 29) && !At(noLunch, 11, 30) && !At(noLunch, 13, 29) && At(noLunch, 13, 30) && !At(noLunch, 16, 0), "lunch 11:30 - 13:30 left out");
		Check(At(FvgReactionLiquiditySweep.SignalHoursRule.AllHours, 3, 0), "all hours");

		Action<FvgReactionLiquiditySweep> early = i =>
		{
			i.RegularHoursStart = new TimeSpan(8, 30, 0);
			i.RegularHoursEnd = new TimeSpan(20, 0, 0);
		};

		Check(At(regular, 8, 30, early) && At(regular, 17, 59, early) && !At(regular, 18, 0, early), "other hours; the session ends by 18:00");
		Check(!At(firstTwo, 10, 30, early) && At(firstTwo, 10, 29, early), "the first two hours follow the start");
	}

	// a bar opening at a New York time in January 2026 (UTC-5)
	private static IndicatorCandle NyBar(int day, int hour, int minute, decimal open, decimal high, decimal low, decimal close)
	{
		var bar = Bar(open, high, low, close);
		bar.Time = new DateTime(2026, 1, day, hour, minute, 0).AddHours(5);
		return bar;
	}

	// Two trading days in 30-minute bars. Jan 13: overnight 99.5 - 100.5, regular hours 98 - 102;
	// bar 6 sweeps the overnight and opening-range highs, bar 10 their lows. Jan 14 (from 18:00
	// on the 13th): an overnight sweep of the swing lows at bar 22, the open at bar 27, bar 28
	// breaks the new overnight and opening-range highs, and the shooting star at bar 31 sweeps
	// the prior day high.
	private static List<IndicatorCandle> KeyLevelDays()
	{
		IndicatorCandle Flat(int day, int hour, int minute) => NyBar(day, hour, minute, 100, 100.5m, 99.5m, 100);

		return new List<IndicatorCandle>
		{
			Flat(13, 8, 0), Flat(13, 8, 30), Flat(13, 9, 0),
			Flat(13, 9, 30), Flat(13, 10, 0), Flat(13, 10, 30),
			NyBar(13, 11, 0, 100, 102, 99.5m, 100.25m),
			Flat(13, 11, 30), Flat(13, 12, 0), Flat(13, 12, 30),
			NyBar(13, 13, 0, 100, 100.5m, 98, 99.75m),
			Flat(13, 13, 30), Flat(13, 14, 0), Flat(13, 14, 30), Flat(13, 15, 0), Flat(13, 15, 30),
			Flat(13, 16, 0), Flat(13, 16, 30), Flat(13, 17, 0), Flat(13, 17, 30),
			Flat(13, 18, 0), Flat(13, 19, 0),
			NyBar(13, 21, 0, 100, 100.5m, 99, 100),
			Flat(14, 1, 0), Flat(14, 5, 0), Flat(14, 8, 0), Flat(14, 9, 0),
			Flat(14, 9, 30),
			NyBar(14, 10, 0, 100, 101, 99.75m, 100.75m),
			NyBar(14, 10, 30, 100.75m, 101.5m, 100.5m, 101.25m),
			NyBar(14, 11, 0, 101.25m, 101.75m, 101, 101.5m),
			NyBar(14, 11, 30, 101.75m, 102.5m, 101.5m, 101.5m),
			NyBar(14, 12, 0, 101.5m, 101.75m, 100.75m, 101),
			NyBar(14, 12, 30, 101, 101.25m, 100.75m, 101)
		};
	}

	private static void KeyLevelDaysScenario()
	{
		Action<FvgReactionLiquiditySweep> Levels(FvgReactionLiquiditySweep.SignalHoursRule hours, Action<FvgReactionLiquiditySweep> more = null) => i =>
		{
			i.SignalHours = hours;
			i.LevelPriorDay = true;
			i.LevelOvernight = true;
			i.LevelOpeningRange = true;
			i.SwingLookback = 2;
			i.OneTradeAtATime = false;
			more?.Invoke(i);
		};

		string TradeKey(TradeView t) => $"{t.EntryBar}|{(t.IsLong ? "BUY" : "SHORT")}|{t.Trigger}|{t.SweptLevel}|{string.Join(",", t.CandlePatterns)}";

		var ind = RunHistorical(KeyLevelDays(), Levels(FvgReactionLiquiditySweep.SignalHoursRule.RegularHours));
		var expectedLevels = new[]
		{
			"OvernightHigh|100.5|3|6|Swept|-1|-1", "OvernightLow|99.5|3|10|Swept|-1|-1",
			"OpeningRangeHigh|100.5|4|6|Swept|-1|-1", "OpeningRangeLow|99.5|4|10|Swept|-1|-1",
			"PriorDayHigh|102|20|31|Swept|-1|-1", "PriorDayLow|98|20|-1|Fresh|-1|-1",
			"OvernightHigh|100.5|27|28|Broken|-1|-1", "OvernightLow|99|27|-1|Fresh|-1|-1",
			"OpeningRangeHigh|100.5|28|28|Broken|-1|-1", "OpeningRangeLow|99.5|28|-1|Fresh|-1|-1"
		}.OrderBy(k => k, StringComparer.Ordinal).ToList();
		var levels = KeyLevelKeys(ind);
		Check(levels.SequenceEqual(expectedLevels), $"key levels: {string.Join("; ", levels)}");

		var trades = Trades(ind).Select(TradeKey).ToList();
		var expected = new[]
		{
			"6|SHORT|KeySweep|OvernightHigh@100.5@6|", "10|BUY|KeySweep|OvernightLow@99.5@10|", "31|SHORT|KeySweep|PriorDayHigh@102@31|ShootingStar"
		};
		Check(trades.SequenceEqual(expected), $"regular hours: {string.Join("; ", trades)}");

		// the overnight sweep of bar 22 only counts around the clock
		trades = Trades(RunHistorical(KeyLevelDays(), Levels(FvgReactionLiquiditySweep.SignalHoursRule.AllHours))).Select(TradeKey).ToList();
		Check(trades.SequenceEqual(expected.Take(2).Append("22|BUY|Sweep||").Concat(expected.Skip(2))), $"all hours: {string.Join("; ", trades)}");

		trades = Trades(RunHistorical(KeyLevelDays(), Levels(FvgReactionLiquiditySweep.SignalHoursRule.RegularHoursNoLunch))).Select(TradeKey).ToList();
		Check(trades.SequenceEqual(expected.Take(1)), $"no lunch: 13:00 and 11:30 are left out ({string.Join("; ", trades)})");

		trades = Trades(RunHistorical(KeyLevelDays(), Levels(FvgReactionLiquiditySweep.SignalHoursRule.FirstTwoHours))).Select(TradeKey).ToList();
		Check(trades.SequenceEqual(expected.Take(1)), $"first two hours: 11:30 is too late ({string.Join("; ", trades)})");

		// without the overnight levels bar 6 sweeps the opening range high instead
		trades = Trades(RunHistorical(KeyLevelDays(), Levels(FvgReactionLiquiditySweep.SignalHoursRule.RegularHours, i => i.LevelOvernight = false)))
			.Select(TradeKey).ToList();
		Check(trades.Take(2).SequenceEqual(new[] { "6|SHORT|KeySweep|OpeningRangeHigh@100.5@6|", "10|BUY|KeySweep|OpeningRangeLow@99.5@10|" }),
			$"opening range next in line: {string.Join("; ", trades)}");

		// and without any key level they are plain sweeps
		trades = Trades(RunHistorical(KeyLevelDays(), i => { i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.RegularHours; i.SwingLookback = 2; i.OneTradeAtATime = false; }))
			.Select(TradeKey).ToList();
		Check(trades.SequenceEqual(new[] { "6|SHORT|Sweep||", "10|BUY|Sweep||", "31|SHORT|Sweep||ShootingStar" }), $"plain sweeps: {string.Join("; ", trades)}");

		// key-level sweeps only
		trades = Trades(RunHistorical(KeyLevelDays(), Levels(FvgReactionLiquiditySweep.SignalHoursRule.AllHours,
			i => i.SignalSource = FvgReactionLiquiditySweep.SignalMode.KeyLevelSweeps))).Select(TradeKey).ToList();
		Check(trades.SequenceEqual(expected), $"key-level sweeps only: {string.Join("; ", trades)}");

		// drawn: lines, a dot per sweep, names, and a tooltip on a name
		ind.FirstVisibleBarNumber = 0;
		ind.LastVisibleBarNumber = ind.Candles.Count - 1;
		var chart = (FakeChart)ind.ChartInfo;
		chart.TopPrice = 110;
		var context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Operations.Count(o => o.Kind == "ellipse" && o.Color.A == 220) == 5, "a dot where each of the five sweeps took its level");
		Check(context.Operations.Count(o => o.Kind == "ellipse" && o.Color.A == 220 && o.Color.R == 239) == 3
			&& context.Operations.Count(o => o.Kind == "ellipse" && o.Color.A == 220 && o.Color.R == 38) == 2, "red dots for the three swept highs, green for the two lows");
		Check(context.Operations.Count(o => o.Kind == "line" && o.Dashed && o.Color.R == 149 && o.Color.A == 70) == 2, "dashed where the two breaks went through");
		Check(context.Strings.Contains("PDH") && context.Strings.Contains("PDL"), "the prior day levels are named");
		Check(CardsOf(context).Any(c => c.Setup == "Sweep PDH · Shooting star"), "the open short's card names the level it swept");

		var pdl = context.Operations.First(o => o.Kind == "text" && o.Text == "PDL");
		chart.MouseLocationInfo.LastPosition = new Point(pdl.From.X + 2, pdl.From.Y + 2);
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Contains("Prior day low 98.00") && context.Strings.Any(t => t.StartsWith("Waiting since bar 20")), "level tooltip on hover");

		// the signal tooltip names the swept level
		var pdhCard = context.Operations.First(o => o.Kind == "text" && o.Text == "Sweep PDH · Shooting star");
		chart.MouseLocationInfo.LastPosition = new Point(pdhCard.From.X + 2, pdhCard.From.Y + 2);
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Contains("Swept the prior day high 102.00 on bar 31"), "signal tooltip names the swept level");
		Check(context.Strings.Any(t => t.StartsWith("Signals: regular hours 09:30–16:00 · last bar 12:30 New York")), "the panel shows the hours and the New York clock");
	}

	// flat bars with swing highs at bar 20 (101) and bar `second`, then a shooting star through
	// both five bars later
	private static List<IndicatorCandle> EqualHighsMarket(decimal secondHigh, decimal between = 100.5m, bool sweep = true, int length = 35, int second = 28)
	{
		var bars = Enumerable.Range(0, 20).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
		bars.Add(Bar(100, 101, 99.5m, 100));

		for (var i = 21; i < second; i++)
			bars.Add(Bar(100, i == 24 ? between : 100.5m, 99.5m, 100));

		bars.Add(Bar(100, secondHigh, 99.5m, 100));

		while (bars.Count < length - 1)
			bars.Add(sweep && bars.Count == second + 5 ? Bar(101, 101.75m, 100.75m, 100.75m) : Bar(100, 100.5m, 99.5m, 100));

		bars.Add(Bar(100, 100.5m, 99.5m, 100));
		return bars;
	}

	private static void EqualHighsScenario()
	{
		Action<FvgReactionLiquiditySweep> Equal(Action<FvgReactionLiquiditySweep> more = null) => i =>
		{
			i.LevelEqual = true;
			more?.Invoke(i);
		};

		var ind = RunHistorical(EqualHighsMarket(101.25m), Equal());
		Check(KeyLevelKeys(ind).SequenceEqual(new[] { "EqualHighs|101.25|32|33|Swept|20|28" }), $"equal highs: {string.Join("; ", KeyLevelKeys(ind))}");
		// each swing high sweeps the highs before it; the shooting star takes the pair
		var trades = Trades(ind);
		Check(trades.Select(t => $"{t.EntryBar}|{t.Trigger}").SequenceEqual(new[] { "20|Sweep", "28|Sweep", "33|KeySweep" }),
			$"sweeps: {string.Join("; ", trades.Select(t => $"{t.EntryBar} {t.Trigger} {t.SweptLevel}"))}");
		var trade = trades.Last();
		Check(!trade.IsLong && trade.EntryBar == 33 && trade.Trigger == "KeySweep" && trade.SweptLevel == "EqualHighs@101.25@33"
			&& trade.CandlePatterns.SequenceEqual(new[] { "ShootingStar" }), "the shooting star through them is a key-level SHORT");

		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.75m), Equal())).Count == 0, "3 ticks apart: not equal");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.5m), Equal())).Count == 1, "2 ticks apart: equal");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.25m, between: 102m), Equal())).Count == 0, "a higher bar between them");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.25m, between: 101.5m), Equal())).SequenceEqual(new[] { "EqualHighs|101.5|28|33|Swept|20|24" }),
			"a swing between them, two ticks up, pairs with the first one as soon as it is confirmed");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.25m), Equal(i => i.EqualToleranceTicks = 0))).Count == 0, "no tolerance: a tick apart is not equal");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101m), Equal(i => i.EqualToleranceTicks = 0))).SequenceEqual(new[] { "EqualHighs|101|32|33|Swept|20|28" }),
			"no tolerance: the same price");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.25m, second: 30, length: 40), Equal(i => i.EqualLookbackBars = 10))).Contains("EqualHighs|101.25|34|35|Swept|20|30"),
			"10 bars apart with a 10-bar lookback");
		Check(KeyLevelKeys(RunHistorical(EqualHighsMarket(101.25m, second: 31, length: 40), Equal(i => i.EqualLookbackBars = 10))).Count == 0, "11 bars apart with a 10-bar lookback");

		// untouched, it counts until Equal level lookback bars after the second swing
		var aging = RunHistorical(EqualHighsMarket(101.25m, sweep: false, length: 45), Equal(i => i.EqualLookbackBars = 10));
		Check(KeyLevelKeys(aging).SequenceEqual(new[] { "EqualHighs|101.25|32|39|Expired|20|28" }), $"expiry: {string.Join("; ", KeyLevelKeys(aging))}");

		// equal highs at 101 in regular hours make the prior day high 101 too; one bar that evening
		// sweeps both, and the prior day names it although the equal highs were posted first
		IndicatorCandle Flat(int hour, int minute) => NyBar(13, hour, minute, 100, 100.5m, 99.5m, 100);
		IndicatorCandle Top(int hour, int minute) => NyBar(13, hour, minute, 100, 101, 99.5m, 100);
		var twoKinds = new List<IndicatorCandle>
		{
			Flat(9, 30), Flat(10, 0), Flat(10, 30), Top(11, 0), Flat(11, 30), Flat(12, 0), Flat(12, 30),
			Top(13, 0), Flat(13, 30), Flat(14, 0), Flat(14, 30), Flat(15, 0), Flat(15, 30),
			Flat(16, 0), Flat(17, 0), Flat(18, 0),
			NyBar(13, 19, 0, 100, 101.5m, 99.75m, 100.25m),
			Flat(20, 0), Flat(21, 0)
		};
		var both = RunHistorical(twoKinds, Equal(i => i.LevelPriorDay = true));
		Check(KeyLevelKeys(both).SequenceEqual(new[] { "EqualHighs|101|11|16|Swept|3|7", "PriorDayHigh|101|15|16|Swept|-1|-1", "PriorDayLow|99.5|15|-1|Fresh|-1|-1" }),
			$"two kinds at one price: {string.Join("; ", KeyLevelKeys(both))}");
		Check(Trades(both).Select(t => $"{t.EntryBar}|{t.Trigger}|{t.SweptLevel}").SequenceEqual(new[] { "16|KeySweep|PriorDayHigh@101@16" }),
			$"the prior day outranks the equal highs before it: {string.Join("; ", Trades(both).Select(t => $"{t.EntryBar} {t.Trigger} {t.SweptLevel}"))}");
	}

	// bar i trades most at 100: 100 + 10 * (i % 20) contracts, sold into the bids
	private static List<IndicatorCandle> PeakMarket(int count)
	{
		var bars = new List<IndicatorCandle>();

		for (var i = 0; i < count; i++)
		{
			var bar = Bar(100, 101, 99, 100);
			var peak = 100 + 10 * (i % 20);

			foreach (var price in new[] { 99m, 99.5m, 100m, 100.5m, 101m })
			{
				bar.Levels.Add(price == 100m
					? new PriceVolumeInfo { Price = price, Volume = peak, Bid = peak, Ask = 0 }
					: new PriceVolumeInfo { Price = price, Volume = 10, Bid = 5, Ask = 5 });
			}

			bars.Add(bar);
		}

		return bars;
	}

	private static void AdaptiveFills()
	{
		Action<FvgReactionLiquiditySweep> Adaptive(int top, int lookback) => i =>
		{
			i.FillSize = FvgReactionLiquiditySweep.FillSizeRule.TopOfRecentBars;
			i.FillTopPercent = top;
			i.FillLookbackBars = lookback;
		};

		// each value once per 20 bars: the top 10% of the last 20 is the 2nd biggest, 280
		var ind = RunHistorical(PeakMarket(61), Adaptive(10, 20));
		var bars = Fills(ind).Select(f => f.Bar).ToList();
		Check(bars.SequenceEqual(new[] { 38, 39, 58, 59 }), $"top 10%: {string.Join(",", bars)}");
		Check((decimal)IndicatorType.GetField("_fillThreshold", Private).GetValue(ind) == 280, "a big fill takes 280 now");

		bars = Fills(RunHistorical(PeakMarket(61), Adaptive(25, 20))).Select(f => f.Bar).ToList();
		Check(bars.SequenceEqual(new[] { 35, 36, 37, 38, 39, 55, 56, 57, 58, 59 }), $"top 25% (the 5th biggest, 250): {string.Join(",", bars)}");

		bars = Fills(RunHistorical(PeakMarket(61), Adaptive(10, 40))).Select(f => f.Bar).ToList();
		Check(!bars.Any(b => b < 20) && bars.Contains(59) && !bars.Contains(57), $"a longer lookback: {string.Join(",", bars)}");

		// bars without a footprint are not ranked: with the first ten empty, the size is still the
		// 2nd biggest of the 20 footprints in the window
		var gappy = PeakMarket(61);
		gappy.Take(10).ToList().ForEach(b => b.Levels.Clear());
		bars = Fills(RunHistorical(gappy, Adaptive(10, 20))).Select(f => f.Bar).ToList();
		Check(bars.SequenceEqual(new[] { 38, 39, 58, 59 }), $"empty bars left out: {string.Join(",", bars)}");

		// with the fixed size every bar of 150+ counts
		Check(Fills(RunHistorical(PeakMarket(61))).Count == 45, "fixed: 150 contracts");

		// an order-book fill of its own takes what a big fill takes now
		var live = LiveIndicator(PeakMarket(61), Adaptive(10, 20));
		live.MarketTime = new DateTime(2026, 3, 2, 15, 0, 0);
		Depth(live, false, 100.5m, 300);
		Print(live, 100.5m, 279, sell: false);
		Depth(live, false, 100.5m, 0);
		Check(Fills(live).Count(f => !f.FromFootprint) == 0, "279 traded against it: under 280");
		Depth(live, false, 100.75m, 300);
		Print(live, 100.75m, 280, sell: false);
		Depth(live, false, 100.75m, 0);
		Check(Fills(live).Count(f => !f.FromFootprint) == 1, "280 traded against it: a big fill of its own");

		// the panel says what a big fill takes
		live.FirstVisibleBarNumber = 0;
		live.LastVisibleBarNumber = live.Candles.Count - 1;
		var context = new RenderContext();
		live.HarnessRender(context);
		Check(context.Strings.Any(t => t.StartsWith("Fills ") && t.EndsWith(" · big ≥280")), "the panel shows the big-fill size");
	}

	private static void AdaptiveRestingOrders()
	{
		var ind = LiveIndicator(FvgSetup(), i =>
		{
			i.RestingSize = FvgReactionLiquiditySweep.RestingSizeRule.TimesTypicalLevel;
			i.RestingMultiplier = 5;
		});

		// fewer than five prices in the book: the fixed 70 still applies
		Depth(ind, true, 99m, 10);
		Depth(ind, true, 98.75m, 10);
		Depth(ind, true, 98.5m, 60);
		Check(Orders(ind).Count == 0, "a thin book: 60 is under 70");

		Depth(ind, false, 101m, 10);
		Depth(ind, true, 98.5m, 60);
		Check(Orders(ind).Count == 0, "four prices are still a thin book");

		Depth(ind, false, 101.25m, 10);
		Depth(ind, false, 101.5m, 10);
		Depth(ind, true, 98.25m, 55);
		var order = Orders(ind).Single();
		Check(order.Price == 98.25m && order.Threshold == 50, "the typical level is 10, so 55 is 5x of it");

		Depth(ind, true, 98.5m, 61);
		Check(Orders(ind).Count == 2, "the 60 comes back as 61 and is judged again");

		// the book fills up: a new order now takes 5x 40, the ones already there keep their size
		foreach (var (bid, price) in new[] { (true, 99m), (true, 98.75m), (false, 101m), (false, 101.25m) })
			Depth(ind, bid, price, 40);

		Depth(ind, false, 102m, 150);
		Check(!Orders(ind).Any(o => o.Price == 102m), "150 is under 5x the typical 40");
		Depth(ind, true, 98.25m, 52);
		Check(Orders(ind).Single(o => o.Price == 98.25m).State == "Active" && !Orders(ind).Single(o => o.Price == 98.25m).Leaving,
			"52 is still above the 50 it needed");
		Depth(ind, true, 98.25m, 45);
		Check(Orders(ind).Single(o => o.Price == 98.25m).Leaving, "under its own 50 it leaves");

		ind.FirstVisibleBarNumber = 0;
		ind.LastVisibleBarNumber = ind.Candles.Count - 1;
		var context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Any(t => t.StartsWith("Resting ≥200 (5× typical): ")), "the panel shows what a resting order takes now");

		// eight prices, 40 40 40 40 45 50 61 150: the median is halfway between 40 and 45
		Depth(ind, false, 101.5m, 50);
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Any(t => t.StartsWith("Resting ≥213 (5× typical): ")), "an even book: 5x 42.5, rounded up");
	}

	private static void ScoreboardCounts()
	{
		// some trades run out of bars: expired ones are left out
		var market = Generate(3000, 41);
		var ind = RunHistorical(market.Candles, i => i.MaxBarsInTrade = 30, market.SessionStarts);
		var closed = Trades(ind).Where(t => t.Outcome == "TakeProfit" || t.Outcome == "BreakEven" || t.Outcome == "StopLoss").ToList();
		var expired = Trades(ind).Count(t => t.Outcome == "Expired");
		var board = IndicatorType.GetMethod("BuildScoreboard", Private).Invoke(ind, null);
		Check((int)Get(board, "Closed") == closed.Count && closed.Count > 50 && expired > 5, $"{closed.Count} closed signals, {expired} expired");

		string Row(object r) => $"{Get(r, "Name")}|{Get(r, "Count")}|{Get(r, "Wins")}|{Get(r, "BreakEvens")}|{Get(r, "Losses")}|{Get(r, "Ticks")}";
		decimal TicksOf(TradeView t) => (t.ExitPrice - t.EntryPrice) / Tick * (t.IsLong ? 1 : -1);
		string Expected(string name, List<TradeView> group) =>
			$"{name}|{group.Count}|{group.Count(t => t.Outcome == "TakeProfit")}|{group.Count(t => t.Outcome == "BreakEven")}|{group.Count(t => t.Outcome == "StopLoss")}|{group.Sum(TicksOf)}";

		var labels = new Dictionary<string, string> { ["Fvg"] = "FVG", ["Sweep"] = "Sweep", ["SweepThenFvg"] = "Sweep+FVG", ["Fill"] = "Fill" };
		var order = new[] { "Fvg", "Sweep", "SweepThenFvg", "Fill" };
		var expectedTriggers = order.Where(k => closed.Any(t => t.Trigger == k)).Select(k => Expected(labels[k], closed.Where(t => t.Trigger == k).ToList())).ToList();
		var triggers = ((IList)Get(board, "Triggers")).Cast<object>().Select(Row).ToList();
		Check(triggers.SequenceEqual(expectedTriggers), $"per trigger: {string.Join("; ", triggers)} vs {string.Join("; ", expectedTriggers)}");

		// per pattern: every signal counts for each pattern it had; the ten most frequent
		var names = PatternDisplayNames();
		var groups = closed.SelectMany(t => t.CandlePatterns.Count == 0 ? new[] { "No pattern" } : t.CandlePatterns.Select(n => names[n]).ToArray(), (t, n) => (t, n))
			.GroupBy(x => x.n)
			.Select(g => (Name: g.Key, Trades: g.Select(x => x.t).ToList()))
			.OrderByDescending(g => g.Trades.Count).ThenBy(g => g.Name, StringComparer.Ordinal)
			.Take(10)
			.Select(g => Expected(g.Name, g.Trades))
			.ToList();
		var patterns = ((IList)Get(board, "Patterns")).Cast<object>().Select(Row).ToList();
		Check(patterns.SequenceEqual(groups), $"per pattern: {string.Join("; ", patterns.Take(3))} vs {string.Join("; ", groups.Take(3))}");

		// the odds check: signals by the TP chance on their label, and how many hit TP
		var edges = new[] { 0, 20, 30, 40, 50, 60, 101 };
		var expectedOdds = Enumerable.Range(0, 6)
			.Select(k => closed.Where(t => LabelPercentsOf(t)[0] >= edges[k] && LabelPercentsOf(t)[0] < edges[k + 1]).ToList())
			.Select((g, k) => $"{edges[k]}|{edges[k + 1]}|{g.Count}|{g.Count(t => t.Outcome == "TakeProfit")}")
			.Where(k => !k.Split('|')[2].Equals("0"))
			.ToList();
		var odds = ((IList)Get(board, "OddsCheck")).Cast<object>().Select(b => $"{Get(b, "From")}|{Get(b, "To")}|{Get(b, "Count")}|{Get(b, "Hits")}").ToList();
		Check(odds.SequenceEqual(expectedOdds) && odds.Count > 1, $"odds check: {string.Join("; ", odds)} vs {string.Join("; ", expectedOdds)}");

		// shown on hover of the statistics panel, instead of a tooltip, or kept open
		var last = market.Candles.Count - 1;
		ind.FirstVisibleBarNumber = last - 300;
		ind.LastVisibleBarNumber = last;
		var chart = (FakeChart)ind.ChartInfo;
		chart.FirstBar = last - 300;
		chart.TopPrice = market.Candles.Skip(last - 300).Max(c => c.High) + 40;
		var context = new RenderContext();
		ind.HarnessRender(context);
		var title = $"Scoreboard: {closed.Count} closed signals, hidden ones included";
		Check(!context.Strings.Contains(title), "no scoreboard until the panel is hovered");

		var panel = (Rectangle)IndicatorType.GetField("_lastPanel", Private).GetValue(ind);
		chart.MouseLocationInfo.LastPosition = new Point(panel.X + 5, panel.Y + 5);
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Contains(title) && context.Strings.Contains("Setup") && context.Strings.Contains("Pattern")
			&& context.Strings.Contains("Odds check (were the TP odds right?)") && context.Strings.Any(t => t.StartsWith("Said TP ")),
			"hovering the panel shows the scoreboard");
		Check(expectedTriggers.All(r => context.Strings.Contains(r.Split('|')[0])), "a row per trigger");

		chart.MouseLocationInfo.LastPosition = new Point(-100, -100);
		ind.ShowScoreboard = true;
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Contains(title), "kept open");
	}

	// every card drawn, left to right: side, setup, odds and EV
	private static List<(string Side, string Setup, string Odds)> CardsOf(RenderContext drawn)
	{
		var found = new List<(string Side, string Setup, string Odds)>();

		for (var i = 0; i + 2 < drawn.Strings.Count; i++)
		{
			if (drawn.Strings[i] == "BUY" || drawn.Strings[i] == "SHORT")
				found.Add((drawn.Strings[i], drawn.Strings[i + 1], drawn.Strings[i + 2]));
		}

		return found;
	}

	// "Hammer" -> "Hammer", "BullishEngulfing" -> "Bullish engulfing", as the indicator names them
	private static Dictionary<string, string> PatternDisplayNames()
	{
		var info = (Array)IndicatorType.GetField("CandlePatternInfo", PrivateStatic).GetValue(null);
		return info.Cast<object>().ToDictionary(
			x => x.GetType().GetField("Item1").GetValue(x).ToString(),
			x => (string)x.GetType().GetField("Item2").GetValue(x));
	}

	#endregion

	#region Fuzz

	// rich = the market must be varied enough to exercise every trigger and confirmation (a
	// narrow setup, like key-level sweeps only, gives few signals on a random walk)
	private static void FuzzHistorical(int seed, Action<FvgReactionLiquiditySweep> configure, bool rich = true)
	{
		var market = Generate(6000, seed);
		var ind = RunHistorical(market.Candles, configure, market.SessionStarts);
		var trades = Trades(ind);

		Check(trades.Count > (rich ? 30 : 3), $"fuzz produced only {trades.Count} trades");

		if (rich)
			Check(trades.Any(t => t.Outcome == "TakeProfit") && trades.Any(t => t.Outcome == "StopLoss"), "fuzz needs both outcomes");

		VerifyOutcomes(ind, market, trades, liveFromBar: int.MaxValue);
		VerifyInvariants(ind, trades, market.Candles.Count);
		VerifySignalsAgainstOracle(ind, market, trades, rich);

		var patterns = VerifyPatternsOnEveryBar(ind, market.Candles);

		// with the default shapes the market has every pattern somewhere
		if (configure == null)
		{
			var missing = PatternStrength.Where(n => !patterns.ContainsKey(n)).ToList();
			Check(missing.Count == 0, $"fuzz market never shows {string.Join(", ", missing)}");
		}
	}

	private static void FuzzFilters()
	{
		var market = Generate(6000, 5);

		foreach (var (name, configure, passes) in new (string, Action<FvgReactionLiquiditySweep>, Func<TradeView, bool>)[]
		{
			("min TP 30%", i => i.MinProbabilityPercent = 30, t => LabelPercentsOf(t)[0] >= 30),
			("min EV +4t", i => i.MinExpectedTicks = 4, t => Math.Round(t.ExpectedTicks, MidpointRounding.AwayFromZero) >= 4)
		})
		{
			var ind = RunHistorical(market.Candles, configure, market.SessionStarts);
			var trades = Trades(ind);

			Check(trades.Where(t => t.IsShown).All(passes), $"{name}: a shown signal fails the filter");
			Check(trades.Any(t => !t.IsShown && !passes(t)), $"{name}: the filter should hide some signals");
			Check((int)IndicatorType.GetField("_filtered", Private).GetValue(ind) == trades.Count(t => !t.IsShown), $"{name}: hidden count");

			// hidden signals still feed the model
			var settled = trades.Count(t => t.Outcome == "TakeProfit" || t.Outcome == "BreakEven" || t.Outcome == "StopLoss");
			Check(ModelResolved(ind) == settled, $"{name}: the model learns from hidden signals too");

			VerifyOutcomes(ind, market, trades, liveFromBar: int.MaxValue);
			VerifyInvariants(ind, trades, market.Candles.Count);
		}
	}

	private static void FuzzLiveMatchesHistory(Action<FvgReactionLiquiditySweep> configure = null, int seed = 19)
	{
		var market = Generate(4000, seed);
		const int historyBars = 2500;

		var historical = RunHistorical(market.Candles, configure, market.SessionStarts);

		// same market, but only the first 2500 bars are loaded; the rest streams in tick by tick,
		// with a made-up order book trading around it
		var live = NewIndicator(i =>
		{
			configure?.Invoke(i);
			i.UseAlerts = true;
			i.AlertOnTradeResult = true;
		});

		foreach (var s in market.SessionStarts)
			live.SessionStarts.Add(s);

		live.Candles.AddRange(market.Candles.Take(historyBars));
		live.HarnessRecalculate();

		for (var i = 0; i < historyBars; i++)
			live.HarnessCalculate(i);

		Check(live.Alerts.Count == 0, "no alerts while loading history");

		var book = new BookSimulator(live, seed: 3, start: new DateTime(2026, 3, 9, 14, 30, 0));

		for (var b = historyBars; b < market.Candles.Count; b++)
			StreamBar(live, market.Paths[b], market.Candles[b].Delta, market.Candles[b].Levels, book, market.Candles[b].Time);

		var liveTrades = Trades(live);
		var histTrades = Trades(historical);

		// signal decisions only use closed bars (and never the order book), so the tracked
		// signals must be identical
		var liveKeys = liveTrades.Select(SignalKey).ToList();
		var histKeys = histTrades.Select(SignalKey).ToList();
		Check(liveKeys.SequenceEqual(histKeys), $"live stream produced different signals ({liveKeys.Count} vs {histKeys.Count})");

		var liveZones = ZoneKeys(live);
		var histZones = ZoneKeys(historical);
		Check(liveZones.SequenceEqual(histZones), "live stream produced different FVG zones");
		Check(KeyLevelKeys(live).SequenceEqual(KeyLevelKeys(historical)), "live stream produced different key levels");

		// live, the order book can report a fill on the new bar before the bar before it closes, so
		// the fills are compared in bar order rather than in the order they were found
		string FillKey(FillView f) => $"{f.Bar}|{f.Price}|{f.Volume}|{f.BidsFilled}|{f.Reaction}|{f.ReactionBar}|{string.Join(",", f.ReactionPatterns)}";
		var liveFills = Fills(live).Where(f => f.FromFootprint).OrderBy(f => f.Bar).ThenBy(f => f.Price).Select(FillKey).ToList();
		var histFills = Fills(historical).OrderBy(f => f.Bar).ThenBy(f => f.Price).Select(FillKey).ToList();
		Check(liveFills.SequenceEqual(histFills), $"live stream produced different footprint fills ({liveFills.Count} vs {histFills.Count})");

		VerifyOutcomes(live, market, liveTrades, liveFromBar: historyBars);
		VerifyInvariants(live, liveTrades, market.Candles.Count);
		VerifySignalsAgainstOracle(live, market, liveTrades);
		book.Verify(live);

		// alerts: every shown signal and result that happened after the history load, nothing else
		var expectedSignals = liveTrades.Count(t => t.IsShown && t.EntryBar >= historyBars - 1);
		var expectedResults = liveTrades.Count(t => t.IsShown && t.Outcome != "Open" && t.ExitBar >= historyBars);
		var expectedMoves = liveTrades.Count(t => t.IsShown && t.BreakEvenActive && t.BreakEvenBar >= historyBars);
		var signalAlerts = live.Alerts.Count(a => a.StartsWith("BUY @") || a.StartsWith("SHORT @"));
		var moveAlerts = live.Alerts.Count(a => a.Contains("stop moved to"));
		var resultAlerts = live.Alerts.Count(a => a.Contains(" from ") && !a.Contains("stop moved to"));
		Check(signalAlerts == expectedSignals, $"signal alerts {signalAlerts}, expected {expectedSignals}");
		Check(resultAlerts == expectedResults, $"result alerts {resultAlerts}, expected {expectedResults}");
		Check(moveAlerts == expectedMoves, $"stop-moved alerts {moveAlerts}, expected {expectedMoves}");
		Check(expectedSignals > 0 && expectedResults > 0 && expectedMoves > 0, "live part should contain signals, results and stop moves");
	}

	private static void RecalculateIsDeterministic()
	{
		var market = Generate(3000, 31);
		var ind = RunHistorical(market.Candles, null, market.SessionStarts);
		var first = Trades(ind).Select(t => SignalKey(t) + $"|{t.Outcome}|{t.ExitBar}|{t.IsShown}|{t.TakeProfit:R}").ToList();
		var firstZones = ZoneKeys(ind);

		Recalculate(ind);

		var second = Trades(ind).Select(t => SignalKey(t) + $"|{t.Outcome}|{t.ExitBar}|{t.IsShown}|{t.TakeProfit:R}").ToList();
		Check(first.SequenceEqual(second) && firstZones.SequenceEqual(ZoneKeys(ind)), "recalculation changed the results");

		// re-sending an already closed bar must not touch it
		var signalBar = Trades(ind).Last(t => t.IsShown).EntryBar;
		var arrow = Series(ind, "_buySignal")[signalBar] + Series(ind, "_shortSignal")[signalBar];
		ind.HarnessCalculate(signalBar);
		var third = Trades(ind).Select(t => SignalKey(t) + $"|{t.Outcome}|{t.ExitBar}|{t.IsShown}|{t.TakeProfit:R}").ToList();
		Check(arrow != 0 && Series(ind, "_buySignal")[signalBar] + Series(ind, "_shortSignal")[signalBar] == arrow && first.SequenceEqual(third),
			"re-sent closed bar changed its signal");
	}

	private static void RenderSmoke()
	{
		var market = Generate(3000, 41);
		var ind = RunHistorical(market.Candles, null, market.SessionStarts);
		var chart = (FakeChart)ind.ChartInfo;
		var trades = Trades(ind);
		var last = market.Candles.Count - 1;
		var first = last - 400;

		ind.FirstVisibleBarNumber = first;
		ind.LastVisibleBarNumber = last;
		chart.FirstBar = first;
		chart.TopPrice = market.Candles.Skip(first).Max(c => c.High) + 40;

		ind.ShowReactionLabels = true;
		var context = new RenderContext();
		ind.HarnessRender(context);

		var visibleShown = trades.Where(t => t.IsShown && t.EntryBar >= first).OrderBy(t => t.EntryBar).ToList();
		Check(visibleShown.Count(t => t.Outcome != "Open") > 3, "render test needs visible closed signals");

		// every card, left to right: side, setup, odds and EV
		List<(string Side, string Setup, string Odds)> Cards(RenderContext drawn)
		{
			var found = new List<(string Side, string Setup, string Odds)>();

			for (var i = 0; i + 2 < drawn.Strings.Count; i++)
			{
				if (drawn.Strings[i] == "BUY" || drawn.Strings[i] == "SHORT")
					found.Add((drawn.Strings[i], drawn.Strings[i + 1], drawn.Strings[i + 2]));
			}

			return found;
		}

		(string, string, string) CardOf(TradeView t) => (t.IsLong ? "BUY" : "SHORT", SetupTextOf(t), OddsLineOf(t));

		// by default only the open trade keeps its card; the others shrink to their result
		var cards = Cards(context);
		var expectedCards = visibleShown.Where(t => t.Outcome == "Open").Select(CardOf).ToList();
		Check(cards.SequenceEqual(expectedCards), $"cards differ, first drawn: {cards.FirstOrDefault()} vs {expectedCards.FirstOrDefault()}");

		var chips = context.Strings.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^(TP|BE|SL|EXP) [+-]?\d+t$")).ToList();
		var expectedChips = visibleShown.Where(t => t.Outcome != "Open").Select(ResultChipOf).ToList();
		Check(chips.SequenceEqual(expectedChips), $"result chips differ: {string.Join(" ", chips.Take(5))} vs {string.Join(" ", expectedChips.Take(5))}");

		// closed trades only leave a faint box: TP side and SL side
		var closedInView = trades.Count(t => t.IsShown && t.Outcome != "Open" && t.EntryBar <= last && t.ExitBar >= first);
		Check(context.Operations.Count(o => o.Kind == "fill" && o.Color.A == 14) == 2 * closedInView, "faint boxes for the closed trades");
		Check(context.Operations.Count(o => o.Kind == "fill" && o.Color.A == 30) == 2 * visibleShown.Count(t => t.Outcome == "Open"),
			"full boxes for the open trade");
		Check(!context.Operations.Any(o => o.Kind == "line" && o.Dashed && o.Color.A == 160),
			"entry and trigger lines only for the open trade");
		var movedClosed = trades.Count(t => t.IsShown && t.Outcome != "Open" && t.BreakEvenActive && t.EntryBar <= last && t.ExitBar >= first);
		Check(movedClosed > 0 && context.Operations.Count(o => o.Kind == "line" && o.Color.R == 255 && o.Color.G == 179 && o.Color.B == 0 && o.Color.A == 90)
			== movedClosed, "closed trades keep a faint break-even line");

		var defaults = NewIndicator(null);
		Check(defaults.CompactClosedLabels && !defaults.ShowReactionLabels && !defaults.ShowUsedZones && defaults.ShowZoneMidline,
			"the clean defaults: compact closed labels, no reaction labels, no retired zones");

		// every card when closed trades keep theirs
		ind.CompactClosedLabels = false;
		var full = new RenderContext();
		ind.HarnessRender(full);
		ind.CompactClosedLabels = true;
		expectedCards = visibleShown.Select(CardOf).ToList();
		Check(Cards(full).SequenceEqual(expectedCards), $"full cards differ, first drawn: {Cards(full).FirstOrDefault()} vs {expectedCards.FirstOrDefault()}");
		Check(visibleShown.Any(t => t.CandlePatterns.Count == 1) && visibleShown.Any(t => t.CandlePatterns.Count > 1),
			"render test needs cards with one and with several patterns");

		// the panel
		Check(context.Strings.Contains("FVG · Sweep · Fill signals   TP 80t · SL 80t · BE +40t → +20t"), "panel title");
		Check(new[] { "Longs", "Shorts", "Total" }.All(context.Strings.Contains), "panel rows");
		Check(context.Strings.Any(s => s.StartsWith("Labelled EV>0: ") && s.Contains("EV≤0: ")), "track record line");
		var fills = Fills(ind);
		Check(context.Strings.Contains($"Fills {fills.Count}: {fills.Count(f => f.Reaction > 0)} bullish · {fills.Count(f => f.Reaction < 0)} bearish reactions · big ≥150"),
			"fills line");
		Check(context.Strings.Contains("Resting ≥70: 0 bids · 0 offers"), "no order book in history");

		// one bubble per visible fill, one dot per visible sweep, one triangle per visible reaction
		// that did not become a card
		// (solid once the reaction window is over, faint while it is open)
		var visibleFills = fills.Where(f => f.Bar >= first && f.Bar <= last).ToList();
		var decidedBubbles = context.Operations.Count(o => o.Kind == "ellipse" && o.Color.A == 150);
		var pendingBubbles = context.Operations.Count(o => o.Kind == "ellipse" && o.Color.A == 70);
		Check(visibleFills.Count > 0 && decidedBubbles == visibleFills.Count(f => f.Decided) && pendingBubbles == visibleFills.Count(f => !f.Decided),
			$"{decidedBubbles} + {pendingBubbles} bubbles for {visibleFills.Count} fills");

		var sweeps = (IList)IndicatorType.GetField("_sweeps", Private).GetValue(ind);
		var visibleSweeps = sweeps.Cast<object>().Where(s => (int)Get(s, "Bar") >= first && (int)Get(s, "SwingBar") <= last).ToList();
		var dots = context.Operations.Count(o => o.Kind == "ellipse" && o.Color.A == 230);
		Check(visibleSweeps.Count > 0 && dots == visibleSweeps.Count, $"{dots} sweep dots for {visibleSweeps.Count} sweeps");
		Check(visibleSweeps.All(sw => context.Operations.Any(o => o.Kind == "line" && o.Dashed
				&& o.From.X == chart.GetXByBar((int)Get(sw, "SwingBar"), false) && o.To.X == chart.GetXByBar((int)Get(sw, "Bar"), false)
				&& o.From.Y == chart.GetYByPrice((decimal)Get(sw, "Level"), false))),
			"each sweep line runs from the swing to the sweep bar");

		var reactions = Zones(ind).Where(z => z.State == "Used" && !z.SignalShown && z.EndBar >= first && z.EndBar <= last).ToList();
		var triangles = context.Operations.Count(o => o.Kind == "polygon");
		Check(reactions.Count > 0 && triangles == reactions.Count, $"{triangles} reaction markers for {reactions.Count} reactions");
		Check(reactions.All(z => context.Strings.Contains(PatternSummaryOf(z.ReactionPatterns))), "each reaction names its pattern");

		// active zones reach the right edge; used, filled and expired ones only show when asked for
		var active = Zones(ind).Where(z => z.State == "Active" && z.StartBar <= last).ToList();
		var right = chart.Region.Width;
		var reaching = context.Operations.Count(o => o.Kind == "fill" && o.Bounds.Right == right && o.Color.A == 40);
		Check(active.Count > 0 && reaching == active.Count, $"{reaching} full-width boxes for {active.Count} live zones");
		Check(!context.Operations.Any(o => o.Kind == "fill" && o.Color.A == 16), "no traces of retired zones by default");

		var retired = Zones(ind).Count(z => z.State != "Active" && z.StartBar <= last && z.EndBar >= first);
		ind.ShowUsedZones = true;
		var traces = new RenderContext();
		ind.HarnessRender(traces);
		ind.ShowUsedZones = false;
		Check(retired > 0 && traces.Operations.Count(o => o.Kind == "fill" && o.Color.A == 16) == retired, "a faint trace per retired zone when switched on");

		// hover a card (the first one is drawn first, so never nudged) -> its tooltip
		var target = visibleShown[0];
		var candle = market.Candles[target.EntryBar];
		var x = chart.GetXByBar(target.EntryBar, false);
		var y = target.IsLong
			? chart.GetYByPrice(candle.Low - 2 * Tick, false) + ind.LabelOffset + 4
			: chart.GetYByPrice(candle.High + 2 * Tick, false) - ind.LabelOffset - 4;
		chart.MouseLocationInfo.LastPosition = new Point(x, y);

		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("P(TP) ") && s.Contains("P(BE) ")), "tooltip drawn on hover");
		Check(context.Strings.Any(s => s.StartsWith($"Confirmations {target.Confirmations}/4: trend ")), "tooltip lists the four confirmations");

		// hover a bubble (cards and reaction labels off, so nothing sits on top of it)
		ind.ShowSignalLabels = false;
		ind.ShowReactionLabels = false;
		var reactionBars = new HashSet<int>(reactions.Select(z => z.EndBar));
		var bubbleFill = visibleFills.Where(f => !reactionBars.Contains(f.Bar)).OrderByDescending(f => f.Volume).First();
		chart.MouseLocationInfo.LastPosition = new Point(chart.GetXByBar(bubbleFill.Bar, false), chart.GetYByPrice(bubbleFill.Price, false));
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Any(s => s.EndsWith($" filled @ {bubbleFill.Price.ToString("0.00", CultureInfo.InvariantCulture)} on bar {bubbleFill.Bar}")),
			"fill tooltip on hover");

		// cluster mode + everything switched on/off still renders
		ind.ShowSignalLabels = true;
		chart.ChartVisualMode = ChartVisualModes.Clusters;
		ind.ShowStatsPanel = false;
		ind.ShowTradeLevels = false;
		ind.ShowFills = false;
		ind.ShowReactionLabels = false;
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(!context.Strings.Any(s => s.StartsWith("FVG · Sweep · Fill signals")), "panel hidden when switched off");
		Check(!context.Operations.Any(o => o.Kind == "ellipse" && o.Color.A == 150), "no bubbles when switched off");

		// an open shown trade gets a live line in the panel
		var openBars = FvgSetup();
		openBars.Add(HammerRetest());
		openBars.Add(Bar(103.5m, 108, 103, 107.5m));
		var openInd = RunHistorical(openBars);
		openInd.FirstVisibleBarNumber = 0;
		openInd.LastVisibleBarNumber = openBars.Count - 1;
		((FakeChart)openInd.ChartInfo).TopPrice = 130;
		context = new RenderContext();
		openInd.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("Live BUY +16t:  TP ") && s.Contains("% · BE ")), "live line for the open trade (+16 ticks)");
		Check(Cards(context).SequenceEqual(Trades(openInd).Select(CardOf)), "the open trade keeps its full card");
		Check(context.Operations.Count(o => o.Kind == "line" && o.Dashed && o.Color.A == 160) == 2, "the open trade's entry and trigger lines");
		Check(context.Strings.Any(s => s.StartsWith("TP ") && s.Contains("123.50")), "TP price tag on the open trade");
		Check(context.Strings.Any(s => s.StartsWith("SL ") && s.Contains("83.50")), "SL price tag before the stop moves");

		// once +40t is reached the stop tag moves to break-even and SL odds drop to 0
		var movedBars = FvgSetup();
		movedBars.Add(HammerRetest());
		movedBars.Add(Bar(103.5m, 114.5m, 103.25m, 114.5m));
		var movedInd = RunHistorical(movedBars);
		movedInd.FirstVisibleBarNumber = 0;
		movedInd.LastVisibleBarNumber = movedBars.Count - 1;
		((FakeChart)movedInd.ChartInfo).TopPrice = 130;
		context = new RenderContext();
		movedInd.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("Live BUY +44t, stop +20t:  TP ") && s.EndsWith("· SL 0%")), "live line after the stop moved");
		Check(context.Strings.Any(s => s.StartsWith("BE ") && s.Contains("108.50")), "stop tag moved to break-even");

		// the open trade's price tags stay on top of the bubbles
		var tagged = RunHistorical(FootprintRetest(101.75m, heavyOnBid: true));
		tagged.FirstVisibleBarNumber = 0;
		tagged.LastVisibleBarNumber = tagged.Candles.Count - 1;
		((FakeChart)tagged.ChartInfo).TopPrice = 130;
		context = new RenderContext();
		tagged.HarnessRender(context);
		var tpTag = context.Operations.FindIndex(o => o.Kind == "text" && o.Text.StartsWith("TP ") && !o.Text.Contains("%") && !o.Text.EndsWith("t"));
		var lastBubble = context.Operations.FindLastIndex(o => o.Kind == "ellipse");
		var card = context.Operations.FindIndex(o => o.Kind == "text" && o.Text == "BUY");
		Check(lastBubble >= 0 && card >= 0 && tpTag > lastBubble && tpTag > card, "price tags are drawn over the bubbles and the cards");

		// resting orders: bands with their size at the right edge, and a tooltip
		var bookInd = LiveIndicator(FvgSetup());
		bookInd.FirstVisibleBarNumber = 0;
		bookInd.LastVisibleBarNumber = bookInd.Candles.Count - 1;
		var bookChart = (FakeChart)bookInd.ChartInfo;
		Depth(bookInd, true, 103m, 120);
		Depth(bookInd, false, 106m, 95);
		Depth(bookInd, true, 102.75m, 60);

		// a filled order keeps a faint band without a tag; one still leaving the book shows nothing
		// (both far enough from the other tags that theirs would not be skipped as overlapping)
		bookInd.MarketTime = new DateTime(2026, 3, 2, 15, 0, 0);
		Depth(bookInd, true, 100m, 80);
		Print(bookInd, 100m, 80, sell: true);
		Depth(bookInd, true, 100m, 0);
		Depth(bookInd, false, 109m, 110);
		Depth(bookInd, false, 109m, 0);
		context = new RenderContext();
		bookInd.HarnessRender(context);
		Check(context.Strings.Contains("120") && context.Strings.Contains("95") && !context.Strings.Contains("60"), "size tags of the 70+ orders only");
		Check(!context.Strings.Contains("80") && !context.Strings.Contains("110") && !context.Strings.Contains("0"), "no tags for filled or leaving orders");
		Check(context.Operations.Count(o => o.Kind == "fill" && o.Color.A == 70 && o.Color.B == 245) == 1, "the filled bid keeps a faint band");
		Check(context.Strings.Contains("Resting ≥70: 1 bids · 1 offers · largest 120 bid @ 103.00"), "panel line for the order book");
		var tag = context.Operations.First(o => o.Kind == "fill" && o.CornerRadius == 3 && o.Bounds.Right > bookChart.Region.Width - 12);
		bookChart.MouseLocationInfo.LastPosition = new Point(tag.Bounds.X + 2, tag.Bounds.Y + 2);
		context = new RenderContext();
		bookInd.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("Resting bid 120 @ 103.00") || s.StartsWith("Resting offer 95 @ 106.00")), "order tooltip on hover");

		// the 120 bid's band runs under the panel: next to the panel it has a tooltip, over the
		// panel the scoreboard shows instead
		var bookPanel = (Rectangle)IndicatorType.GetField("_lastPanel", Private).GetValue(bookInd);
		bookChart.TopPrice = 103m + (bookPanel.Y + bookPanel.Height / 2 + 1) / 8m;
		var bandY = bookChart.GetYByPrice(103m) + 1;
		bookChart.MouseLocationInfo.LastPosition = new Point(bookPanel.X - 20, bandY);
		context = new RenderContext();
		bookInd.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("Resting bid 120 @ 103.00")), "the band's tooltip next to the panel");
		bookChart.MouseLocationInfo.LastPosition = new Point(bookPanel.X + 4, bandY);
		context = new RenderContext();
		bookInd.HarnessRender(context);
		Check(!context.Strings.Any(s => s.StartsWith("Resting bid 120 @ 103.00")) && context.Strings.Any(s => s.StartsWith("Scoreboard: ")),
			"over the panel: the scoreboard, not the tooltip of the band under it");

		// hover a reaction marker's label
		var reactionInd = RunHistorical(FvgBearishBars(), i =>
		{
			i.EnableShortSignals = false;
			i.ShowReactionLabels = true;
		});
		reactionInd.FirstVisibleBarNumber = 0;
		reactionInd.LastVisibleBarNumber = reactionInd.Candles.Count - 1;
		var reactionChart = (FakeChart)reactionInd.ChartInfo;
		context = new RenderContext();
		reactionInd.HarnessRender(context);
		var chip = context.Operations.FirstOrDefault(o => o.Kind == "text" && o.Text == "Bearish engulfing +1");
		Check(chip != null, "an unsignalled reaction shows its pattern");

		if (chip != null)
		{
			reactionChart.MouseLocationInfo.LastPosition = new Point(chip.From.X + 2, chip.From.Y + 2);
			context = new RenderContext();
			reactionInd.HarnessRender(context);
			Check(context.Strings.Any(s => s.StartsWith("Bearish reaction on bar 20: Bearish engulfing, Tweezer top")), "reaction tooltip on hover");
		}
	}

	// the bars of FvgBearishReactionAtBullishGap
	private static List<IndicatorCandle> FvgBearishBars()
	{
		var bars = FvgSetup();
		bars.Add(Bar(102.75m, 103.25m, 102, 103));
		bars.Add(Bar(103, 103.25m, 100, 100.25m, delta: -30));
		bars.Add(Bar(100.25m, 100.5m, 100, 100.25m));
		return bars;
	}

	// "TP +80t", as the chip of a closed trade shows it
	private static string ResultChipOf(TradeView t)
	{
		var badge = t.Outcome == "TakeProfit" ? "TP" : t.Outcome == "BreakEven" ? "BE" : t.Outcome == "StopLoss" ? "SL" : "EXP";
		var ticks = (int)Math.Round((t.ExitPrice - t.EntryPrice) / Tick * (t.IsLong ? 1 : -1), MidpointRounding.AwayFromZero);
		return $"{badge} {ticks.ToString("+0;-0;0", CultureInfo.InvariantCulture)}t";
	}

	// "FVG · Hammer", as a card shows it
	private static string SetupTextOf(TradeView t)
	{
		var trigger = t.Trigger == "Fvg" ? "FVG" : t.Trigger == "SweepThenFvg" ? "Sweep+FVG" : t.Trigger;
		var pattern = PatternSummaryOf(t.CandlePatterns);
		return pattern.Length == 0 ? trigger : $"{trigger} · {pattern}";
	}

	private static string PatternSummaryOf(List<string> patterns)
	{
		var patternType = IndicatorType.GetNestedType("CandlePattern", BindingFlags.NonPublic);
		var names = (List<string>)IndicatorType.GetMethod("PatternNames", PrivateStatic)
			.Invoke(null, new[] { Enum.Parse(patternType, patterns.Count == 0 ? "None" : string.Join(", ", patterns)) });
		return names.Count == 0 ? "" : names.Count == 1 ? names[0] : $"{names[0]} +{names.Count - 1}";
	}

	private static string OddsLineOf(TradeView t)
	{
		var p = LabelPercentsOf(t);
		var ev = ((int)Math.Round(t.ExpectedTicks, MidpointRounding.AwayFromZero)).ToString("+0;-0;0", CultureInfo.InvariantCulture);
		return t.HasBreakEven ? $"TP {p[0]}%  BE {p[1]}%  SL {p[2]}%   EV {ev}t" : $"TP {p[0]}%  SL {p[1]}%   EV {ev}t";
	}

	#endregion

	#region Oracles

	private sealed class OracleZone
	{
		public int Start;
		public int Confirmed;
		public decimal Top;
		public decimal Bottom;
		public bool Bull;
		public string State = "Active";
		public int End = -1;
		public int LastTouch = -1;
		public bool ReactionBull;
		public List<string> Patterns = new List<string>();
	}

	private sealed class OracleFill
	{
		public int Bar;
		public decimal Price;
		public decimal Volume;
		public bool BidsFilled;
		public int Reaction;
		public int ReactionBar = -1;
		public List<string> Patterns = new List<string>();
		public bool Decided;
	}

	private sealed class OracleLevel
	{
		public string Kind;
		public bool High;
		public int Rank;            // 0 prior day, 1 overnight, 2 opening range, 3 equal highs / lows
		public decimal Price;
		public int From;
		public int End = -1;
		public string State = "Fresh";
		public int First = -1;
		public int Second = -1;
	}

	// The key-level and signal-hours rules written again from the README, with New York time
	// taken from the system's time zone database rather than the indicator's own rule
	private sealed class OracleSessions
	{
		private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

		private readonly FvgReactionLiquiditySweep _ind;
		private readonly List<IndicatorCandle> _candles;
		private readonly List<int> _swingHighs = new List<int>();
		private readonly List<int> _swingLows = new List<int>();
		private DateTime _day = DateTime.MinValue;
		private decimal? _rthHigh;
		private decimal? _rthLow;
		private decimal? _priorHigh;
		private decimal? _priorLow;
		private decimal? _onHigh;
		private decimal? _onLow;
		private decimal? _orHigh;
		private decimal? _orLow;
		private bool _onPosted;
		private bool _orPosted;

		public OracleSessions(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles)
		{
			_ind = ind;
			_candles = candles;
		}

		public List<OracleLevel> Levels { get; } = new List<OracleLevel>();

		public static DateTime NewYorkTime(DateTime utc)
		{
			return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NewYork);
		}

		private bool Regular(TimeSpan time)
		{
			return time >= _ind.RegularHoursStart && time < _ind.RegularHoursEnd;
		}

		public bool InSignalHours(int b)
		{
			var time = NewYorkTime(_candles[b].Time).TimeOfDay;

			switch (_ind.SignalHours)
			{
				case FvgReactionLiquiditySweep.SignalHoursRule.AllHours:
					return true;

				case FvgReactionLiquiditySweep.SignalHoursRule.FirstTwoHours:
					return Regular(time) && time < _ind.RegularHoursStart.Add(TimeSpan.FromHours(2));

				case FvgReactionLiquiditySweep.SignalHoursRule.RegularHoursNoLunch:
					return Regular(time) && (time < new TimeSpan(11, 30, 0) || time >= new TimeSpan(13, 30, 0));

				default:
					return Regular(time);
			}
		}

		private void Post(string kind, bool high, int rank, decimal price, int from, int first = -1, int second = -1)
		{
			Levels.Add(new OracleLevel { Kind = kind, High = high, Rank = rank, Price = price, From = from, First = first, Second = second });
		}

		private static decimal? Max(decimal? a, decimal b) => a == null || b > a ? b : a;

		private static decimal? Min(decimal? a, decimal b) => a == null || b < a ? b : a;

		// the most important swept level: prior day, overnight, opening range, equal; then the
		// furthest; then the one that counted first
		private static OracleLevel Better(OracleLevel best, OracleLevel level)
		{
			if (best == null || level.Rank < best.Rank)
				return level;

			if (level.Rank > best.Rank)
				return best;

			if (level.Price != best.Price)
				return (level.High ? level.Price > best.Price : level.Price < best.Price) ? level : best;

			return level.From < best.From ? level : best;
		}

		// one closed bar: returns the level swept below (for a buy) and above (for a short)
		public (OracleLevel Low, OracleLevel High) Step(int b)
		{
			var c = _candles[b];
			var ny = NewYorkTime(c.Time);
			var day = ny.Hour >= 18 ? ny.Date.AddDays(1) : ny.Date;
			var time = ny.TimeOfDay;
			var regular = Regular(time);
			var openingEnd = _ind.RegularHoursStart.Add(TimeSpan.FromMinutes(_ind.OpeningRangeMinutes));

			if (day != _day)
			{
				if (_rthHigh != null)
				{
					_priorHigh = _rthHigh;
					_priorLow = _rthLow;
				}

				foreach (var level in Levels.Where(l => l.State == "Fresh" && l.Rank < 3))
				{
					level.State = "Expired";
					level.End = b - 1;
				}

				_day = day;
				_rthHigh = _rthLow = _onHigh = _onLow = _orHigh = _orLow = null;
				_onPosted = _orPosted = false;

				if (_ind.LevelPriorDay && _priorHigh != null)
				{
					Post("PriorDayHigh", true, 0, _priorHigh.Value, b);
					Post("PriorDayLow", false, 0, _priorLow.Value, b);
				}
			}

			if (regular && !_onPosted)
			{
				_onPosted = true;

				if (_ind.LevelOvernight && _onHigh != null)
				{
					Post("OvernightHigh", true, 1, _onHigh.Value, b);
					Post("OvernightLow", false, 1, _onLow.Value, b);
				}
			}

			if (regular && time >= openingEnd && !_orPosted)
			{
				_orPosted = true;

				if (_ind.LevelOpeningRange && _orHigh != null)
				{
					Post("OpeningRangeHigh", true, 2, _orHigh.Value, b);
					Post("OpeningRangeLow", false, 2, _orLow.Value, b);
				}
			}

			OracleLevel low = null;
			OracleLevel high = null;

			foreach (var level in Levels.Where(l => l.State == "Fresh" && l.From <= b).ToList())
			{
				if (!(level.High ? c.High > level.Price : c.Low < level.Price))
				{
					if (level.Rank == 3 && b - level.Second > _ind.EqualLookbackBars)
					{
						level.State = "Expired";
						level.End = b;
					}

					continue;
				}

				var back = level.High ? c.Close < level.Price : c.Close > level.Price;
				level.State = back ? "Swept" : "Broken";
				level.End = b;

				if (back && level.High)
					high = Better(high, level);
				else if (back)
					low = Better(low, level);
			}

			if (regular)
			{
				_rthHigh = Max(_rthHigh, c.High);
				_rthLow = Min(_rthLow, c.Low);

				if (time < openingEnd)
				{
					_orHigh = Max(_orHigh, c.High);
					_orLow = Min(_orLow, c.Low);
				}
			}
			else if (!_onPosted)
			{
				_onHigh = Max(_onHigh, c.High);
				_onLow = Min(_onLow, c.Low);
			}

			if (_ind.LevelEqual)
				Swings(b);

			return (low, high);
		}

		// a swing high is above the three bars before it and not below the three after it
		private void Swings(int b)
		{
			var pivot = b - 3;

			if (pivot < 3)
				return;

			foreach (var high in new[] { true, false })
			{
				decimal V(int i) => high ? _candles[i].High : _candles[i].Low;
				var swing = Enumerable.Range(pivot - 3, 7).Where(i => i != pivot)
					.All(i => high ? (i < pivot ? V(i) < V(pivot) : V(i) <= V(pivot)) : (i < pivot ? V(i) > V(pivot) : V(i) >= V(pivot)));

				if (!swing)
					continue;

				var swings = high ? _swingHighs : _swingLows;
				var tolerance = _ind.EqualToleranceTicks * Tick;
				var kind = high ? "EqualHighs" : "EqualLows";

				foreach (var first in Enumerable.Reverse(swings).ToList())
				{
					if (pivot - first > _ind.EqualLookbackBars)
						break;

					if (Math.Abs(V(first) - V(pivot)) > tolerance)
						continue;

					var level = high ? Math.Max(V(first), V(pivot)) : Math.Min(V(first), V(pivot));

					if (Enumerable.Range(first + 1, pivot - first - 1).Any(i => high ? V(i) > level : V(i) < level))
						continue;

					if (!Levels.Any(l => l.State == "Fresh" && l.Kind == kind && Math.Abs(l.Price - level) <= tolerance))
						Post(kind, high, 3, level, b + 1, first, pivot);

					break;
				}

				swings.Add(pivot);
			}

			_swingHighs.RemoveAll(p => pivot - p > _ind.EqualLookbackBars);
			_swingLows.RemoveAll(p => pivot - p > _ind.EqualLookbackBars);
		}
	}

	// what a big fill takes: Min filled volume, or the busiest price ranked at the Top share of
	// the recent bars' (nothing until 20 of them had a footprint)
	private static decimal OracleFillMinimum(FvgReactionLiquiditySweep ind, List<decimal> peaks)
	{
		if (ind.FillSize == FvgReactionLiquiditySweep.FillSizeRule.FixedContracts)
			return ind.FillMinVolume;

		var known = peaks.Where(p => p > 0).OrderByDescending(p => p).ToList();

		if (known.Count < 20)
			return 0;

		var rank = (int)Math.Ceiling(known.Count * ind.FillTopPercent / 100m);
		return known[Math.Max(1, rank) - 1];
	}

	// strongest first; twins share a rank - the order is part of the rules
	private static readonly string[] PatternStrength =
	{
		"BullishThreeLineStrike", "BearishThreeLineStrike", "MorningDojiStar", "EveningDojiStar", "MorningStar", "EveningStar",
		"ThreeOutsideUp", "ThreeOutsideDown", "ThreeWhiteSoldiers", "ThreeBlackCrows", "BullishOutsideReversal", "BearishOutsideReversal",
		"BullishEngulfing", "BearishEngulfing", "ThreeInsideUp", "ThreeInsideDown", "PiercingLine", "DarkCloudCover",
		"Hammer", "ShootingStar", "DragonflyDoji", "GravestoneDoji", "TweezerBottom", "TweezerTop",
		"BullishMarubozu", "BearishMarubozu", "BullishHarami", "BearishHarami", "InvertedHammer", "HangingMan"
	};

	private static readonly Dictionary<string, int> PatternCandles = new Dictionary<string, int>
	{
		["BullishThreeLineStrike"] = 4, ["BearishThreeLineStrike"] = 4,
		["MorningDojiStar"] = 3, ["EveningDojiStar"] = 3, ["MorningStar"] = 3, ["EveningStar"] = 3,
		["ThreeOutsideUp"] = 3, ["ThreeOutsideDown"] = 3, ["ThreeWhiteSoldiers"] = 3, ["ThreeBlackCrows"] = 3,
		["ThreeInsideUp"] = 3, ["ThreeInsideDown"] = 3,
		["BullishOutsideReversal"] = 2, ["BearishOutsideReversal"] = 2, ["BullishEngulfing"] = 2, ["BearishEngulfing"] = 2,
		["PiercingLine"] = 2, ["DarkCloudCover"] = 2, ["TweezerBottom"] = 2, ["TweezerTop"] = 2, ["BullishHarami"] = 2, ["BearishHarami"] = 2,
		["Hammer"] = 1, ["ShootingStar"] = 1, ["DragonflyDoji"] = 1, ["GravestoneDoji"] = 1, ["BullishMarubozu"] = 1, ["BearishMarubozu"] = 1,
		["InvertedHammer"] = 1, ["HangingMan"] = 1
	};

	private static int OracleRank(List<string> names)
	{
		return names.Count == 0 ? int.MaxValue : names.Min(n => Array.IndexOf(PatternStrength, n) / 2);
	}

	private static int OracleDecide(List<string> bull, List<string> bear)
	{
		var b = OracleRank(bull);
		var s = OracleRank(bear);
		return b < s ? 1 : s < b ? -1 : 0;
	}

	private static OracleZone BetterZone(OracleZone best, OracleZone zone)
	{
		if (best == null)
			return zone;

		var rank = OracleRank(zone.Patterns);
		var bestRank = OracleRank(best.Patterns);
		return rank < bestRank || (rank == bestRank && zone.Confirmed > best.Confirmed) ? zone : best;
	}

	private static OracleFill BetterFill(OracleFill best, OracleFill fill)
	{
		if (best == null)
			return fill;

		var rank = OracleRank(fill.Patterns);
		var bestRank = OracleRank(best.Patterns);

		if (rank != bestRank)
			return rank < bestRank ? fill : best;

		if (fill.Volume != best.Volume)
			return fill.Volume > best.Volume ? fill : best;

		return fill.Bar > best.Bar ? fill : best;
	}

	// Re-derives every gap, reaction, fill, sweep and signal from the raw bars and checks the
	// indicator's against them - including the hidden trigger series.
	private static void VerifySignalsAgainstOracle(FvgReactionLiquiditySweep ind, Market market, List<TradeView> trades, bool rich = true)
	{
		var candles = market.Candles;
		var mirrored = Mirror(candles);
		var zones = new List<OracleZone>();
		var fills = new List<OracleFill>();
		var fillByBar = new Dictionary<int, OracleFill>();
		var ema = new List<decimal>();
		var expected = new List<string>();
		var markers = new Dictionary<int, (bool BullReaction, bool BearReaction, bool SweptLows, bool SweptHighs)>();
		var lastLowSweep = -1;
		var lastHighSweep = -1;
		var lastLong = -1;
		var lastShort = -1;
		var minGap = Math.Max(ind.MinFvgTicks, 1) * Tick;
		var none = new List<string>();
		var sessions = new OracleSessions(ind, candles);
		var peaks = new List<decimal>();
		var keyMarkers = new Dictionary<int, (bool Low, bool High)>();
		OracleLevel lastLowKey = null;
		OracleLevel lastHighKey = null;
		var lastLowKeyBar = -1;
		var lastHighKeyBar = -1;

		List<string> P(int b, bool bullish, bool afterDecline) => OraclePatterns(ind, candles, mirrored, b, bullish, afterDecline);

		// the last bar is still forming, so it is never processed
		for (var b = 0; b < candles.Count - 1; b++)
		{
			var c = candles[b];
			ema.Add(b == 0 || ind.TrendEmaPeriod <= 0 ? c.Close : ema[b - 1] + 2m / (ind.TrendEmaPeriod + 1) * (c.Close - ema[b - 1]));

			// the price where the bar filled the most, if it stands out: at least Min filled volume,
			// or among the Top share of the busiest prices of the bars before it
			var best = c.Levels.Count > 0 ? c.Levels.OrderByDescending(l => l.Volume).ThenBy(l => l.Price).First() : null;
			var peak = best != null && best.Volume > 0 ? best.Volume : 0;
			var minimum = OracleFillMinimum(ind, peaks);
			peaks.Add(peak);

			if (peaks.Count > ind.FillLookbackBars)
				peaks.RemoveAt(0);

			if (peak > 0 && minimum > 0)
			{
				var average = c.Levels.Sum(l => l.Volume) / c.Levels.Count;

				if (best.Volume >= Math.Max(minimum, (decimal)ind.FillVolumeMultiplier * average))
				{
					var fill = new OracleFill { Bar = b, Price = best.Price, Volume = best.Volume, BidsFilled = best.Bid >= best.Ask };
					fills.Add(fill);
					fillByBar[b] = fill;
				}
			}

			// the first pattern closing away from a fill, within its window, is the reaction to it
			OracleFill bullFill = null;
			OracleFill bearFill = null;

			foreach (var fill in fills.Where(f => !f.Decided && f.Bar <= b))
			{
				var bull = c.Close > fill.Price ? P(b, true, fill.BidsFilled) : none;
				var bear = c.Close < fill.Price ? P(b, false, fill.BidsFilled) : none;
				var reaction = OracleDecide(bull, bear);

				if (reaction != 0)
				{
					fill.Reaction = reaction;
					fill.ReactionBar = b;
					fill.Patterns = reaction > 0 ? bull : bear;
					fill.Decided = true;

					if (reaction > 0)
						bullFill = BetterFill(bullFill, fill);
					else
						bearFill = BetterFill(bearFill, fill);
				}
				else if (b >= fill.Bar + ind.ReactionBars)
					fill.Decided = true;
			}

			// key levels follow every bar, warm-up included
			var (keyLow, keyHigh) = sessions.Step(b);
			keyMarkers[b] = (keyLow != null, keyHigh != null);

			if (keyLow != null)
			{
				lastLowKey = keyLow;
				lastLowKeyBar = b;
			}

			if (keyHigh != null)
			{
				lastHighKey = keyHigh;
				lastHighKeyBar = b;
			}

			if (b < ind.SwingLookback + 3)
				continue;

			var left = candles[b - 2];

			if (c.Low - left.High >= minGap)
				zones.Add(new OracleZone { Start = b - 1, Confirmed = b, Top = c.Low, Bottom = left.High, Bull = true });

			if (left.Low - c.High >= minGap)
				zones.Add(new OracleZone { Start = b - 1, Confirmed = b, Top = left.Low, Bottom = c.High, Bull = false });

			var window = candles.Skip(b - ind.SwingLookback).Take(ind.SwingLookback).ToList();
			var highest = window.Max(x => x.High);
			var lowest = window.Min(x => x.Low);
			var sweptHighs = c.High > highest && c.Close < highest;
			var sweptLows = c.Low < lowest && c.Close > lowest;

			// a gap reacts to a pattern that includes a candle that traded into it; price comes
			// down into a bullish gap and up into a bearish one
			OracleZone bullZone = null;
			OracleZone bearZone = null;

			foreach (var zone in zones.Where(z => z.State == "Active" && b > z.Confirmed))
			{
				if (c.Low <= zone.Top && c.High >= zone.Bottom)
					zone.LastTouch = b;

				if (zone.LastTouch >= 0)
				{
					var need = b - zone.LastTouch + 1;
					var bull = P(b, true, zone.Bull).Where(n => PatternCandles[n] >= need).ToList();
					var bear = P(b, false, zone.Bull).Where(n => PatternCandles[n] >= need).ToList();

					if (ind.RequireCloseThroughZone)
					{
						if (c.Close <= zone.Top)
							bull = none;

						if (c.Close >= zone.Bottom)
							bear = none;
					}

					var reaction = OracleDecide(bull, bear);

					if (reaction != 0)
					{
						zone.State = "Used";
						zone.End = b;
						zone.ReactionBull = reaction > 0;
						zone.Patterns = reaction > 0 ? bull : bear;

						if (reaction > 0)
							bullZone = BetterZone(bullZone, zone);
						else
							bearZone = BetterZone(bearZone, zone);

						continue;
					}
				}

				var middle = (zone.Top + zone.Bottom) / 2;
				var filled = ind.FvgFill == FvgReactionLiquiditySweep.FvgFillRule.CloseBeyond ? (zone.Bull ? c.Close < zone.Bottom : c.Close > zone.Top)
					: ind.FvgFill == FvgReactionLiquiditySweep.FvgFillRule.Middle ? (zone.Bull ? c.Low <= middle : c.High >= middle)
					: zone.Bull ? c.Low <= zone.Bottom : c.High >= zone.Top;

				if (filled)
				{
					zone.State = "Filled";
					zone.End = b;
				}
				else if (b - zone.Start > ind.MaxZoneAgeBars)
				{
					zone.State = "Expired";
					zone.End = b;
				}
			}

			markers[b] = (bullZone != null, bearZone != null, sweptLows, sweptHighs);

			if (sweptLows)
				lastLowSweep = b;

			if (sweptHighs)
				lastHighSweep = b;

			if (ind.ExpireAtSessionEnd && market.SessionStarts.Contains(b + 1))
				continue;

			if (!sessions.InSignalHours(b))
				continue;

			foreach (var isLong in new[] { true, false })
			{
				if (!(isLong ? ind.EnableBuySignals : ind.EnableShortSignals))
					continue;

				var zone = isLong ? bullZone : bearZone;
				var fill = isLong ? bullFill : bearFill;
				var sweepNow = isLong ? sweptLows : sweptHighs;
				var lastSweep = isLong ? lastLowSweep : lastHighSweep;
				var confluence = lastSweep >= 0 && b - lastSweep <= ind.ConfluenceBars;
				var keyNow = isLong ? keyLow : keyHigh;
				var lastKeyBar = isLong ? lastLowKeyBar : lastHighKeyBar;
				var keyBefore = lastKeyBar >= 0 && b - lastKeyBar <= ind.ConfluenceBars;
				var fvgTrigger = zone == null ? null : keyBefore ? "KeySweepThenFvg" : confluence ? "SweepThenFvg" : "Fvg";
				string trigger;

				switch (ind.SignalSource)
				{
					case FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly:
						trigger = fvgTrigger;
						break;

					case FvgReactionLiquiditySweep.SignalMode.LiquiditySweepOnly:
						trigger = keyNow != null ? "KeySweep" : sweepNow ? "Sweep" : null;
						break;

					case FvgReactionLiquiditySweep.SignalMode.SweepThenFvg:
						trigger = fvgTrigger == "SweepThenFvg" || fvgTrigger == "KeySweepThenFvg" ? fvgTrigger : null;
						break;

					case FvgReactionLiquiditySweep.SignalMode.FillReactionOnly:
						trigger = fill != null ? "Fill" : null;
						break;

					case FvgReactionLiquiditySweep.SignalMode.KeyLevelSweeps:
						trigger = fvgTrigger == "KeySweepThenFvg" ? fvgTrigger : keyNow != null ? "KeySweep" : null;
						break;

					default:
						trigger = fvgTrigger ?? (fill != null ? "Fill" : keyNow != null ? "KeySweep" : sweepNow ? "Sweep" : null);
						break;
				}

				if (trigger == null)
					continue;

				var lastSignal = isLong ? lastLong : lastShort;

				if (lastSignal >= 0 && b - lastSignal <= ind.SignalCooldownBars)
					continue;

				var patterns = trigger == "Fill" ? fill.Patterns : trigger == "Sweep" || trigger == "KeySweep" ? P(b, isLong, isLong) : zone.Patterns;
				var swept = trigger == "KeySweep" ? keyNow : trigger == "KeySweepThenFvg" ? (isLong ? lastLowKey : lastHighKey) : null;
				var withTrend = ind.TrendEmaPeriod > 0 && b >= ind.TrendEmaPeriod && (isLong ? c.Close > ema[b] : c.Close < ema[b]);
				var delta = isLong ? c.Delta > 0 : c.Delta < 0;
				var fillOk = OracleSupportingFill(candles, fillByBar, b, isLong);

				if ((ind.OnlyWithTrend && !withTrend) || (ind.RequireDeltaConfirmation && !delta) || (ind.RequireFillConfirmation && !fillOk)
					|| (ind.RequireCandlePattern && patterns.Count == 0))
					continue;

				var confirmations = (withTrend ? 1 : 0) + (delta ? 1 : 0) + (fillOk ? 1 : 0) + (patterns.Count > 0 ? 1 : 0);
				expected.Add($"{b}|{isLong}|{trigger}|{confirmations}|{c.Close}|{withTrend}|{delta}|{fillOk}|{string.Join(",", patterns.OrderBy(n => n, StringComparer.Ordinal))}"
					+ $"|{(swept == null ? "" : $"{swept.Kind}@{swept.Price}@{swept.End}")}");

				if (isLong)
					lastLong = b;
				else
					lastShort = b;
			}
		}

		var actual = trades
			.OrderBy(t => t.EntryBar).ThenBy(t => !t.IsLong)
			.Select(t => $"{t.EntryBar}|{t.IsLong}|{t.Trigger}|{t.Confirmations}|{t.EntryPrice}|{t.WithTrend}|{t.DeltaConfirms}|{t.FillConfirms}|{string.Join(",", t.CandlePatterns)}"
				+ $"|{t.SweptLevel}")
			.ToList();

		var firstDiff = Enumerable.Range(0, Math.Min(actual.Count, expected.Count)).FirstOrDefault(i => actual[i] != expected[i]);
		Check(actual.SequenceEqual(expected),
			$"signals differ from the oracle ({actual.Count} vs {expected.Count}); first difference: "
			+ $"{actual.ElementAtOrDefault(firstDiff)} vs {expected.ElementAtOrDefault(firstDiff)}");

		Check(!rich || ((ind.TrendEmaPeriod == 0 || trades.Any(t => t.WithTrend)) && trades.Any(t => t.DeltaConfirms) && trades.Any(t => t.FillConfirms)
			&& trades.Any(t => t.CandlePatterns.Count > 0)), "fuzz market should exercise every confirmation");

		var keyLevelsOn = ind.LevelPriorDay || ind.LevelOvernight || ind.LevelOpeningRange || ind.LevelEqual;

		if (ind.SignalSource == FvgReactionLiquiditySweep.SignalMode.AnyTrigger)
		{
			var triggers = keyLevelsOn ? new[] { "Fvg", "Sweep", "Fill", "SweepThenFvg", "KeySweep", "KeySweepThenFvg" } : new[] { "Fvg", "Sweep", "Fill", "SweepThenFvg" };
			var missingTriggers = triggers.Where(k => !trades.Any(t => t.Trigger == k)).ToList();
			Check(missingTriggers.Count == 0, $"fuzz market should exercise every trigger, missing {string.Join(", ", missingTriggers)}");
		}

		// every key level and how it ended
		var expectedLevels = sessions.Levels.Select(l => $"{l.Kind}|{l.Price}|{l.From}|{l.End}|{l.State}|{l.First}|{l.Second}")
			.OrderBy(k => k, StringComparer.Ordinal).ToList();
		var actualLevels = KeyLevelKeys(ind);
		var levelDiff = Enumerable.Range(0, Math.Min(actualLevels.Count, expectedLevels.Count)).FirstOrDefault(i => actualLevels[i] != expectedLevels[i]);
		Check(actualLevels.SequenceEqual(expectedLevels), $"key levels differ from the oracle ({actualLevels.Count} vs {expectedLevels.Count}): "
			+ $"{actualLevels.ElementAtOrDefault(levelDiff)} vs {expectedLevels.ElementAtOrDefault(levelDiff)}");

		if (keyLevelsOn)
		{
			var kinds = new[] { (ind.LevelPriorDay, "PriorDay"), (ind.LevelOvernight, "Overnight"), (ind.LevelOpeningRange, "OpeningRange"), (ind.LevelEqual, "Equal") }
				.Where(k => k.Item1).Select(k => k.Item2).ToList();
			var missingKinds = kinds.Where(k => !sessions.Levels.Any(l => l.Kind.StartsWith(k))).ToList();
			Check(missingKinds.Count == 0, $"fuzz market should post every kind of key level, missing {string.Join(", ", missingKinds)}");
			var states = new[] { "Swept", "Broken", "Expired" };
			Check(states.All(st => sessions.Levels.Any(l => l.State == st)),
				$"fuzz market should sweep, break and expire key levels: {string.Join(", ", sessions.Levels.GroupBy(l => l.State).Select(g => $"{g.Key} {g.Count()}"))}");
		}

		// every gap and how it ended
		string ZoneKey(OracleZone z) => $"{z.Start}|{z.Top}|{z.Bottom}|{z.Bull}|{z.State}|{z.End}|{z.LastTouch}|{z.State == "Used" && z.ReactionBull}|"
			+ string.Join(",", z.Patterns.OrderBy(n => n, StringComparer.Ordinal));
		var expectedZones = zones.Select(ZoneKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
		var actualZones = ZoneKeys(ind);
		var zoneDiff = Enumerable.Range(0, Math.Min(actualZones.Count, expectedZones.Count)).FirstOrDefault(i => actualZones[i] != expectedZones[i]);
		Check(actualZones.SequenceEqual(expectedZones), $"FVG zones differ from the oracle ({actualZones.Count} vs {expectedZones.Count}): "
			+ $"{actualZones.ElementAtOrDefault(zoneDiff)} vs {expectedZones.ElementAtOrDefault(zoneDiff)}");
		Check(zones.Any(z => z.State == "Used" && z.ReactionBull) && zones.Any(z => z.State == "Used" && !z.ReactionBull)
			&& zones.Any(z => z.State == "Filled") && zones.Any(z => z.State == "Expired") && zones.Any(z => z.State == "Active"),
			"fuzz market should use, fill and expire gaps");

		// every footprint fill and its reaction
		string FillKey(int bar, decimal price, decimal volume, bool bids, int reaction, int reactionBar, bool decided, IEnumerable<string> patterns) =>
			$"{bar}|{price}|{volume}|{bids}|{reaction}|{reactionBar}|{decided}|{string.Join(",", patterns.OrderBy(n => n, StringComparer.Ordinal))}";
		var expectedFills = fills.Select(f => FillKey(f.Bar, f.Price, f.Volume, f.BidsFilled, f.Reaction, f.ReactionBar, f.Decided, f.Patterns)).ToList();
		var actualFills = Fills(ind).Where(f => f.FromFootprint).OrderBy(f => f.Bar)
			.Select(f => FillKey(f.Bar, f.Price, f.Volume, f.BidsFilled, f.Reaction, f.ReactionBar, f.Decided, f.ReactionPatterns)).ToList();
		var fillDiff = Enumerable.Range(0, Math.Min(actualFills.Count, expectedFills.Count)).FirstOrDefault(i => actualFills[i] != expectedFills[i]);
		Check(actualFills.SequenceEqual(expectedFills), $"fills differ from the oracle ({actualFills.Count} vs {expectedFills.Count}): "
			+ $"{actualFills.ElementAtOrDefault(fillDiff)} vs {expectedFills.ElementAtOrDefault(fillDiff)}");
		Check(fills.Any(f => f.Reaction > 0) && fills.Any(f => f.Reaction < 0) && fills.Any(f => f.Decided && f.Reaction == 0),
			"fuzz market should have bullish, bearish and no reactions to fills");

		// the hidden trigger series
		var bullReactionSeries = Series(ind, "_bullReaction");
		var bearReactionSeries = Series(ind, "_bearReaction");
		var bullSweepSeries = Series(ind, "_bullSweep");
		var bearSweepSeries = Series(ind, "_bearSweep");
		var wrong = 0;

		for (var b = 0; b < candles.Count; b++)
		{
			markers.TryGetValue(b, out var m);
			keyMarkers.TryGetValue(b, out var k);
			var low = candles[b].Low - 2 * Tick;
			var high = candles[b].High + 2 * Tick;

			wrong += bullReactionSeries[b] != (m.BullReaction ? low : 0) ? 1 : 0;
			wrong += bullSweepSeries[b] != (m.SweptLows || k.Low ? low : 0) ? 1 : 0;
			wrong += bearReactionSeries[b] != (m.BearReaction ? high : 0) ? 1 : 0;
			wrong += bearSweepSeries[b] != (m.SweptHighs || k.High ? high : 0) ? 1 : 0;
		}

		Check(wrong == 0, $"{wrong} trigger series values differ from the oracle");
	}

	// a big fill of resting bids (buys) or offers (shorts) near that end of the signal bar or the bar before
	private static bool OracleSupportingFill(List<IndicatorCandle> candles, Dictionary<int, OracleFill> fillByBar, int bar, bool isLong)
	{
		for (var b = bar; b >= Math.Max(0, bar - 1); b--)
		{
			if (!fillByBar.TryGetValue(b, out var fill) || fill.BidsFilled != isLong)
				continue;

			var c = candles[b];
			var edge = (c.High - c.Low) * 0.35m;

			if (isLong ? fill.Price <= c.Low + edge : fill.Price >= c.High - edge)
				return true;
		}

		return false;
	}

	// The candles upside down around a pivot: a bearish pattern after a rally is exactly the
	// bullish one after a decline in this mirror, so the oracle only spells out bullish ones.
	private static List<IndicatorCandle> Mirror(List<IndicatorCandle> candles, decimal pivot = 100000)
	{
		return candles
			.Select(c => new IndicatorCandle { Open = pivot - c.Open, High = pivot - c.Low, Low = pivot - c.High, Close = pivot - c.Close, Time = c.Time })
			.ToList();
	}

	private static readonly Dictionary<string, string> BearishTwin = new Dictionary<string, string>
	{
		["Hammer"] = "ShootingStar",
		["DragonflyDoji"] = "GravestoneDoji",
		["InvertedHammer"] = "HangingMan",
		["BullishEngulfing"] = "BearishEngulfing",
		["PiercingLine"] = "DarkCloudCover",
		["BullishHarami"] = "BearishHarami",
		["TweezerBottom"] = "TweezerTop",
		["MorningStar"] = "EveningStar",
		["MorningDojiStar"] = "EveningDojiStar",
		["ThreeWhiteSoldiers"] = "ThreeBlackCrows",
		["BullishMarubozu"] = "BearishMarubozu",
		["ThreeInsideUp"] = "ThreeInsideDown",
		["ThreeOutsideUp"] = "ThreeOutsideDown",
		["BullishOutsideReversal"] = "BearishOutsideReversal",
		["BullishThreeLineStrike"] = "BearishThreeLineStrike"
	};

	// Independent re-implementation of the candlestick patterns completing on bar b that point
	// one way, as sorted pattern names. Bearish ones are the bullish definitions read on the
	// mirrored candles, where a rally becomes a decline.
	private static List<string> OraclePatterns(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles, List<IndicatorCandle> mirrored,
		int b, bool bullish, bool afterDecline)
	{
		var names = bullish
			? BullishPatterns(ind, candles, b, afterDecline)
			: BullishPatterns(ind, mirrored, b, !afterDecline).Select(n => BearishTwin[n]).ToList();

		names.Sort(StringComparer.Ordinal);
		return names;
	}

	private static List<string> BullishPatterns(FvgReactionLiquiditySweep ind, List<IndicatorCandle> k, int b, bool afterDecline)
	{
		var names = new List<string>();

		decimal Body(IndicatorCandle x) => Math.Abs(x.Close - x.Open);
		decimal BodyTop(IndicatorCandle x) => Math.Max(x.Open, x.Close);
		decimal BodyBottom(IndicatorCandle x) => Math.Min(x.Open, x.Close);
		decimal Middle(IndicatorCandle x) => (x.Open + x.Close) / 2;
		bool Up(IndicatorCandle x) => x.Close > x.Open;
		bool Down(IndicatorCandle x) => x.Close < x.Open;

		// long / small against the average body of the candles before the pattern's first one,
		// compared without dividing: body * n against the sum of those n bodies
		(int N, decimal Sum) Yardstick(int first)
		{
			var from = Math.Max(0, first - ind.PatternAverageBars);
			return (first - from, Enumerable.Range(from, first - from).Sum(i => Body(k[i])));
		}

		bool Long(IndicatorCandle x, (int N, decimal Sum) y) => Body(x) > 0 && Body(x) * y.N >= y.Sum;
		bool Small(IndicatorCandle x, (int N, decimal Sum) y) => Body(x) * y.N < y.Sum;

		var c = k[b];
		var range = c.High - c.Low;

		if (range > 0)
		{
			var upperWick = c.High - BodyTop(c);
			var lowerWick = BodyBottom(c) - c.Low;
			var wickRatio = (decimal)ind.PinBarWickRatio;

			// the hammer shapes only read bullish after a decline
			if (afterDecline && ind.PatternHammer && lowerWick >= wickRatio * Body(c) && upperWick * 10 <= range)
				names.Add(Body(c) * 10 <= range ? "DragonflyDoji" : "Hammer");

			if (afterDecline && ind.PatternInvertedHammer && upperWick >= wickRatio * Body(c) && lowerWick * 10 <= range)
				names.Add("InvertedHammer");

			if (ind.PatternMarubozu && Up(c) && Long(c, Yardstick(b)) && upperWick * 20 <= range && lowerWick * 20 <= range)
				names.Add("BullishMarubozu");
		}

		if (b >= 1)
		{
			var p = k[b - 1];
			var y = Yardstick(b - 1);

			if (ind.PatternEngulfing && Down(p) && Up(c) && c.Open <= p.Close && c.Close >= p.Open && Body(c) > Body(p) && Long(c, y))
				names.Add("BullishEngulfing");

			if (ind.PatternPiercingLine && Down(p) && Long(p, y) && Up(c) && c.Open <= p.Close && c.Close > Middle(p) && c.Close < p.Open)
				names.Add("PiercingLine");

			if (ind.PatternHarami && Down(p) && Long(p, y) && Up(c) && Small(c, y) && c.Open >= p.Close && c.Close <= p.Open)
				names.Add("BullishHarami");

			if (ind.PatternTweezers && Down(p) && Up(c) && Math.Abs(c.Low - p.Low) <= ind.TweezerToleranceTicks * Tick)
				names.Add("TweezerBottom");

			if (ind.PatternOutsideReversal && c.High > p.High && c.Low < p.Low && Up(c) && c.Close > p.High)
				names.Add("BullishOutsideReversal");
		}

		if (b >= 2)
		{
			var a = k[b - 2];
			var p = k[b - 1];
			var y = Yardstick(b - 2);

			if (ind.PatternStars && Down(a) && Long(a, y) && Small(p, y) && BodyTop(p) <= Middle(a) && Up(c) && Long(c, y) && c.Close > Middle(a))
				names.Add(Body(p) * 10 <= p.High - p.Low ? "MorningDojiStar" : "MorningStar");

			bool Soldier(IndicatorCandle x) => Up(x) && Long(x, y) && (x.High - x.Close) * 4 <= x.High - x.Low;
			bool OpensIn(IndicatorCandle x, IndicatorCandle before) => x.Open >= BodyBottom(before) && x.Open <= BodyTop(before);

			if (ind.PatternThreeSoldiers && Soldier(a) && Soldier(p) && Soldier(c) && OpensIn(p, a) && OpensIn(c, p)
				&& p.Close > a.Close && c.Close > p.Close)
				names.Add("ThreeWhiteSoldiers");

			// a bearish harami confirmed by a close above the first candle's open
			if (ind.PatternThreeInside && Down(a) && Long(a, y) && Small(p, y) && BodyTop(p) <= BodyTop(a) && BodyBottom(p) >= BodyBottom(a)
				&& Up(c) && c.Close > a.Open)
				names.Add("ThreeInsideUp");

			// a bullish engulfing confirmed by a higher close
			if (ind.PatternThreeOutside && Down(a) && Up(p) && p.Open <= a.Close && p.Close >= a.Open && Body(p) > Body(a) && Long(p, y)
				&& c.Close > p.Close)
				names.Add("ThreeOutsideUp");
		}

		if (b >= 3 && ind.PatternThreeLineStrike)
		{
			var x1 = k[b - 3];
			var x2 = k[b - 2];
			var x3 = k[b - 1];

			if (Down(x1) && Down(x2) && Down(x3) && x2.Close < x1.Close && x3.Close < x2.Close && Up(c) && c.Open <= x3.Close && c.Close > x1.Open)
				names.Add("BullishThreeLineStrike");
		}

		return names;
	}

	// every bar, each way and after either move: the indicator's pattern finder against the
	// oracle; returns how often each pattern was found
	private static Dictionary<string, int> VerifyPatternsOnEveryBar(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles)
	{
		var find = IndicatorType.GetMethod("FindCandlePatterns", Private);
		var mirrored = Mirror(candles);
		var counts = new Dictionary<string, int>();
		var wrong = 0;

		for (var b = 0; b < candles.Count; b++)
		{
			foreach (var (bullish, afterDecline) in new[] { (true, true), (true, false), (false, true), (false, false) })
			{
				var actual = PatternList(find.Invoke(ind, new object[] { b, bullish, afterDecline }));
				var expected = OraclePatterns(ind, candles, mirrored, b, bullish, afterDecline);

				if (!actual.SequenceEqual(expected) && wrong++ < 3)
				{
					Failures.Add($"bar {b} {(bullish ? "bullish" : "bearish")} after a {(afterDecline ? "decline" : "rally")}: "
						+ $"indicator [{string.Join(",", actual)}], oracle [{string.Join(",", expected)}]");
				}

				foreach (var name in actual)
					counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
			}
		}

		Check(wrong == 0, $"{wrong} bars whose candlestick patterns differ from the oracle");
		return counts;
	}

	private static void VerifyOutcomes(FvgReactionLiquiditySweep ind, Market market, List<TradeView> trades, int liveFromBar)
	{
		var last = market.Candles.Count - 1;
		var mismatches = 0;

		foreach (var t in trades)
		{
			var expected = Oracle(ind, market, t, last, liveFromBar);
			var ok = expected.Outcome == t.Outcome && expected.ExitBar == t.ExitBar && expected.BreakEvenBar == t.BreakEvenBar
				&& (t.Outcome == "Open" || expected.Exit == t.ExitPrice);

			if (ok)
				continue;

			if (mismatches++ < 5)
			{
				Failures.Add($"trade @{t.EntryBar} {(t.IsLong ? "L" : "S")}: indicator {t.Outcome}/{t.ExitBar}/BE {t.BreakEvenBar}/{t.ExitPrice}, "
					+ $"oracle {expected.Outcome}/{expected.ExitBar}/BE {expected.BreakEvenBar}/{expected.Exit}");
			}
		}

		Check(mismatches == 0, $"{mismatches} trade outcomes differ from the oracle");
		Check(!ind.ExpireAtSessionEnd || trades.All(t => !market.SessionStarts.Contains(t.EntryBar + 1)), "trade opened on a session's last bar");
	}

	// independent re-implementation of how a trade should settle, break-even included
	private static (string Outcome, int ExitBar, int BreakEvenBar, decimal Exit) Oracle(FvgReactionLiquiditySweep ind, Market market, TradeView t,
		int lastBar, int liveFromBar)
	{
		var active = false;
		var movedAt = -1;

		for (var b = t.EntryBar + 1; b <= lastBar; b++)
		{
			var step = b >= liveFromBar
				? OracleWalk(t, market.Paths[b], active)
				: OracleBar(t, market.Candles[b], active, ind.SameBarRule);

			if (step.Active && !active)
				movedAt = b;

			active = step.Active;

			if (step.Outcome != "Open")
				return (step.Outcome, b, movedAt, step.Exit);

			if (b == lastBar)
				break; // still forming - no end-of-bar expiry yet

			if (ind.MaxBarsInTrade > 0 && b - t.EntryBar >= ind.MaxBarsInTrade)
				return ("Expired", b, movedAt, market.Candles[b].Close);

			if (ind.ExpireAtSessionEnd && market.SessionStarts.Contains(b + 1))
				return ("Expired", b, movedAt, market.Candles[b].Close);
		}

		return ("Open", -1, movedAt, 0);
	}

	// one historical bar: open, both extremes in the order the rule picks, close
	private static (string Outcome, bool Active, decimal Exit) OracleBar(TradeView t, IndicatorCandle c, bool active,
		FvgReactionLiquiditySweep.SameBarHitRule rule)
	{
		var lowFirst = OracleWalk(t, new List<decimal> { c.Open, c.Low, c.High, c.Close }, active);
		var highFirst = OracleWalk(t, new List<decimal> { c.Open, c.High, c.Low, c.Close }, active);

		if (lowFirst.Outcome == highFirst.Outcome && lowFirst.Active == highFirst.Active)
			return lowFirst;

		if (rule == FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection)
			return c.Close >= c.Open ? lowFirst : highFirst;

		if (rule == FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst && c.High - c.Open != c.Open - c.Low)
			return c.High - c.Open < c.Open - c.Low ? highFirst : lowFirst;

		int Score((string Outcome, bool Active, decimal Exit) r) =>
			r.Outcome == "StopLoss" ? 0 : r.Outcome == "BreakEven" ? 2 : r.Outcome == "TakeProfit" ? 4 : r.Active ? 3 : 1;

		return Score(lowFirst) <= Score(highFirst) ? lowFirst : highFirst;
	}

	// price by price: TP, then the break-even trigger, then whichever stop applies
	private static (string Outcome, bool Active, decimal Exit) OracleWalk(TradeView t, List<decimal> prices, bool active)
	{
		foreach (var price in prices)
		{
			var inFavour = t.IsLong ? price - t.EntryPrice : t.EntryPrice - price;
			var reachedTrigger = t.HasBreakEven && inFavour >= Math.Abs(t.TriggerPrice - t.EntryPrice);

			if (inFavour >= Math.Abs(t.TakeProfitPrice - t.EntryPrice))
				return ("TakeProfit", active || reachedTrigger, t.TakeProfitPrice);

			active |= reachedTrigger;

			var stop = active ? t.BreakEvenPrice : t.StopLossPrice;

			if (t.IsLong ? price <= stop : price >= stop)
				return (active ? "BreakEven" : "StopLoss", active, stop);
		}

		return ("Open", active, 0);
	}

	private static void VerifyInvariants(FvgReactionLiquiditySweep ind, List<TradeView> trades, int barCount)
	{
		// 1) walk-forward: each estimate uses exactly the trades settled by its signal bar
		var lookAhead = 0;
		var prior = PriorOf(ind);
		double beTicks = ind.BreakEvenTriggerTicks > 0 && ind.BreakEvenTriggerTicks < ind.TakeProfitTicks
			? Math.Min(ind.BreakEvenStopTicks, ind.BreakEvenTriggerTicks - 1)
			: 0;

		foreach (var t in trades)
		{
			var settled = trades.Where(o => o.IsLong == t.IsLong && o.ExitBar >= 0 && o.ExitBar <= t.EntryBar
				&& (o.Outcome == "TakeProfit" || o.Outcome == "BreakEven" || o.Outcome == "StopLoss")).ToList();
			var sameTrigger = settled.Where(o => o.Trigger == t.Trigger).ToList();
			var sameSetup = sameTrigger.Where(o => o.Confirmations == t.Confirmations).ToList();

			bool Matches(List<TradeView> group, (int Wins, int BreakEvens, int Count) tally) =>
				tally.Count == group.Count && tally.Wins == group.Count(o => o.Outcome == "TakeProfit")
				&& tally.BreakEvens == group.Count(o => o.Outcome == "BreakEven");

			var ok = Matches(settled, t.Direction) && Matches(sameTrigger, t.TriggerTally) && Matches(sameSetup, t.Setup);

			double k = ind.ProbabilitySmoothing;
			var p = prior;

			foreach (var tally in new[] { t.Direction, t.TriggerTally, t.Setup })
			{
				double n = tally.Count + k;
				p = ((tally.Wins + k * p.Tp) / n, (tally.BreakEvens + k * p.Be) / n, (tally.Count - tally.Wins - tally.BreakEvens + k * p.Sl) / n);
			}

			var ev = p.Tp * ind.TakeProfitTicks + p.Be * beTicks - p.Sl * ind.StopLossTicks;
			ok &= Math.Abs(p.Tp - t.TakeProfit) < 1e-12 && Math.Abs(p.Be - t.BreakEven) < 1e-12 && Math.Abs(ev - t.ExpectedTicks) < 1e-9;
			ok &= t.TakeProfit > 0 && t.StopLoss > 0 && (beTicks > 0 || t.HasBreakEven || t.BreakEven == 0);

			if (!ok && lookAhead++ < 3)
				Failures.Add($"estimate of trade @{t.EntryBar} does not match the trades settled before it");
		}

		Check(lookAhead == 0, $"{lookAhead} estimates use data they could not have had");

		// break-even bookkeeping
		Check(trades.All(t => t.HasBreakEven || !t.BreakEvenActive), "break-even moved on a trade without it");
		Check(trades.Where(t => t.Outcome == "BreakEven").All(t => t.BreakEvenActive && t.BreakEvenBar <= t.ExitBar), "break-even exit without a stop move");
		Check(trades.Where(t => t.BreakEvenActive).All(t => t.BreakEvenBar > t.EntryBar), "stop moved on the signal bar");

		// 2) one position at a time
		if (ind.OneTradeAtATime)
		{
			var shown = trades.Where(t => t.IsShown).OrderBy(t => t.EntryBar).ToList();
			var overlaps = shown.Where((t, i) => i > 0 && (shown[i - 1].ExitBar < 0 || shown[i - 1].ExitBar > t.EntryBar)).Count();
			Check(overlaps == 0, $"{overlaps} shown trades overlap an open one");
		}

		// 3) cooldown between tracked signals in the same direction
		foreach (var side in new[] { true, false })
		{
			var bars = trades.Where(t => t.IsLong == side).Select(t => t.EntryBar).ToList();
			var tooClose = bars.Where((b, i) => i > 0 && b - bars[i - 1] <= ind.SignalCooldownBars).Count();
			Check(tooClose == 0, $"{tooClose} signals inside the cooldown");
		}

		// 4) arrows exactly on shown signals
		var buy = Series(ind, "_buySignal");
		var sell = Series(ind, "_shortSignal");
		var shownLong = new HashSet<int>(trades.Where(t => t.IsShown && t.IsLong).Select(t => t.EntryBar));
		var shownShort = new HashSet<int>(trades.Where(t => t.IsShown && !t.IsLong).Select(t => t.EntryBar));
		var badMarkers = 0;

		for (var b = 0; b < barCount; b++)
		{
			if ((buy[b] != 0) != shownLong.Contains(b) || (sell[b] != 0) != shownShort.Contains(b))
				badMarkers++;
		}

		Check(badMarkers == 0, $"{badMarkers} bars with wrong signal arrows");

		// 5) the panel matches the trade list
		int PanelInt(string name) => (int)IndicatorType.GetField(name, Private).GetValue(ind);

		var shownTrades = trades.Where(t => t.IsShown).ToList();
		Check(PanelInt("_longWins") == shownTrades.Count(t => t.IsLong && t.Outcome == "TakeProfit"), "long wins");
		Check(PanelInt("_longBreakEvens") == shownTrades.Count(t => t.IsLong && t.Outcome == "BreakEven"), "long break-evens");
		Check(PanelInt("_shortBreakEvens") == shownTrades.Count(t => !t.IsLong && t.Outcome == "BreakEven"), "short break-evens");
		Check(PanelInt("_longLosses") == shownTrades.Count(t => t.IsLong && t.Outcome == "StopLoss"), "long losses");
		Check(PanelInt("_shortWins") == shownTrades.Count(t => !t.IsLong && t.Outcome == "TakeProfit"), "short wins");
		Check(PanelInt("_shortLosses") == shownTrades.Count(t => !t.IsLong && t.Outcome == "StopLoss"), "short losses");
		Check(PanelInt("_expired") == shownTrades.Count(t => t.Outcome == "Expired"), "expired");
		Check(PanelInt("_filtered") == trades.Count(t => !t.IsShown), "filtered");
		var settledAll = trades.Where(t => t.Outcome == "TakeProfit" || t.Outcome == "BreakEven" || t.Outcome == "StopLoss").ToList();
		Check(ModelResolved(ind) == settledAll.Count, "model sample size");

		// track record: realised ticks of every settled signal, split by the sign of its labelled EV
		decimal Realised(TradeView t) => (t.ExitPrice - t.EntryPrice) / Tick * (t.IsLong ? 1 : -1);
		bool PositiveLabel(TradeView t) => Math.Round(t.ExpectedTicks, MidpointRounding.AwayFromZero) > 0;
		decimal PanelTicks(string name) => (decimal)IndicatorType.GetField(name, Private).GetValue(ind);
		Check(PanelInt("_positiveEvCount") == settledAll.Count(PositiveLabel)
			&& PanelInt("_negativeEvCount") == settledAll.Count(t => !PositiveLabel(t))
			&& PanelTicks("_positiveEvTicks") == settledAll.Where(PositiveLabel).Sum(Realised)
			&& PanelTicks("_negativeEvTicks") == settledAll.Where(t => !PositiveLabel(t)).Sum(Realised), "track record counters");

		var net = (decimal)IndicatorType.GetField("_longNetTicks", Private).GetValue(ind)
			+ (decimal)IndicatorType.GetField("_shortNetTicks", Private).GetValue(ind);
		var expectedNet = shownTrades.Where(t => t.Outcome != "Open")
			.Sum(t => (t.ExitPrice - t.EntryPrice) / Tick * (t.IsLong ? 1 : -1));
		Check(net == expectedNet, $"net ticks {net} vs {expectedNet}");
		Check(trades.Where(t => t.Outcome == "TakeProfit").All(t => Realised(t) == ind.TakeProfitTicks), "a TP is worth exactly +TP ticks");
		Check(trades.Where(t => t.Outcome == "BreakEven").All(t => Realised(t) == (decimal)beTicks), "a break-even exit is worth exactly the offset");
		Check(trades.Where(t => t.Outcome == "StopLoss").All(t => Realised(t) == -ind.StopLossTicks), "an SL is worth exactly -SL ticks");
	}

	// A made-up order book around the streamed price: eight levels each side at random sizes,
	// eaten by the ticks that trade through them (the book sometimes reports it before the
	// prints), pulled, shrunk or joined at random, and scrolling out of view at the far end.
	// It keeps its own record of every order of 70+ by the rules the indicator should follow -
	// the oracle for the order book.
	private sealed class BookSimulator
	{
		private const int Levels = 8;
		private const decimal Big = 70;

		private readonly FvgReactionLiquiditySweep _ind;
		private readonly Random _rng;
		private readonly SortedDictionary<decimal, decimal> _bids = new SortedDictionary<decimal, decimal>();
		private readonly SortedDictionary<decimal, decimal> _asks = new SortedDictionary<decimal, decimal>();
		private readonly Dictionary<(decimal Price, bool Bid), decimal> _traded = new Dictionary<(decimal Price, bool Bid), decimal>();
		private readonly Dictionary<(decimal Price, bool Bid), Tracked> _open = new Dictionary<(decimal Price, bool Bid), Tracked>();
		private readonly List<Tracked> _ended = new List<Tracked>();
		private DateTime _time;
		private decimal _last;
		private int _bar;

		public BookSimulator(FvgReactionLiquiditySweep ind, int seed, DateTime start)
		{
			_ind = ind;
			_rng = new Random(seed);
			_time = start;
			ind.MarketTime = start;
		}

		private sealed class Tracked
		{
			public decimal Price;
			public bool Bid;
			public decimal Max;
			public decimal TradedAtStart;
			public int FirstBar;
			public bool Leaving;
			public DateTime LeftAt;
			public bool AtEdge;
			public int EndBar = -1;
			public string State = "Active";
			public decimal Traded;
		}

		// what happens in the book with one tick of the bar being streamed
		public void OnTick(decimal price, int bar)
		{
			_bar = bar;
			_time = _time.AddMilliseconds(_rng.Next(100, 900));
			_ind.MarketTime = _time;

			if (_last != 0)
			{
				// a move eats every level it trades through
				if (price < _last)
				{
					foreach (var p in _bids.Keys.Where(p => p >= price).Reverse().ToList())
						Consume(p, true);
				}
				else if (price > _last)
				{
					foreach (var p in _asks.Keys.Where(p => p <= price).ToList())
						Consume(p, false);
				}
				else if (_rng.NextDouble() < 0.4)
					NibbleTouch();
			}

			_last = price;
			Refill();
			Churn();
		}

		private void Consume(decimal price, bool bid)
		{
			var size = (bid ? _bids : _asks)[price];

			if (_rng.NextDouble() < 0.3)
			{
				SetLevel(price, bid, 0);
				Print(price, size, bid);
			}
			else
			{
				Print(price, size, bid);
				SetLevel(price, bid, 0);
			}
		}

		// part of the best bid or offer trades while price stays put
		private void NibbleTouch()
		{
			var bid = _rng.Next(2) == 0;
			var book = bid ? _bids : _asks;

			if (book.Count == 0)
				return;

			var price = bid ? book.Keys.Last() : book.Keys.First();
			var size = book[price];
			var part = Math.Max(1, Math.Round(size * (decimal)_rng.NextDouble()));
			Print(price, part, bid);
			SetLevel(price, bid, size - part);
		}

		// eight levels each side of the last price; the far ones leave view, deepest first
		private void Refill()
		{
			for (var i = 1; i <= Levels; i++)
			{
				if (!_bids.ContainsKey(_last - i * Tick))
					SetLevel(_last - i * Tick, true, RandomSize());

				if (!_asks.ContainsKey(_last + i * Tick))
					SetLevel(_last + i * Tick, false, RandomSize());
			}

			foreach (var p in _bids.Keys.Where(p => p < _last - Levels * Tick).ToList())
				SetLevel(p, true, 0);

			foreach (var p in _asks.Keys.Where(p => p > _last + Levels * Tick).Reverse().ToList())
				SetLevel(p, false, 0);
		}

		// orders pulled, shrunk or joining without any trade
		private void Churn()
		{
			var roll = _rng.NextDouble();
			var bid = _rng.Next(2) == 0;
			var book = bid ? _bids : _asks;
			var big = book.Where(kv => kv.Value >= Big).Select(kv => kv.Key).ToList();

			if (roll < 0.05 && big.Count > 0)
				SetLevel(big[_rng.Next(big.Count)], bid, 0);
			else if (roll < 0.08 && big.Count > 0)
				SetLevel(big[_rng.Next(big.Count)], bid, _rng.Next(5, 60));
			else if (roll < 0.11 && book.Count > 0)
			{
				var keys = book.Keys.ToList();
				SetLevel(keys[_rng.Next(keys.Count)], bid, _rng.Next(70, 300));
			}
		}

		private decimal RandomSize()
		{
			return _rng.NextDouble() < 0.15 ? _rng.Next(70, 300) : _rng.Next(5, 60);
		}

		private void Print(decimal price, decimal volume, bool intoBids)
		{
			_ind.HarnessTrade(new MarketDataArg
			{
				Price = price,
				Volume = volume,
				DataType = MarketDataType.Trade,
				Direction = intoBids ? TradeDirection.Sell : TradeDirection.Buy,
				Time = _time
			});

			_traded[(price, intoBids)] = TradedAgainst(price, intoBids) + volume;
			Settle();
		}

		private void SetLevel(decimal price, bool bid, decimal volume)
		{
			var book = bid ? _bids : _asks;

			if (volume > 0)
				book[price] = volume;
			else
				book.Remove(price);

			_ind.HarnessDepth(new MarketDataArg { Price = price, Volume = volume, DataType = bid ? MarketDataType.Bid : MarketDataType.Ask, Time = _time });

			_open.TryGetValue((price, bid), out var order);

			if (volume >= Big)
			{
				if (order == null)
				{
					order = new Tracked { Price = price, Bid = bid, FirstBar = _bar, TradedAtStart = TradedAgainst(price, bid) };
					_open[(price, bid)] = order;
				}

				order.Leaving = false;
				order.EndBar = -1;
				order.Max = Math.Max(order.Max, volume);
			}
			else if (order != null && !order.Leaving)
			{
				order.Leaving = true;
				order.LeftAt = _time;
				order.EndBar = _bar;
				order.AtEdge = volume == 0 && (bid ? !_bids.Keys.Any(p => p < price) : !_asks.Keys.Any(p => p > price));
			}

			Settle();
		}

		// the rule under test, written again: filled once half its size traded against it,
		// otherwise pulled (or out of view) two seconds after it left the book
		public void Settle()
		{
			foreach (var order in _open.Values.Where(o => o.Leaving).ToList())
			{
				order.Traded = TradedAgainst(order.Price, order.Bid) - order.TradedAtStart;
				var filled = order.Traded >= order.Max / 2;

				if (!filled && (_time - order.LeftAt).TotalSeconds < 2)
					continue;

				order.State = filled ? "Filled" : order.AtEdge ? "OutOfView" : "Pulled";
				_open.Remove((order.Price, order.Bid));
				_ended.Add(order);
			}
		}

		private decimal TradedAgainst(decimal price, bool bid)
		{
			return _traded.TryGetValue((price, bid), out var volume) ? volume : 0;
		}

		public void Verify(FvgReactionLiquiditySweep ind)
		{
			string Key(decimal price, bool bid, string state, decimal max, int first, int end) => $"{price}|{bid}|{state}|{max}|{first}|{end}";

			var expected = _ended.Select(o => Key(o.Price, o.Bid, o.State, o.Max, o.FirstBar, o.EndBar))
				.Concat(_open.Values.Select(o => Key(o.Price, o.Bid, o.Leaving ? "Leaving" : "Active", o.Max, o.FirstBar, o.EndBar)))
				.OrderBy(k => k, StringComparer.Ordinal)
				.ToList();

			var actual = Orders(ind)
				.Select(o => Key(o.Price, o.IsBid, o.State == "Active" && o.Leaving ? "Leaving" : o.State, o.MaxVolume, o.FirstBar, o.EndBar))
				.OrderBy(k => k, StringComparer.Ordinal)
				.ToList();

			var diff = Enumerable.Range(0, Math.Min(actual.Count, expected.Count)).FirstOrDefault(i => actual[i] != expected[i]);
			Check(actual.SequenceEqual(expected), $"order book: {actual.Count} orders vs {expected.Count} expected; first difference "
				+ $"{actual.ElementAtOrDefault(diff)} vs {expected.ElementAtOrDefault(diff)}");

			var states = _ended.GroupBy(o => o.State).ToDictionary(g => g.Key, g => g.Count());
			Check(new[] { "Filled", "Pulled", "OutOfView" }.All(states.ContainsKey),
				$"the made-up book should fill, pull and scroll away orders: {string.Join(", ", states.Select(kv => $"{kv.Key} {kv.Value}"))}");

			// the orders filled at one bar, price and side mark the footprint fill there with the
			// biggest of them; without one, they are a fill of their own if at least 150 traded
			// against one of them, and no fill at all otherwise
			var fills = Fills(ind);
			var wrong = new List<string>();
			var smallOnly = 0;

			foreach (var group in _ended.Where(o => o.State == "Filled").GroupBy(o => (o.EndBar, o.Price, o.Bid)))
			{
				var here = fills.Where(f => f.Bar == group.Key.EndBar && f.Price == group.Key.Price && f.BidsFilled == group.Key.Bid).ToList();
				var biggest = group.Max(o => o.Max);
				var footprint = here.Where(f => f.FromFootprint).ToList();
				var own = here.Where(f => !f.FromFootprint).ToList();
				bool ok;

				if (footprint.Count > 0)
					ok = footprint.Count == 1 && own.Count == 0 && footprint[0].RestingSize == biggest;
				else if (group.Any(o => o.Traded >= 150))
				{
					// its volume: what traded against the first big one and every one after it
					var volume = group.SkipWhile(o => o.Traded < 150).Sum(o => o.Traded);
					ok = own.Count == 1 && own[0].RestingSize == biggest && own[0].Volume == volume;
				}
				else
				{
					ok = here.Count == 0;
					smallOnly++;
				}

				if (!ok)
					wrong.Add($"{group.Key}: {here.Count} fills");
			}

			Check(wrong.Count == 0, $"{wrong.Count} filled prices shown wrong, e.g. {wrong.FirstOrDefault()}");

			// and nothing else carries the order book's mark
			var unexplained = fills.Count(f => (!f.FromFootprint || f.RestingSize > 0)
				&& !_ended.Any(o => o.State == "Filled" && o.EndBar == f.Bar && o.Price == f.Price && o.Bid == f.BidsFilled));
			Check(unexplained == 0, $"{unexplained} fills marked by the order book without a filled order");
			Check(fills.Any(f => !f.FromFootprint) && fills.Any(f => f.FromFootprint && f.RestingSize > 0) && smallOnly > 0,
				"order-book fills should show on their own, on footprint fills, and not at all when small");
		}
	}

	#endregion

	#region Market generator

	private sealed class Market
	{
		public List<IndicatorCandle> Candles { get; } = new List<IndicatorCandle>();
		public List<List<decimal>> Paths { get; } = new List<List<decimal>>();
		public HashSet<int> SessionStarts { get; } = new HashSet<int>();
	}

	// random walk in ticks with trend regimes, impulse bars (-> FVGs), wick spikes
	// (-> sweeps), session gaps and a footprint with occasional one-sided heavy levels
	private static Market Generate(int barCount, int seed)
	{
		var rng = new Random(seed);
		var market = new Market();
		var price = 15000m;
		var drift = 0.0;
		var time = new DateTime(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

		for (var b = 0; b < barCount; b++)
		{
			if (b > 0 && b % 390 == 0)
			{
				market.SessionStarts.Add(b);
				price += rng.Next(-40, 41) * Tick; // overnight gap
			}

			if (rng.NextDouble() < 0.03)
				drift = (rng.NextDouble() - 0.5) * 0.5;

			var path = new List<decimal> { price };
			var steps = rng.Next(12, 60);
			var impulse = rng.NextDouble() < 0.07 ? (rng.Next(2) == 0 ? -1 : 1) : 0;

			for (var s = 0; s < steps; s++)
			{
				var up = rng.NextDouble() < 0.5 + drift;
				var size = impulse != 0 ? rng.Next(1, 4) : rng.Next(1, 3);
				var dir = impulse != 0 && rng.NextDouble() < 0.8 ? impulse : up ? 1 : -1;
				price += dir * size * Tick;
				path.Add(price);
			}

			if (rng.NextDouble() < 0.08)
			{
				// wick spike that comes back
				var at = rng.Next(1, path.Count - 1);
				var spike = (rng.Next(2) == 0 ? -1 : 1) * rng.Next(6, 30) * Tick;
				path.Insert(at, path[at] + spike);
			}

			var candle = new IndicatorCandle
			{
				Open = path[0],
				High = path.Max(),
				Low = path.Min(),
				Close = path[path.Count - 1],
				Time = time.AddMinutes(b)
			};

			for (var p = candle.Low; p <= candle.High; p += Tick)
			{
				decimal volume = rng.Next(5, 60);
				var askShare = (decimal)(0.3 + rng.NextDouble() * 0.4);

				if (rng.NextDouble() < 0.05)
				{
					volume = rng.Next(200, 900);
					askShare = rng.Next(2) == 0 ? (decimal)(0.72 + rng.NextDouble() * 0.2) : (decimal)(0.08 + rng.NextDouble() * 0.2);
				}

				var ask = Math.Round(volume * askShare);
				candle.Levels.Add(new PriceVolumeInfo { Price = p, Volume = volume, Ask = ask, Bid = volume - ask });
			}

			candle.Delta = candle.Levels.Sum(l => l.Ask - l.Bid);
			market.Candles.Add(candle);
			market.Paths.Add(path);
		}

		return market;
	}

	#endregion

	#region Harness

	private sealed class FakeChart : IChart
	{
		private readonly Container _container = new Container();

		public int FirstBar { get; set; }
		public decimal TopPrice { get; set; } = 130;
		public ChartVisualModes ChartVisualMode { get; set; } = ChartVisualModes.Candles;
		public IChartContainer PriceChartContainer => _container;
		public Rectangle Region => _container.Region;
		public MouseLocationInfo MouseLocationInfo { get; } = new MouseLocationInfo();

		public int GetXByBar(int bar, bool isStartOfBar = true)
		{
			return (bar - FirstBar) * 8 + (isStartOfBar ? 0 : 4);
		}

		public int GetYByPrice(decimal price, bool isStartOnPriceLevel = true)
		{
			return (int)((TopPrice - price) / Tick * 2) - (isStartOnPriceLevel ? 1 : 0);
		}

		public string GetPriceString(decimal price)
		{
			return price.ToString("0.00", CultureInfo.InvariantCulture);
		}

		private sealed class Container : IChartContainer
		{
			public Rectangle Region => new Rectangle(0, 0, 3400, 1200);
			public decimal BarsWidth => 6;
			public decimal BarSpacing => 2;
			public decimal PriceRowHeight => 2;
		}
	}

	private sealed class TradeView
	{
		public int EntryBar;
		public int ExitBar;
		public bool IsLong;
		public bool IsShown;
		public bool AmbiguousExit;
		public bool HasBreakEven;
		public bool BreakEvenActive;
		public int BreakEvenBar;
		public decimal TriggerPrice;
		public decimal BreakEvenPrice;
		public bool WithTrend;
		public bool DeltaConfirms;
		public bool FillConfirms;
		public List<string> CandlePatterns;
		public string Outcome;
		public string Trigger;
		public int Confirmations;
		public decimal EntryPrice;
		public decimal TakeProfitPrice;
		public decimal StopLossPrice;
		public decimal ExitPrice;
		public decimal ZoneTop;
		public decimal ZoneBottom;
		public decimal FillPrice;
		public decimal FillVolume;
		public string SweptLevel;
		public double TakeProfit;
		public double BreakEven;
		public double StopLoss;
		public double ExpectedTicks;
		public (int Wins, int BreakEvens, int Count) Setup;
		public (int Wins, int BreakEvens, int Count) TriggerTally;
		public (int Wins, int BreakEvens, int Count) Direction;
	}

	private sealed class ZoneView
	{
		public int StartBar;
		public int EndBar;
		public int LastTouchBar;
		public decimal Top;
		public decimal Bottom;
		public bool IsBullish;
		public string State;
		public bool ReactionBullish;
		public List<string> ReactionPatterns;
		public bool SignalShown;
	}

	private sealed class FillView
	{
		public int Bar;
		public decimal Price;
		public decimal Volume;
		public bool BidsFilled;
		public bool FromFootprint;
		public decimal RestingSize;
		public int Reaction;
		public int ReactionBar;
		public List<string> ReactionPatterns;
		public bool Decided;
		public int WatchedBars;
		public decimal UpTicks;
		public decimal DownTicks;
		public bool SignalShown;
	}

	private sealed class OrderView
	{
		public decimal Price;
		public bool IsBid;
		public decimal Volume;
		public decimal MaxVolume;
		public decimal Traded;
		public int FirstBar;
		public int EndBar;
		public string State;
		public bool Leaving;
		public decimal Threshold;
	}

	// Most checks were written for signals at any hour, a fixed big-fill size and no key levels;
	// they start from those settings. The checks of the newer features switch them back on (see
	// Defaults for what a fresh indicator has).
	private static FvgReactionLiquiditySweep NewIndicator(Action<FvgReactionLiquiditySweep> configure)
	{
		var ind = new FvgReactionLiquiditySweep
		{
			InstrumentInfo = new InstrumentInfo { TickSize = Tick },
			ChartInfo = new FakeChart(),
			SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.AllHours,
			FillSize = FvgReactionLiquiditySweep.FillSizeRule.FixedContracts,
			LevelPriorDay = false,
			LevelOvernight = false,
			LevelOpeningRange = false,
			LevelEqual = false
		};

		configure?.Invoke(ind);
		return ind;
	}

	// back to what a fresh indicator has
	private static void Defaults(FvgReactionLiquiditySweep ind)
	{
		var fresh = new FvgReactionLiquiditySweep();
		ind.SignalHours = fresh.SignalHours;
		ind.FillSize = fresh.FillSize;
		ind.LevelPriorDay = fresh.LevelPriorDay;
		ind.LevelOvernight = fresh.LevelOvernight;
		ind.LevelOpeningRange = fresh.LevelOpeningRange;
		ind.LevelEqual = fresh.LevelEqual;
	}

	private static FvgReactionLiquiditySweep RunHistorical(List<IndicatorCandle> bars,
		Action<FvgReactionLiquiditySweep> configure = null, HashSet<int> sessionStarts = null)
	{
		var ind = NewIndicator(configure);

		if (sessionStarts != null)
		{
			foreach (var s in sessionStarts)
				ind.SessionStarts.Add(s);
		}

		ind.Candles.AddRange(bars);
		ind.HarnessRecalculate();

		for (var i = 0; i < bars.Count; i++)
			ind.HarnessCalculate(i);

		return ind;
	}

	// history loaded, ticking live from here
	private static FvgReactionLiquiditySweep LiveIndicator(List<IndicatorCandle> history, Action<FvgReactionLiquiditySweep> configure = null)
	{
		var ind = NewIndicator(configure);
		ind.Candles.AddRange(history);
		ind.HarnessRecalculate();

		for (var i = 0; i < history.Count; i++)
			ind.HarnessCalculate(i);

		return ind;
	}

	// what ATAS does after a settings change: calculate every bar again
	private static void Recalculate(FvgReactionLiquiditySweep ind)
	{
		ind.HarnessRecalculate();

		for (var i = 0; i < ind.Candles.Count; i++)
			ind.HarnessCalculate(i);
	}

	// append a new bar and feed it one tick at a time, like ATAS does in real time - with an
	// order book trading around it if there is one
	private static void StreamBar(FvgReactionLiquiditySweep ind, IList<decimal> path, decimal delta = 0, List<PriceVolumeInfo> levels = null,
		BookSimulator book = null, DateTime? time = null)
	{
		var candle = new IndicatorCandle { Open = path[0], High = path[0], Low = path[0], Close = path[0], Time = time ?? DateTime.MinValue };
		ind.Candles.Add(candle);
		var bar = ind.Candles.Count - 1;

		for (var i = 0; i < path.Count; i++)
		{
			candle.High = Math.Max(candle.High, path[i]);
			candle.Low = Math.Min(candle.Low, path[i]);
			candle.Close = path[i];

			var done = i == path.Count - 1;
			candle.Delta = done ? delta : delta * (i + 1) / path.Count;
			candle.Levels = levels == null
				? new List<PriceVolumeInfo>()
				: levels.Where(l => l.Price >= candle.Low && l.Price <= candle.High).ToList();

			book?.OnTick(path[i], bar);
			ind.HarnessCalculate(bar);
			book?.Settle();
		}
	}

	// one more tick of the bar being built by hand
	private static void AddTick(FvgReactionLiquiditySweep ind, IndicatorCandle candle, decimal price)
	{
		candle.High = Math.Max(candle.High, price);
		candle.Low = Math.Min(candle.Low, price);
		candle.Close = price;
		ind.HarnessCalculate(ind.Candles.IndexOf(candle));
	}

	private static void Depth(FvgReactionLiquiditySweep ind, bool bid, decimal price, decimal volume)
	{
		ind.HarnessDepth(new MarketDataArg
		{
			Price = price,
			Volume = volume,
			DataType = bid ? MarketDataType.Bid : MarketDataType.Ask,
			Time = ind.MarketTime
		});
	}

	private static void Print(FvgReactionLiquiditySweep ind, decimal price, decimal volume, bool sell)
	{
		ind.HarnessTrade(new MarketDataArg
		{
			Price = price,
			Volume = volume,
			DataType = MarketDataType.Trade,
			Direction = sell ? TradeDirection.Sell : TradeDirection.Buy,
			Time = ind.MarketTime
		});
	}

	private static List<TradeView> Trades(FvgReactionLiquiditySweep ind)
	{
		var list = (IList)IndicatorType.GetField("_trades", Private).GetValue(ind);
		var result = new List<TradeView>();

		foreach (var t in list)
		{
			var estimate = Get(t, "Estimate");
			double EstimateValue(string name) => (double)estimate.GetType().GetProperty(name).GetValue(estimate);

			(int, int, int) Tally(string name)
			{
				var tally = estimate.GetType().GetProperty(name).GetValue(estimate);
				int Count(string property) => (int)tally.GetType().GetProperty(property).GetValue(tally);
				return (Count("Wins"), Count("BreakEvens"), Count("Count"));
			}

			result.Add(new TradeView
			{
				EntryBar = (int)Get(t, "EntryBar"),
				ExitBar = (int)Get(t, "ExitBar"),
				IsLong = (bool)Get(t, "IsLong"),
				IsShown = (bool)Get(t, "IsShown"),
				AmbiguousExit = (bool)Get(t, "AmbiguousExit"),
				HasBreakEven = (bool)Get(t, "HasBreakEven"),
				BreakEvenActive = (bool)Get(t, "BreakEvenActive"),
				BreakEvenBar = (int)Get(t, "BreakEvenBar"),
				TriggerPrice = (decimal)Get(t, "TriggerPrice"),
				BreakEvenPrice = (decimal)Get(t, "BreakEvenPrice"),
				WithTrend = (bool)Get(t, "WithTrend"),
				DeltaConfirms = (bool)Get(t, "DeltaConfirms"),
				FillConfirms = (bool)Get(t, "FillConfirms"),
				CandlePatterns = PatternList(Get(t, "CandlePatterns")),
				Outcome = Get(t, "Outcome").ToString(),
				Trigger = Get(t, "Trigger").ToString(),
				Confirmations = (int)Get(t, "Confirmations"),
				EntryPrice = (decimal)Get(t, "EntryPrice"),
				TakeProfitPrice = (decimal)Get(t, "TakeProfitPrice"),
				StopLossPrice = (decimal)Get(t, "StopLossPrice"),
				ExitPrice = (decimal)Get(t, "ExitPrice"),
				ZoneTop = (decimal)Get(t, "ZoneTop"),
				ZoneBottom = (decimal)Get(t, "ZoneBottom"),
				FillPrice = (decimal)Get(t, "FillPrice"),
				FillVolume = (decimal)Get(t, "FillVolume"),
				SweptLevel = LevelKeyOf(Get(t, "SweptLevel")),
				TakeProfit = EstimateValue("TakeProfit"),
				BreakEven = EstimateValue("BreakEven"),
				StopLoss = EstimateValue("StopLoss"),
				ExpectedTicks = EstimateValue("ExpectedTicks"),
				Setup = Tally("Setup"),
				TriggerTally = Tally("Trigger"),
				Direction = Tally("Direction")
			});
		}

		return result;
	}

	// the live gaps first, then the ones no longer watched, in the order they ended
	private static List<ZoneView> Zones(FvgReactionLiquiditySweep ind)
	{
		var active = (IList)IndicatorType.GetField("_activeZones", Private).GetValue(ind);
		var retired = (IList)IndicatorType.GetField("_retiredZones", Private).GetValue(ind);

		return active.Cast<object>().Concat(retired.Cast<object>())
			.Select(z => new ZoneView
			{
				StartBar = (int)Get(z, "StartBar"),
				EndBar = (int)Get(z, "EndBar"),
				LastTouchBar = (int)Get(z, "LastTouchBar"),
				Top = (decimal)Get(z, "Top"),
				Bottom = (decimal)Get(z, "Bottom"),
				IsBullish = (bool)Get(z, "IsBullish"),
				State = Get(z, "State").ToString(),
				ReactionBullish = (bool)Get(z, "ReactionBullish"),
				ReactionPatterns = PatternList(Get(z, "ReactionPatterns")),
				SignalShown = (bool)Get(z, "SignalShown")
			})
			.ToList();
	}

	private static List<string> ZoneKeys(FvgReactionLiquiditySweep ind)
	{
		return Zones(ind)
			.Select(z => $"{z.StartBar}|{z.Top}|{z.Bottom}|{z.IsBullish}|{z.State}|{z.EndBar}|{z.LastTouchBar}|{z.State == "Used" && z.ReactionBullish}|"
				+ string.Join(",", z.ReactionPatterns))
			.OrderBy(k => k, StringComparer.Ordinal)
			.ToList();
	}

	// "PriorDayHigh@102.00@25": a swept key level, as a trade names it ("" for none)
	private static string LevelKeyOf(object level)
	{
		return level == null ? "" : $"{Get(level, "Kind")}@{Get(level, "Price")}@{Get(level, "EndBar")}";
	}

	private static List<object> KeyLevelsOf(FvgReactionLiquiditySweep ind)
	{
		var active = (IList)IndicatorType.GetField("_keyLevels", Private).GetValue(ind);
		var ended = (IList)IndicatorType.GetField("_endedLevels", Private).GetValue(ind);
		return active.Cast<object>().Concat(ended.Cast<object>()).ToList();
	}

	private static List<string> KeyLevelKeys(FvgReactionLiquiditySweep ind)
	{
		return KeyLevelsOf(ind)
			.Select(l => $"{Get(l, "Kind")}|{Get(l, "Price")}|{Get(l, "FromBar")}|{Get(l, "EndBar")}|{Get(l, "State")}|{Get(l, "FirstSwingBar")}|{Get(l, "SecondSwingBar")}")
			.OrderBy(k => k, StringComparer.Ordinal)
			.ToList();
	}

	private static List<FillView> Fills(FvgReactionLiquiditySweep ind)
	{
		var fills = (IList)IndicatorType.GetField("_fills", Private).GetValue(ind);

		return fills.Cast<object>()
			.Select(f => new FillView
			{
				Bar = (int)Get(f, "Bar"),
				Price = (decimal)Get(f, "Price"),
				Volume = (decimal)Get(f, "Volume"),
				BidsFilled = (bool)Get(f, "BidsFilled"),
				FromFootprint = (bool)Get(f, "FromFootprint"),
				RestingSize = (decimal)Get(f, "RestingSize"),
				Reaction = (int)Get(f, "Reaction"),
				ReactionBar = (int)Get(f, "ReactionBar"),
				ReactionPatterns = PatternList(Get(f, "ReactionPatterns")),
				Decided = (bool)Get(f, "Decided"),
				WatchedBars = (int)Get(f, "WatchedBars"),
				UpTicks = (decimal)Get(f, "UpTicks"),
				DownTicks = (decimal)Get(f, "DownTicks"),
				SignalShown = (bool)Get(f, "SignalShown")
			})
			.ToList();
	}

	// resting orders still in the book (bids, then offers), then the ended ones in the order they ended
	private static List<OrderView> Orders(FvgReactionLiquiditySweep ind)
	{
		var bids = (IDictionary)IndicatorType.GetField("_restingBids", Private).GetValue(ind);
		var asks = (IDictionary)IndicatorType.GetField("_restingAsks", Private).GetValue(ind);
		var ended = (IList)IndicatorType.GetField("_endedOrders", Private).GetValue(ind);

		return bids.Values.Cast<object>().Concat(asks.Values.Cast<object>()).Concat(ended.Cast<object>())
			.Select(o => new OrderView
			{
				Price = (decimal)Get(o, "Price"),
				IsBid = (bool)Get(o, "IsBid"),
				Volume = (decimal)Get(o, "Volume"),
				MaxVolume = (decimal)Get(o, "MaxVolume"),
				Traded = (decimal)Get(o, "Traded"),
				FirstBar = (int)Get(o, "FirstBar"),
				EndBar = (int)Get(o, "EndBar"),
				State = Get(o, "State").ToString(),
				Leaving = (bool)Get(o, "Leaving"),
				Threshold = (decimal)Get(o, "Threshold")
			})
			.ToList();
	}

	private static object Get(object obj, string field)
	{
		return obj.GetType().GetField(field).GetValue(obj);
	}

	private static ValueDataSeries Series(FvgReactionLiquiditySweep ind, string field)
	{
		return (ValueDataSeries)IndicatorType.GetField(field, Private).GetValue(ind);
	}

	private static int ModelResolved(FvgReactionLiquiditySweep ind)
	{
		var model = IndicatorType.GetField("_model", Private).GetValue(ind);
		return (int)model.GetType().GetProperty("Resolved").GetValue(model);
	}

	// a CandlePattern value as sorted member names
	private static List<string> PatternList(object patterns)
	{
		var text = patterns.ToString();
		var names = text == "None" ? new List<string>() : text.Split(", ").ToList();
		names.Sort(StringComparer.Ordinal);
		return names;
	}

	private static string SignalKey(TradeView t)
	{
		return $"{t.EntryBar}|{t.IsLong}|{t.Trigger}|{t.Confirmations}|{t.EntryPrice}";
	}

	private static IndicatorCandle Bar(decimal open, decimal high, decimal low, decimal close, decimal delta = 0)
	{
		return new IndicatorCandle { Open = open, High = high, Low = low, Close = close, Delta = delta, Time = new DateTime(2026, 3, 2) };
	}

	// driftless random-walk odds, derived independently of the indicator
	private static (double Tp, double Be, double Sl) PriorOf(FvgReactionLiquiditySweep ind)
	{
		double tp = ind.TakeProfitTicks;
		double sl = ind.StopLossTicks;

		if (ind.BreakEvenTriggerTicks <= 0 || ind.BreakEvenTriggerTicks >= ind.TakeProfitTicks)
			return (sl / (tp + sl), 0, tp / (tp + sl));

		double trigger = ind.BreakEvenTriggerTicks;
		double stop = Math.Min(ind.BreakEvenStopTicks, ind.BreakEvenTriggerTicks - 1);
		var reach = sl / (trigger + sl);
		var after = (trigger - stop) / (tp - stop);
		return (reach * after, reach * (1 - after), 1 - reach);
	}

	// whole percentages adding to 100, largest remainders first - as the labels show them
	private static int[] LabelPercentsOf(TradeView t)
	{
		var p = t.HasBreakEven ? new[] { t.TakeProfit, t.BreakEven, t.StopLoss } : new[] { t.TakeProfit, t.StopLoss };
		var result = p.Select(x => (int)Math.Floor(x * 100)).ToArray();
		var byRemainder = Enumerable.Range(0, p.Length).OrderByDescending(i => p[i] * 100 - result[i]).ThenBy(i => i).ToList();
		var left = 100 - result.Sum();

		for (var i = 0; i < left; i++)
			result[byRemainder[i]]++;

		return result;
	}

	private static readonly Type TradeType = IndicatorType.GetNestedType("SignalTrade", BindingFlags.NonPublic);

	// entry 100, TP / SL 20 away, break-even at +10 moving the stop to +5
	private static object MakeTrade(bool isLong, bool breakEven, bool active = false)
	{
		var trade = Activator.CreateInstance(TradeType);
		var direction = isLong ? 1 : -1;
		Set(trade, "IsLong", isLong);
		Set(trade, "EntryPrice", 100m);
		Set(trade, "TakeProfitPrice", 100m + direction * 20);
		Set(trade, "StopLossPrice", 100m - direction * 20);
		Set(trade, "HasBreakEven", breakEven);
		Set(trade, "TriggerPrice", 100m + direction * 10);
		Set(trade, "BreakEvenPrice", 100m + direction * 5);
		Set(trade, "BreakEvenActive", active);
		return trade;
	}

	private static void Set(object obj, string field, object value)
	{
		obj.GetType().GetField(field).SetValue(obj, value);
	}

	private static object Odds(double tp, double be, double sl)
	{
		return Activator.CreateInstance(IndicatorType.GetNestedType("OutcomeOdds", BindingFlags.NonPublic), tp, be, sl);
	}

	private static void SetEstimate(object trade, double tp, double be, double sl)
	{
		var counter = Activator.CreateInstance(IndicatorType.GetNestedType("OutcomeCounter", BindingFlags.NonPublic));
		var estimateType = IndicatorType.GetNestedType("ProbabilityEstimate", BindingFlags.NonPublic);
		Set(trade, "Estimate", Activator.CreateInstance(estimateType, Odds(tp, be, sl), 0.0, counter, counter, counter));
	}

	// a whole historical bar, as the indicator sees it the first time
	private static (string Outcome, bool Active, bool Ambiguous, object Exit) Settle(object trade, decimal open, decimal high, decimal low,
		decimal close, FvgReactionLiquiditySweep.SameBarHitRule rule)
	{
		var method = IndicatorType.GetMethod("SettleStretch", PrivateStatic);
		var args = new object[] { trade, open, high, low, close, rule, false };
		var result = method.Invoke(null, args);
		object R(string name) => result.GetType().GetProperty(name).GetValue(result);
		return (R("Outcome").ToString(), (bool)R("BreakEvenActive"), (bool)args[6], R("ExitPrice"));
	}

	private static (string Outcome, bool Active, bool Ambiguous, object Exit) Walk(object trade, params decimal[] path)
	{
		var result = IndicatorType.GetMethod("WalkPath", PrivateStatic).Invoke(null, new[] { trade, (object)path });
		object R(string name) => result.GetType().GetProperty(name).GetValue(result);
		return (R("Outcome").ToString(), (bool)R("BreakEvenActive"), false, R("ExitPrice"));
	}

	private static void Expect((string Outcome, bool Active, bool Ambiguous, object Exit) actual, string outcome, bool ambiguous, string name)
	{
		Check(actual.Outcome == outcome && actual.Ambiguous == ambiguous, $"{name}: got {actual.Outcome}/{actual.Ambiguous}");
	}

	private static void ExpectBar((string Outcome, bool Active, bool Ambiguous, object Exit) actual, string outcome, bool active, bool ambiguous, string name)
	{
		Check(actual.Outcome == outcome && actual.Active == active && actual.Ambiguous == ambiguous,
			$"{name}: got {actual.Outcome}/{actual.Active}/{actual.Ambiguous}");
	}

	private static void ExpectWalk((string Outcome, bool Active, bool Ambiguous, object Exit) actual, string outcome, bool active, object exit, string name)
	{
		var exitOk = outcome == "Open" || (actual.Outcome != "Open" && (decimal)actual.Exit == Convert.ToDecimal(exit, CultureInfo.InvariantCulture));
		Check(actual.Outcome == outcome && actual.Active == active && exitOk, $"{name}: got {actual.Outcome}/{actual.Active}/{actual.Exit}");
	}

	private static double Hit(double theta, double excursion, double tp, double sl)
	{
		return (double)IndicatorType.GetMethod("HitProbability", PrivateStatic).Invoke(null, new object[] { theta, excursion, tp, sl });
	}

	private static double Live(double p, double tp, double sl, double excursion)
	{
		return (double)IndicatorType.GetMethod("LiveProbability", PrivateStatic).Invoke(null, new object[] { p, tp, sl, excursion });
	}

	private static void Run(string name, Action test)
	{
		var before = Failures.Count;

		try
		{
			test();
		}
		catch (Exception ex)
		{
			Failures.Add($"{name}: threw {ex}");
		}

		Console.WriteLine($"{(Failures.Count == before ? "PASS" : "FAIL")}  {name}");
	}

	private static void Check(bool condition, string message)
	{
		_checks++;

		if (!condition)
			Failures.Add(message);
	}

	private static void CheckClose(double actual, double expected, double tolerance, string message)
	{
		Check(Math.Abs(actual - expected) <= tolerance, $"{message}: {actual} vs {expected}");
	}

	#endregion
}

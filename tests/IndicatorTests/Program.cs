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
		Run("FVG: completing candle is not a retest", FvgCompletingCandleIsNotReaction);
		Run("FVG retest: BUY at close, TP 80 ticks later", FvgRetestBuyHitsTakeProfit);
		Run("Sweep of highs: SHORT, same-bar rules", SweepShortSameBarRule);
		Run("Break-even: stop moves at +40t, exits at +20t", BreakEvenStopAfterTrigger);
		Run("Live ticks: signal only after the bar closes", LiveSignalAppearsAfterClose);
		Run("Live ticks: a dip before the trigger is not a break-even exit", LiveBreakEvenFollowsTheTicks);
		Run("Absorption: heatmap cells and buy confirmation", AbsorptionConfirmation);
		Run("FVG: a reacted zone still dies when price closes through", ReactedZoneIsInvalidated);
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
		Run("Fuzz: no break-even, worst-case rule", () => FuzzHistorical(seed: 29, configure: i =>
		{
			i.BreakEvenTriggerTicks = 0;
			i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
		}));
		Run("Fuzz: tight bracket, break-even, worst-case rule", () => FuzzHistorical(seed: 37, configure: i =>
		{
			i.TakeProfitTicks = 40;
			i.StopLossTicks = 120;
			i.BreakEvenTriggerTicks = 20;
			i.BreakEvenStopTicks = 10;
			i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
		}));
		Run("Fuzz: break-even at entry, 120 / 60 bracket", () => FuzzHistorical(seed: 31, configure: i =>
		{
			i.TakeProfitTicks = 120;
			i.StopLossTicks = 60;
			i.BreakEvenTriggerTicks = 30;
			i.BreakEvenStopTicks = 0;
		}));
		Run("Fuzz: filters hide signals, the model still learns", FuzzFilters);
		Run("Fuzz: live tick stream matches history", FuzzLiveMatchesHistory);
		Run("Recalculate is deterministic", RecalculateIsDeterministic);
		Run("Render: labels, levels, panel, tooltip", RenderSmoke);

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
		CheckClose(Estimate(true, sweep, 1).Tp, direction.Item1, 1e-12, "one TP, other trigger");
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

	private static void FvgCompletingCandleIsNotReaction()
	{
		var bars = FvgSetup();
		bars.Add(Bar(103.75m, 104.5m, 103.25m, 104));       // 19 no retest
		var ind = RunHistorical(bars);

		Check(Series(ind, "_bullReaction")[17] == 0, "old bug: the gap's own right candle was flagged as a reaction");
		Check(Trades(ind).Count == 0, "no signal without a retest");

		var zones = (IList)IndicatorType.GetField("_zones", Private).GetValue(ind);
		Check(zones.Count == 1, $"expected exactly one zone, got {zones.Count}");
	}

	private static void FvgRetestBuyHitsTakeProfit()
	{
		var bars = FvgSetup();
		bars.Add(Bar(103, 103.75m, 101.75m, 103.5m, delta: 50));   // 19 dips into the zone, closes above it, bullish
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
		Check(t.Trigger == "Fvg" && t.Confirmations == 1, $"trigger/confirmations {t.Trigger}/{t.Confirmations}");
		CheckClose(t.TakeProfit, 2.0 / 9, 1e-12, "no history -> TP 2/9");
		CheckClose(t.BreakEven, 4.0 / 9, 1e-12, "no history -> BE 4/9");
		CheckClose(t.ExpectedTicks, 0, 1e-12, "no history -> 0 expected ticks");
		Check(t.Outcome == "TakeProfit" && t.ExitBar == 21 && t.BreakEvenBar == 21, $"outcome {t.Outcome} on bar {t.ExitBar}");
		Check(Series(ind, "_buySignal")[19] == 101.25m, "buy arrow 2 ticks under the low");
		Check(Series(ind, "_bullReaction")[19] == 0, "signal arrow replaces the reaction arrow");
		Check((int)IndicatorType.GetField("_longWins", Private).GetValue(ind) == 1, "panel counts the win");
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

	private static void BreakEvenStopAfterTrigger()
	{
		// BUY at 103.5: TP 123.5, SL 83.5, trigger 113.5 (+40t), break-even stop 108.5 (+20t)
		List<IndicatorCandle> Bars()
		{
			var bars = FvgSetup();
			bars.Add(Bar(103, 103.75m, 101.75m, 103.5m, delta: 50));   // 19 BUY
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

	private static void ReactedZoneIsInvalidated()
	{
		var bars = FvgSetup();
		bars.Add(Bar(103, 103.75m, 101.75m, 103.5m));      // 19 reaction
		bars.Add(Bar(103.5m, 103.5m, 102.75m, 103));        // 20 still above the zone
		var ind = RunHistorical(bars);
		Check(ZoneKeys(ind).Count == 1 && ZoneKeys(ind)[0].EndsWith("|False|True"), "zone reacted and is still drawn");

		bars.Add(Bar(103, 103, 99.75m, 100));               // 21 closes under the 100.5 bottom
		bars.Add(Bar(100, 100.25m, 99.75m, 100));           // 22 forming
		ind = RunHistorical(bars);
		Check(ZoneKeys(ind).Count == 0, "zone closed through after its reaction is removed");
	}

	private static void AbsorptionConfirmation()
	{
		// the FVG retest bar (19, range 101.75 - 103.75) with a footprint: thin levels
		// everywhere plus one heavy level whose side and position vary per case
		List<IndicatorCandle> Bars(decimal heavyPrice, bool heavyOnBid)
		{
			var bars = FvgSetup();
			var retest = Bar(103, 103.75m, 101.75m, 103.5m, delta: 50);

			for (var p = retest.Low; p <= retest.High; p += Tick)
			{
				var heavy = p == heavyPrice;
				decimal volume = heavy ? 400 : 20;
				var bid = heavy ? (heavyOnBid ? 340 : 60) : 10;
				retest.Levels.Add(new PriceVolumeInfo { Price = p, Volume = volume, Bid = bid, Ask = volume - bid });
			}

			bars.Add(retest);
			bars.Add(Bar(103.5m, 104, 103, 103.75m));
			return bars;
		}

		var ind = RunHistorical(Bars(101.75m, heavyOnBid: true));
		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].HasAbsorption && trades[0].Confirmations == 2, "sellers absorbed at the low confirm the buy");

		var map = (IDictionary)IndicatorType.GetField("_absorptionByBar", Private).GetValue(ind);
		var cells = map.Contains(19) ? (Array)Get(map[19], "Cells") : Array.Empty<object>();
		Check(cells.Length == 1, $"exactly the heavy level is flagged, got {cells.Length}");

		if (cells.Length == 1)
		{
			var cell = cells.GetValue(0);
			var cellType = cell.GetType();
			Check((decimal)cellType.GetProperty("Price").GetValue(cell) == 101.75m && !(bool)cellType.GetProperty("AskDominant").GetValue(cell),
				"flagged cell is the bid-dominant 101.75 level");
		}

		var wrongSide = Trades(RunHistorical(Bars(101.75m, heavyOnBid: false)));
		Check(wrongSide.Count == 1 && !wrongSide[0].HasAbsorption, "absorbed BUYING at the low does not confirm a buy");

		var wrongPlace = Trades(RunHistorical(Bars(103.5m, heavyOnBid: true)));
		Check(wrongPlace.Count == 1 && !wrongPlace[0].HasAbsorption, "absorption near the high does not confirm a buy");

		var required = Trades(RunHistorical(Bars(103.5m, heavyOnBid: true), i => i.RequireAbsorption = true));
		Check(required.Count == 0, "RequireAbsorption drops the unconfirmed buy");
	}

	private static void LiveBreakEvenFollowsTheTicks()
	{
		var ind = NewIndicator(null);
		var history = FvgSetup();
		ind.Candles.AddRange(history);
		ind.HarnessRecalculate();

		for (var i = 0; i < history.Count; i++)
			ind.HarnessCalculate(i);

		StreamBar(ind, new[] { 103m, 102.5m, 101.75m, 102.75m, 103.25m, 103.75m, 103.5m }, delta: 50); // 19: BUY at 103.5

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
		var history = FvgSetup();
		var ind = NewIndicator(null);
		ind.Candles.AddRange(history);
		ind.HarnessRecalculate();

		for (var i = 0; i < history.Count; i++)
			ind.HarnessCalculate(i);

		// bar 19 forms tick by tick: into the zone, then back above it
		var path19 = new[] { 103m, 102.5m, 101.75m, 102.75m, 103.25m, 103.75m, 103.5m };
		StreamBar(ind, path19, delta: 50);
		Check(Series(ind, "_buySignal")[19] == 0 && Trades(ind).Count == 0, "no signal while the bar is still forming");

		// first tick of bar 20 closes bar 19
		StreamBar(ind, new[] { 103.5m, 104m, 110m, 109m });
		Check(Series(ind, "_buySignal")[19] != 0 && Trades(ind).Count == 1, "signal appears once the bar has closed");

		// bar 21 runs through TP intrabar; resolution happens on the tick, before the bar closes
		StreamBar(ind, new[] { 109m, 118m, 123.5m });
		var trades = Trades(ind);
		Check(trades.Count == 1 && trades[0].Outcome == "TakeProfit" && trades[0].ExitBar == 21, "TP resolved intrabar");

		var zones = (IList)IndicatorType.GetField("_zones", Private).GetValue(ind);
		var keys = zones.Cast<object>().Select(z => $"{Get(z, "StartBar")}/{Get(z, "IsBullish")}").ToList();
		Check(keys.Count == keys.Distinct().Count(), "no duplicate zones from intrabar recalculation");
	}

	#endregion

	#region Fuzz

	private static void FuzzHistorical(int seed, Action<FvgReactionLiquiditySweep> configure)
	{
		var market = Generate(6000, seed);
		var ind = RunHistorical(market.Candles, configure, market.SessionStarts);
		var trades = Trades(ind);

		Check(trades.Count > 30, $"fuzz produced only {trades.Count} trades");
		Check(trades.Any(t => t.Outcome == "TakeProfit") && trades.Any(t => t.Outcome == "StopLoss"), "fuzz needs both outcomes");

		VerifyOutcomes(ind, market, trades, liveFromBar: int.MaxValue);
		VerifyInvariants(ind, trades, market.Candles.Count);
		VerifySignalsAgainstOracle(ind, market, trades);
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

	private static void FuzzLiveMatchesHistory()
	{
		var market = Generate(4000, 19);
		const int historyBars = 2500;

		var historical = RunHistorical(market.Candles, null, market.SessionStarts);

		// same market, but only the first 2500 bars are loaded; the rest streams in tick by tick
		var live = NewIndicator(i =>
		{
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

		for (var b = historyBars; b < market.Candles.Count; b++)
			StreamBar(live, market.Paths[b], market.Candles[b].Delta, market.Candles[b].Levels);

		var liveTrades = Trades(live);
		var histTrades = Trades(historical);

		// signal decisions only use closed bars, so the tracked signals must be identical
		var liveKeys = liveTrades.Select(SignalKey).ToList();
		var histKeys = histTrades.Select(SignalKey).ToList();
		Check(liveKeys.SequenceEqual(histKeys), $"live stream produced different signals ({liveKeys.Count} vs {histKeys.Count})");

		var liveZones = ZoneKeys(live);
		var histZones = ZoneKeys(historical);
		Check(liveZones.SequenceEqual(histZones), "live stream produced different FVG zones");

		VerifyOutcomes(live, market, liveTrades, liveFromBar: historyBars);
		VerifyInvariants(live, liveTrades, market.Candles.Count);
		VerifySignalsAgainstOracle(live, market, liveTrades);

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

		ind.HarnessRecalculate();

		for (var i = 0; i < market.Candles.Count; i++)
			ind.HarnessCalculate(i);

		var second = Trades(ind).Select(t => SignalKey(t) + $"|{t.Outcome}|{t.ExitBar}|{t.IsShown}|{t.TakeProfit:R}").ToList();
		Check(first.SequenceEqual(second), "recalculation changed the results");

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

		ind.FirstVisibleBarNumber = last - 400;
		ind.LastVisibleBarNumber = last;
		chart.FirstBar = ind.FirstVisibleBarNumber;
		chart.TopPrice = market.Candles.Skip(last - 400).Max(c => c.High) + 40;

		var context = new RenderContext();
		ind.HarnessRender(context);

		var visibleShown = trades.Where(t => t.IsShown && t.EntryBar >= ind.FirstVisibleBarNumber).ToList();
		Check(visibleShown.Count > 0, "render test needs visible signals");
		Check(context.Strings.Count(s => s.StartsWith("BUY  TP ") || s.StartsWith("SHORT  TP ")) == visibleShown.Count,
			"one probability label per visible signal");
		Check(context.Strings.Any(s => s.StartsWith("FVG / Sweep signals")), "stats panel drawn");
		Check(context.Strings.Any(s => s.StartsWith("Labelled EV>0: ") && s.Contains("EV<=0: ")), "track record line drawn");
		Check(context.Strings.Any(s => s.StartsWith("Longs ") && s.Contains(" BE ")), "panel rows count break-even exits");
		Check(context.Rectangles > 0 && context.Lines > 0, "levels and boxes drawn");

		// labels read "BUY  TP x% | BE y% | SL z%" and the three add up to 100
		var labels = context.Strings.Where(s => s.StartsWith("BUY  TP ") || s.StartsWith("SHORT  TP ")).ToList();
		Check(labels.All(l => l.Contains("% | BE ") && l.Split('%').Take(3).Sum(part => int.Parse(new string(part.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()))) == 100),
			"label odds add up to 100%");
		Check(context.Strings.Any(s => s.Contains(" | EV ")), "labels show expected ticks");

		// an activated trade draws its break-even stop line in the break-even color
		var moved = visibleShown.FirstOrDefault(t => t.BreakEvenActive);

		if (moved != null)
		{
			var yBreakEven = chart.GetYByPrice(moved.BreakEvenPrice, false);
			var beColor = ind.BreakEvenPen.RenderObject.Color;
			Check(context.Operations.Any(o => o.Kind == "line" && o.From.Y == yBreakEven && o.Color.ToArgb() == beColor.ToArgb()), "break-even stop line drawn");
		}

		// hover the first visible label (drawn first, so never nudged) -> tooltip
		var target = visibleShown.OrderBy(t => t.EntryBar).First();
		var candle = market.Candles[target.EntryBar];
		var x = chart.GetXByBar(target.EntryBar, false);
		var y = target.IsLong
			? chart.GetYByPrice(candle.Low - 2 * Tick, false) + ind.LabelOffset + 4
			: chart.GetYByPrice(candle.High + 2 * Tick, false) - ind.LabelOffset - 4;
		chart.MouseLocationInfo.LastPosition = new Point(x, y);

		context = new RenderContext();
		ind.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("P(TP) ") && s.Contains("P(BE) ")), "tooltip drawn on hover");

		// cluster mode + everything switched on/off still renders
		chart.ChartVisualMode = ChartVisualModes.Clusters;
		ind.ShowStatsPanel = false;
		ind.ShowTradeLevels = false;
		context = new RenderContext();
		ind.HarnessRender(context);
		Check(!context.Strings.Any(s => s.StartsWith("FVG / Sweep signals")), "panel hidden when switched off");

		// an open shown trade gets a live line in the panel
		var openInd = NewIndicator(null);
		var bars = FvgSetup();
		bars.Add(Bar(103, 103.75m, 101.75m, 103.5m, delta: 50));
		bars.Add(Bar(103.5m, 108, 103, 107.5m));
		openInd.Candles.AddRange(bars);
		openInd.HarnessRecalculate();

		for (var i = 0; i < bars.Count; i++)
			openInd.HarnessCalculate(i);

		openInd.FirstVisibleBarNumber = 0;
		openInd.LastVisibleBarNumber = bars.Count - 1;
		((FakeChart)openInd.ChartInfo).TopPrice = 130;
		context = new RenderContext();
		openInd.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("Live BUY +16t:  TP ")), "live line for the open trade (+16 ticks)");
		Check(context.Strings.Any(s => s.StartsWith("TP ") && s.Contains("123.50")), "TP price tag on the open trade");
		Check(context.Strings.Any(s => s.StartsWith("SL ") && s.Contains("83.50")), "SL price tag before the stop moves");

		// once +40t is reached the stop tag moves to break-even and SL odds drop to 0
		var movedInd = NewIndicator(null);
		bars = FvgSetup();
		bars.Add(Bar(103, 103.75m, 101.75m, 103.5m, delta: 50));
		bars.Add(Bar(103.5m, 114.5m, 103.25m, 114.5m));
		movedInd.Candles.AddRange(bars);
		movedInd.HarnessRecalculate();

		for (var i = 0; i < bars.Count; i++)
			movedInd.HarnessCalculate(i);

		movedInd.FirstVisibleBarNumber = 0;
		movedInd.LastVisibleBarNumber = bars.Count - 1;
		((FakeChart)movedInd.ChartInfo).TopPrice = 130;
		context = new RenderContext();
		movedInd.HarnessRender(context);
		Check(context.Strings.Any(s => s.StartsWith("Live BUY +44t, stop +20t:  TP ") && s.EndsWith("| SL 0%")), "live line after the stop moved");
		Check(context.Strings.Any(s => s.StartsWith("BE ") && s.Contains("108.50")), "stop tag moved to break-even");
	}

	private sealed class OracleZone
	{
		public int Start;
		public int Confirmed;
		public decimal Top;
		public decimal Bottom;
		public bool Bull;
		public bool Filled;
		public bool Reacted;
	}

	// Re-derives every signal from the raw bars (zones, reactions, sweeps, EMA trend,
	// delta, absorption, cooldown, filters) and checks the indicator's signals and
	// trigger arrows against it.
	private static void VerifySignalsAgainstOracle(FvgReactionLiquiditySweep ind, Market market, List<TradeView> trades)
	{
		var candles = market.Candles;
		var zones = new List<OracleZone>();
		var ema = new List<decimal>();
		var expected = new List<string>();
		var markers = new Dictionary<int, (bool BullReaction, bool BearReaction, bool SweptLows, bool SweptHighs)>();
		var lastLowSweep = -1;
		var lastHighSweep = -1;
		var lastLong = -1;
		var lastShort = -1;
		var minGap = Math.Max(ind.MinFvgTicks, 1) * Tick;

		// the last bar is still forming, so it never produces a signal
		for (var b = 0; b < candles.Count - 1; b++)
		{
			var c = candles[b];
			ema.Add(b == 0 || ind.TrendEmaPeriod <= 0 ? c.Close : ema[b - 1] + 2m / (ind.TrendEmaPeriod + 1) * (c.Close - ema[b - 1]));

			if (b < ind.SwingLookback + 3)
				continue;

			var left = candles[b - 2];

			if (c.Low - left.High >= minGap)
				zones.Add(new OracleZone { Start = b - 1, Confirmed = b, Top = c.Low, Bottom = left.High, Bull = true });

			if (left.Low - c.High >= minGap)
				zones.Add(new OracleZone { Start = b - 1, Confirmed = b, Top = left.Low, Bottom = c.High, Bull = false });

			var bullReaction = false;
			var bearReaction = false;

			foreach (var z in zones.Where(z => !z.Filled && b > z.Confirmed))
			{
				if (z.Bull)
				{
					var closeOk = ind.RequireCloseThroughZone ? c.Close > z.Top : c.Close >= z.Bottom;

					if (!z.Reacted && c.Low <= z.Top && c.Low >= z.Bottom && closeOk && c.Close > c.Open)
					{
						bullReaction = z.Reacted = true;
						continue;
					}

					z.Filled |= c.Close < z.Bottom;
				}
				else
				{
					var closeOk = ind.RequireCloseThroughZone ? c.Close < z.Bottom : c.Close <= z.Top;

					if (!z.Reacted && c.High >= z.Bottom && c.High <= z.Top && closeOk && c.Close < c.Open)
					{
						bearReaction = z.Reacted = true;
						continue;
					}

					z.Filled |= c.Close > z.Top;
				}
			}

			var window = candles.Skip(Math.Max(0, b - ind.SwingLookback)).Take(b - Math.Max(0, b - ind.SwingLookback)).ToList();
			var sweptHighs = c.High > window.Max(x => x.High) && c.Close < window.Max(x => x.High);
			var sweptLows = c.Low < window.Min(x => x.Low) && c.Close > window.Min(x => x.Low);
			zones.RemoveAll(z => z.Filled || b - z.Start > ind.MaxZoneAgeBars);
			markers[b] = (bullReaction, bearReaction, sweptLows, sweptHighs);

			if (sweptLows)
				lastLowSweep = b;

			if (sweptHighs)
				lastHighSweep = b;

			if (ind.ExpireAtSessionEnd && market.SessionStarts.Contains(b + 1))
				continue;

			foreach (var isLong in new[] { true, false })
			{
				if (!(isLong ? ind.EnableBuySignals : ind.EnableShortSignals))
					continue;

				var fvg = isLong ? bullReaction : bearReaction;
				var sweepNow = isLong ? sweptLows : sweptHighs;
				var lastSweep = isLong ? lastLowSweep : lastHighSweep;
				var confluence = lastSweep >= 0 && b - lastSweep <= ind.ConfluenceBars;
				var trigger = fvg ? (confluence ? "SweepThenFvg" : "Fvg") : sweepNow ? "Sweep" : null;

				if (trigger == null
					|| (ind.SignalSource == FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly && !fvg)
					|| (ind.SignalSource == FvgReactionLiquiditySweep.SignalMode.LiquiditySweepOnly && !sweepNow)
					|| (ind.SignalSource == FvgReactionLiquiditySweep.SignalMode.SweepThenFvg && trigger != "SweepThenFvg"))
					continue;

				var lastSignal = isLong ? lastLong : lastShort;

				if (lastSignal >= 0 && b - lastSignal <= ind.SignalCooldownBars)
					continue;

				var absorption = OracleAbsorption(ind, candles, b, isLong);
				var withTrend = ind.TrendEmaPeriod > 0 && b >= ind.TrendEmaPeriod && (isLong ? c.Close > ema[b] : c.Close < ema[b]);
				var delta = isLong ? c.Delta > 0 : c.Delta < 0;

				if ((ind.OnlyWithTrend && !withTrend) || (ind.RequireDeltaConfirmation && !delta) || (ind.RequireAbsorption && !absorption))
					continue;

				var confirmations = (absorption ? 1 : 0) + (withTrend ? 1 : 0) + (delta ? 1 : 0);
				expected.Add($"{b}|{isLong}|{trigger}|{confirmations}|{c.Close}|{absorption}|{withTrend}|{delta}");

				if (isLong)
					lastLong = b;
				else
					lastShort = b;
			}
		}

		var actual = trades
			.OrderBy(t => t.EntryBar).ThenBy(t => !t.IsLong)
			.Select(t => $"{t.EntryBar}|{t.IsLong}|{t.Trigger}|{t.Confirmations}|{t.EntryPrice}|{t.HasAbsorption}|{t.WithTrend}|{t.DeltaConfirms}")
			.ToList();

		var firstDiff = Enumerable.Range(0, Math.Min(actual.Count, expected.Count)).FirstOrDefault(i => actual[i] != expected[i]);
		Check(actual.SequenceEqual(expected),
			$"signals differ from the oracle ({actual.Count} vs {expected.Count}); first difference: "
			+ $"{actual.ElementAtOrDefault(firstDiff)} vs {expected.ElementAtOrDefault(firstDiff)}");

		Check(trades.Any(t => t.HasAbsorption) && (ind.TrendEmaPeriod == 0 || trades.Any(t => t.WithTrend)) && trades.Any(t => t.DeltaConfirms),
			"fuzz market should exercise every confirmation");

		// trigger arrows: set where the oracle found a trigger, unless a shown signal took its place
		var bullReactionSeries = Series(ind, "_bullReaction");
		var bearReactionSeries = Series(ind, "_bearReaction");
		var bullSweepSeries = Series(ind, "_bullSweep");
		var bearSweepSeries = Series(ind, "_bearSweep");
		var shownLong = new HashSet<int>(trades.Where(t => t.IsShown && t.IsLong).Select(t => t.EntryBar));
		var shownShort = new HashSet<int>(trades.Where(t => t.IsShown && !t.IsLong).Select(t => t.EntryBar));
		var wrong = 0;

		for (var b = 0; b < candles.Count; b++)
		{
			markers.TryGetValue(b, out var m);
			var low = candles[b].Low - 2 * Tick;
			var high = candles[b].High + 2 * Tick;

			wrong += bullReactionSeries[b] != (m.BullReaction && !shownLong.Contains(b) ? low : 0) ? 1 : 0;
			wrong += bullSweepSeries[b] != (m.SweptLows && !shownLong.Contains(b) ? low : 0) ? 1 : 0;
			wrong += bearReactionSeries[b] != (m.BearReaction && !shownShort.Contains(b) ? high : 0) ? 1 : 0;
			wrong += bearSweepSeries[b] != (m.SweptHighs && !shownShort.Contains(b) ? high : 0) ? 1 : 0;
		}

		Check(wrong == 0, $"{wrong} trigger arrows differ from the oracle");

		// the zones still alive (and drawn) at the end: same set, same state
		var expectedZones = zones.Select(z => $"{z.Start}|{z.Top}|{z.Bottom}|{z.Bull}|{z.Filled}|{z.Reacted}").ToList();
		Check(ZoneKeys(ind).SequenceEqual(expectedZones), "live FVG zones differ from the oracle");
	}

	private static bool OracleAbsorption(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles, int bar, bool isLong)
	{
		for (var b = bar; b >= Math.Max(0, bar - 1); b--)
		{
			var c = candles[b];

			if (c.Levels.Count == 0 || c.Levels.Average(l => l.Volume) <= 0)
				continue;

			var threshold = Math.Max(ind.AbsorptionMinVolume, (decimal)ind.AbsorptionVolumeMultiplier * c.Levels.Average(l => l.Volume));
			var edge = (c.High - c.Low) * 0.35m;

			foreach (var l in c.Levels)
			{
				if (l.Volume < threshold || l.Volume <= 0 || Math.Max(l.Bid, l.Ask) / l.Volume < (decimal)ind.AbsorptionDominanceRatio)
					continue;

				if (isLong && l.Bid > l.Ask && l.Price <= c.Low + edge)
					return true;

				if (!isLong && l.Ask >= l.Bid && l.Price >= c.High - edge)
					return true;
			}
		}

		return false;
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

		// 4) arrows exactly on shown signals, replacing the trigger arrows
		var buy = Series(ind, "_buySignal");
		var sell = Series(ind, "_shortSignal");
		var bullReaction = Series(ind, "_bullReaction");
		var bullSweep = Series(ind, "_bullSweep");
		var bearReaction = Series(ind, "_bearReaction");
		var bearSweep = Series(ind, "_bearSweep");
		var shownLong = new HashSet<int>(trades.Where(t => t.IsShown && t.IsLong).Select(t => t.EntryBar));
		var shownShort = new HashSet<int>(trades.Where(t => t.IsShown && !t.IsLong).Select(t => t.EntryBar));
		var badMarkers = 0;

		for (var b = 0; b < barCount; b++)
		{
			if ((buy[b] != 0) != shownLong.Contains(b) || (sell[b] != 0) != shownShort.Contains(b))
				badMarkers++;

			if (buy[b] != 0 && (bullReaction[b] != 0 || bullSweep[b] != 0))
				badMarkers++;

			if (sell[b] != 0 && (bearReaction[b] != 0 || bearSweep[b] != 0))
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
		public bool HasAbsorption;
		public bool WithTrend;
		public bool DeltaConfirms;
		public string Outcome;
		public string Trigger;
		public int Confirmations;
		public decimal EntryPrice;
		public decimal TakeProfitPrice;
		public decimal StopLossPrice;
		public decimal ExitPrice;
		public double TakeProfit;
		public double BreakEven;
		public double StopLoss;
		public double ExpectedTicks;
		public (int Wins, int BreakEvens, int Count) Setup;
		public (int Wins, int BreakEvens, int Count) TriggerTally;
		public (int Wins, int BreakEvens, int Count) Direction;
	}

	private static FvgReactionLiquiditySweep NewIndicator(Action<FvgReactionLiquiditySweep> configure)
	{
		var ind = new FvgReactionLiquiditySweep
		{
			InstrumentInfo = new InstrumentInfo { TickSize = Tick },
			ChartInfo = new FakeChart()
		};

		configure?.Invoke(ind);
		return ind;
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

	// append a new bar and feed it one tick at a time, like ATAS does in real time
	private static void StreamBar(FvgReactionLiquiditySweep ind, IList<decimal> path, decimal delta = 0, List<PriceVolumeInfo> levels = null)
	{
		var candle = new IndicatorCandle { Open = path[0], High = path[0], Low = path[0], Close = path[0], Time = DateTime.MinValue };
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

			ind.HarnessCalculate(bar);
		}
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
				HasAbsorption = (bool)Get(t, "HasAbsorption"),
				WithTrend = (bool)Get(t, "WithTrend"),
				DeltaConfirms = (bool)Get(t, "DeltaConfirms"),
				Outcome = Get(t, "Outcome").ToString(),
				Trigger = Get(t, "Trigger").ToString(),
				Confirmations = (int)Get(t, "Confirmations"),
				EntryPrice = (decimal)Get(t, "EntryPrice"),
				TakeProfitPrice = (decimal)Get(t, "TakeProfitPrice"),
				StopLossPrice = (decimal)Get(t, "StopLossPrice"),
				ExitPrice = (decimal)Get(t, "ExitPrice"),
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

	private static List<string> ZoneKeys(FvgReactionLiquiditySweep ind)
	{
		var zones = (IList)IndicatorType.GetField("_zones", Private).GetValue(ind);
		return zones.Cast<object>()
			.Select(z => $"{Get(z, "StartBar")}|{Get(z, "Top")}|{Get(z, "Bottom")}|{Get(z, "IsBullish")}|{Get(z, "Filled")}|{Get(z, "ReactionMarked")}")
			.ToList();
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

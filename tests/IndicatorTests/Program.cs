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
		Run("EvaluateBar: bracket rules", EvaluateBarRules);
		Run("Hit probability: closed forms", HitProbabilityClosedForms);
		Run("Hit probability: Monte Carlo with drift", HitProbabilityMonteCarlo);
		Run("Probability model: shrinkage math", ModelShrinkage);
		Run("FVG: completing candle is not a retest", FvgCompletingCandleIsNotReaction);
		Run("FVG retest: BUY at close, TP 80 ticks later", FvgRetestBuyHitsTakeProfit);
		Run("Sweep of highs: SHORT, same-bar rule", SweepShortSameBarRule);
		Run("Live ticks: signal only after the bar closes", LiveSignalAppearsAfterClose);
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
		Run("Fuzz: min probability filter hides, still learns", FuzzMinProbability);
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

	private static void EvaluateBarRules()
	{
		var stopFirst = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
		var byCandle = FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection;

		// long, TP 120 / SL 80
		Expect(Evaluate(true, 120, 80, 100, 119, 81, 110, stopFirst), "Open", false, "long untouched");
		Expect(Evaluate(true, 120, 80, 100, 120, 90, 118, stopFirst), "TakeProfit", false, "long TP touched exactly");
		Expect(Evaluate(true, 120, 80, 100, 110, 80, 90, stopFirst), "StopLoss", false, "long SL touched exactly");
		Expect(Evaluate(true, 120, 80, 100, 121, 79, 110, stopFirst), "StopLoss", true, "long both, conservative");
		Expect(Evaluate(true, 120, 80, 100, 121, 79, 110, byCandle), "StopLoss", true, "long both, bullish bar -> low first");
		Expect(Evaluate(true, 120, 80, 100, 121, 79, 90, byCandle), "TakeProfit", true, "long both, bearish bar -> high first");
		Expect(Evaluate(true, 120, 80, 125, 126, 70, 75, stopFirst), "TakeProfit", false, "long gaps over TP at the open");
		Expect(Evaluate(true, 120, 80, 75, 130, 70, 128, stopFirst), "StopLoss", false, "long gaps under SL at the open");

		// short, TP 80 / SL 120
		Expect(Evaluate(false, 80, 120, 100, 119, 80, 90, stopFirst), "TakeProfit", false, "short TP");
		Expect(Evaluate(false, 80, 120, 100, 120, 81, 110, stopFirst), "StopLoss", false, "short SL");
		Expect(Evaluate(false, 80, 120, 100, 121, 79, 110, byCandle), "TakeProfit", true, "short both, bullish bar -> low first");
		Expect(Evaluate(false, 80, 120, 100, 121, 79, 90, byCandle), "StopLoss", true, "short both, bearish bar -> high first");
		Expect(Evaluate(false, 80, 120, 100, 121, 79, 90, stopFirst), "StopLoss", true, "short both, conservative");
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
		var model = Activator.CreateInstance(modelType);
		var record = modelType.GetMethod("Record");
		var estimate = modelType.GetMethod("Estimate");
		var fvg = Enum.Parse(triggerType, "Fvg");
		var sweep = Enum.Parse(triggerType, "Sweep");

		double Estimate(object trigger, int confirmations)
		{
			var e = estimate.Invoke(model, new[] { true, trigger, confirmations, 0.5, 10.0 });
			return (double)e.GetType().GetProperty("TakeProfit").GetValue(e);
		}

		CheckClose(Estimate(fvg, 1), 0.5, 1e-12, "no history = prior");

		record.Invoke(model, new[] { true, fvg, 1, true });

		var pDirection = (1 + 10 * 0.5) / 11;
		var pTrigger = (1 + 10 * pDirection) / 11;
		var pSetup = (1 + 10 * pTrigger) / 11;
		CheckClose(Estimate(fvg, 1), pSetup, 1e-12, "one win, same setup");
		CheckClose(Estimate(fvg, 2), pTrigger, 1e-12, "one win, same trigger, other confirmations");
		CheckClose(Estimate(sweep, 1), pDirection, 1e-12, "one win, other trigger");

		// shorts are unaffected by long outcomes
		var shortEstimate = estimate.Invoke(model, new[] { false, fvg, 1, 0.5, 10.0 });
		CheckClose((double)shortEstimate.GetType().GetProperty("TakeProfit").GetValue(shortEstimate), 0.5, 1e-12, "shorts independent");

		// a long losing streak drags the estimate well under 50%
		for (var i = 0; i < 40; i++)
			record.Invoke(model, new[] { true, sweep, 0, false });

		Check(Estimate(sweep, 0) < 0.1, "40 losses -> estimate under 10%");
		Check(Estimate(fvg, 1) > Estimate(sweep, 0), "winning setup stays above losing one");
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
		Check(t.Trigger == "Fvg" && t.Confirmations == 1, $"trigger/confirmations {t.Trigger}/{t.Confirmations}");
		CheckClose(t.TakeProfit, 0.5, 1e-12, "no history -> 50%");
		Check(t.Outcome == "TakeProfit" && t.ExitBar == 21, $"outcome {t.Outcome} on bar {t.ExitBar}");
		Check(Series(ind, "_buySignal")[19] == 101.25m, "buy arrow 2 ticks under the low");
		Check(Series(ind, "_bullReaction")[19] == 0, "signal arrow replaces the reaction arrow");
		Check((int)IndicatorType.GetField("_longWins", Private).GetValue(ind) == 1, "panel counts the win");
	}

	private static void SweepShortSameBarRule()
	{
		List<IndicatorCandle> Bars()
		{
			var bars = Enumerable.Range(0, 15).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
			bars.Add(Bar(100, 101, 99.75m, 100));   // 15 wicks over the 100.5 high, closes back under
			bars.Add(Bar(100, 121, 79, 110));       // 16 (forming) covers TP 80 and SL 120, bullish bar
			return bars;
		}

		var conservative = Trades(RunHistorical(Bars()));
		Check(conservative.Count == 1 && !conservative[0].IsLong && conservative[0].Trigger == "Sweep", "SHORT from the sweep");
		Check(conservative.Count == 1 && conservative[0].Outcome == "StopLoss" && conservative[0].AmbiguousExit, "conservative rule -> SL");

		var byCandle = Trades(RunHistorical(Bars(), i => i.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection));
		Check(byCandle.Count == 1 && byCandle[0].Outcome == "TakeProfit", "bullish bar trades the low first -> short TP");
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

	private static void FuzzMinProbability()
	{
		var market = Generate(6000, 5);
		var ind = RunHistorical(market.Candles, i => i.MinProbabilityPercent = 60, market.SessionStarts);
		var trades = Trades(ind);

		Check(trades.Where(t => t.IsShown).All(t => Math.Round(t.TakeProfit * 100, MidpointRounding.AwayFromZero) >= 60),
			"every shown signal is labelled >= 60%");
		Check(trades.Count(t => !t.IsShown) > 0, "filter hides some signals");
		Check((int)IndicatorType.GetField("_filtered", Private).GetValue(ind) == trades.Count(t => !t.IsShown), "filtered count");

		// hidden signals still feed the model
		var resolved = trades.Count(t => t.Outcome == "TakeProfit" || t.Outcome == "StopLoss");
		Check(ModelResolved(ind) == resolved, "model learns from hidden signals too");

		VerifyOutcomes(ind, market, trades, liveFromBar: int.MaxValue);
		VerifyInvariants(ind, trades, market.Candles.Count);
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
		var signalAlerts = live.Alerts.Count(a => a.StartsWith("BUY @") || a.StartsWith("SHORT @"));
		var resultAlerts = live.Alerts.Count(a => a.Contains(" from "));
		Check(signalAlerts == expectedSignals, $"signal alerts {signalAlerts}, expected {expectedSignals}");
		Check(resultAlerts == expectedResults, $"result alerts {resultAlerts}, expected {expectedResults}");
		Check(expectedSignals > 0 && expectedResults > 0, "live part should contain signals and results");
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
		Check(context.Strings.Any(s => s.StartsWith("Labelled >=60%: ") && s.Contains("<=40%: ")), "track record line drawn");
		Check(context.Rectangles > 0 && context.Lines > 0, "levels and boxes drawn");

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
		Check(context.Strings.Any(s => s.StartsWith("P(TP first)")), "tooltip drawn on hover");

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
		Check(context.Strings.Any(s => s.StartsWith("Live BUY +16t:")), "live line for the open trade (+16 ticks)");
		Check(context.Strings.Any(s => s.StartsWith("TP ") && s.Contains("123.50")), "price tag on the open trade");
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
			var (outcome, exitBar) = Oracle(ind, market, t, last, liveFromBar);

			if (outcome == t.Outcome && exitBar == t.ExitBar)
				continue;

			if (mismatches++ < 5)
				Failures.Add($"trade @{t.EntryBar} {(t.IsLong ? "L" : "S")}: indicator {t.Outcome}/{t.ExitBar}, oracle {outcome}/{exitBar}");
		}

		Check(mismatches == 0, $"{mismatches} trade outcomes differ from the oracle");

		if (!ind.ExpireAtSessionEnd)
			return;

		// no trade opens on the last bar of a session
		Check(trades.All(t => !market.SessionStarts.Contains(t.EntryBar + 1)), "trade opened on a session's last bar");
	}

	// independent re-implementation of how a trade should settle
	private static (string Outcome, int ExitBar) Oracle(FvgReactionLiquiditySweep ind, Market market, TradeView t, int lastBar, int liveFromBar)
	{
		for (var b = t.EntryBar + 1; b <= lastBar; b++)
		{
			var outcome = b >= liveFromBar
				? FirstTouch(t, market.Paths[b])
				: HistoricalBar(t, market.Candles[b], ind.SameBarRule);

			if (outcome != "Open")
				return (outcome, b);

			if (b == lastBar)
				break; // still forming - no end-of-bar expiry yet

			if (ind.MaxBarsInTrade > 0 && b - t.EntryBar >= ind.MaxBarsInTrade)
				return ("Expired", b);

			if (ind.ExpireAtSessionEnd && market.SessionStarts.Contains(b + 1))
				return ("Expired", b);
		}

		return ("Open", -1);
	}

	private static string HistoricalBar(TradeView t, IndicatorCandle c, FvgReactionLiquiditySweep.SameBarHitRule rule)
	{
		var tp = t.IsLong ? c.High >= t.TakeProfitPrice : c.Low <= t.TakeProfitPrice;
		var sl = t.IsLong ? c.Low <= t.StopLossPrice : c.High >= t.StopLossPrice;

		if (tp && sl)
		{
			if (t.IsLong ? c.Open >= t.TakeProfitPrice : c.Open <= t.TakeProfitPrice)
				return "TakeProfit";

			if (t.IsLong ? c.Open <= t.StopLossPrice : c.Open >= t.StopLossPrice)
				return "StopLoss";

			if (rule == FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst)
				return "StopLoss";

			var lowFirst = c.Close >= c.Open;
			return t.IsLong == lowFirst ? "StopLoss" : "TakeProfit";
		}

		return tp ? "TakeProfit" : sl ? "StopLoss" : "Open";
	}

	private static string FirstTouch(TradeView t, List<decimal> path)
	{
		foreach (var price in path)
		{
			if (t.IsLong ? price >= t.TakeProfitPrice : price <= t.TakeProfitPrice)
				return "TakeProfit";

			if (t.IsLong ? price <= t.StopLossPrice : price >= t.StopLossPrice)
				return "StopLoss";
		}

		return "Open";
	}

	private static void VerifyInvariants(FvgReactionLiquiditySweep ind, List<TradeView> trades, int barCount)
	{
		// 1) walk-forward: each estimate uses exactly the trades settled by its signal bar
		var lookAhead = 0;

		foreach (var t in trades)
		{
			var settled = trades.Where(o => o.IsLong == t.IsLong && o.ExitBar >= 0 && o.ExitBar <= t.EntryBar
				&& (o.Outcome == "TakeProfit" || o.Outcome == "StopLoss")).ToList();
			var sameTrigger = settled.Where(o => o.Trigger == t.Trigger).ToList();
			var sameSetup = sameTrigger.Where(o => o.Confirmations == t.Confirmations).ToList();

			var ok = t.DirectionCount == settled.Count && t.DirectionWins == settled.Count(o => o.Outcome == "TakeProfit")
				&& t.TriggerCount == sameTrigger.Count && t.TriggerWins == sameTrigger.Count(o => o.Outcome == "TakeProfit")
				&& t.SetupCount == sameSetup.Count && t.SetupWins == sameSetup.Count(o => o.Outcome == "TakeProfit");

			double k = ind.ProbabilitySmoothing;
			var prior = (double)ind.StopLossTicks / (ind.TakeProfitTicks + ind.StopLossTicks);
			var p = (t.DirectionWins + k * prior) / (t.DirectionCount + k);
			p = (t.TriggerWins + k * p) / (t.TriggerCount + k);
			p = (t.SetupWins + k * p) / (t.SetupCount + k);
			ok &= Math.Abs(p - t.TakeProfit) < 1e-12 && t.TakeProfit > 0 && t.TakeProfit < 1;

			if (!ok && lookAhead++ < 3)
				Failures.Add($"estimate of trade @{t.EntryBar} does not match the trades settled before it");
		}

		Check(lookAhead == 0, $"{lookAhead} estimates use data they could not have had");

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
		Check(PanelInt("_longLosses") == shownTrades.Count(t => t.IsLong && t.Outcome == "StopLoss"), "long losses");
		Check(PanelInt("_shortWins") == shownTrades.Count(t => !t.IsLong && t.Outcome == "TakeProfit"), "short wins");
		Check(PanelInt("_shortLosses") == shownTrades.Count(t => !t.IsLong && t.Outcome == "StopLoss"), "short losses");
		Check(PanelInt("_expired") == shownTrades.Count(t => t.Outcome == "Expired"), "expired");
		Check(PanelInt("_filtered") == trades.Count(t => !t.IsShown), "filtered");
		Check(ModelResolved(ind) == trades.Count(t => t.Outcome == "TakeProfit" || t.Outcome == "StopLoss"), "model sample size");

		var settledAll = trades.Where(t => t.Outcome == "TakeProfit" || t.Outcome == "StopLoss").ToList();
		int Labelled(TradeView t) => (int)Math.Round(t.TakeProfit * 100, MidpointRounding.AwayFromZero);
		Check(PanelInt("_highOddsCount") == settledAll.Count(t => Labelled(t) >= 60)
			&& PanelInt("_highOddsWins") == settledAll.Count(t => Labelled(t) >= 60 && t.Outcome == "TakeProfit")
			&& PanelInt("_lowOddsCount") == settledAll.Count(t => Labelled(t) <= 40)
			&& PanelInt("_lowOddsWins") == settledAll.Count(t => Labelled(t) <= 40 && t.Outcome == "TakeProfit"), "track record counters");

		var net = (decimal)IndicatorType.GetField("_longNetTicks", Private).GetValue(ind)
			+ (decimal)IndicatorType.GetField("_shortNetTicks", Private).GetValue(ind);
		var expectedNet = shownTrades.Where(t => t.Outcome != "Open")
			.Sum(t => (t.ExitPrice - t.EntryPrice) / Tick * (t.IsLong ? 1 : -1));
		Check(net == expectedNet, $"net ticks {net} vs {expectedNet}");
		Check(shownTrades.Where(t => t.Outcome == "TakeProfit").All(t => (t.ExitPrice - t.EntryPrice) / Tick * (t.IsLong ? 1 : -1) == ind.TakeProfitTicks),
			"a TP is worth exactly +TP ticks");
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
		public int SetupWins;
		public int SetupCount;
		public int TriggerWins;
		public int TriggerCount;
		public int DirectionWins;
		public int DirectionCount;
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
			int EstimateInt(string name) => (int)estimate.GetType().GetProperty(name).GetValue(estimate);

			result.Add(new TradeView
			{
				EntryBar = (int)Get(t, "EntryBar"),
				ExitBar = (int)Get(t, "ExitBar"),
				IsLong = (bool)Get(t, "IsLong"),
				IsShown = (bool)Get(t, "IsShown"),
				AmbiguousExit = (bool)Get(t, "AmbiguousExit"),
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
				TakeProfit = (double)estimate.GetType().GetProperty("TakeProfit").GetValue(estimate),
				SetupWins = EstimateInt("SetupWins"),
				SetupCount = EstimateInt("SetupCount"),
				TriggerWins = EstimateInt("TriggerWins"),
				TriggerCount = EstimateInt("TriggerCount"),
				DirectionWins = EstimateInt("DirectionWins"),
				DirectionCount = EstimateInt("DirectionCount")
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

	private static (string Outcome, bool Ambiguous) Evaluate(bool isLong, decimal tp, decimal sl, decimal open, decimal high, decimal low,
		decimal close, FvgReactionLiquiditySweep.SameBarHitRule rule)
	{
		var method = IndicatorType.GetMethod("EvaluateBar", PrivateStatic);
		var args = new object[] { isLong, tp, sl, open, high, low, close, rule, false };
		var outcome = method.Invoke(null, args);
		return (outcome.ToString(), (bool)args[8]);
	}

	private static void Expect((string Outcome, bool Ambiguous) actual, string outcome, bool ambiguous, string name)
	{
		Check(actual.Outcome == outcome && actual.Ambiguous == ambiguous, $"{name}: got {actual.Outcome}/{actual.Ambiguous}");
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

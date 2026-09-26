// Checks for the execution side of FvgReactionLiquiditySweep, run against the ATAS API stubs:
//   dotnet run -c Release --project tests/ExecutionTests
// Exit code 0 = all checks passed. Markets are scripted (or random, for the reconciliation runs)
// and fed tick by tick, as ATAS does in real time. Paper orders never leave the indicator; the
// live checks use a fake broker that records the order calls, and each check plays the broker's
// answers (fills, cancels, rejections) itself.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;

using OFT.Rendering.Context;

internal static class Program
{
	private const decimal Tick = 0.25m;
	private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

	// 09:30 New York on a winter Monday (UTC-5): bar 19, the signal bar of the scripted market, opens at 09:49
	private static readonly DateTime Start = new DateTime(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);
	private static readonly string LogRoot = Path.Combine(Path.GetTempPath(), "fvg-execution-tests-" + Guid.NewGuid().ToString("N"));
	private static readonly Type IndicatorType = typeof(FvgReactionLiquiditySweep);
	private static readonly List<string> Failures = new List<string>();
	private static int _checks;

	public static int Main()
	{
		Run("Defaults: off, paper on, one contract - and nothing happens while off", Defaults);
		Run("Paper BUY: market entry, the chart's stop and TP, break-even, TP traded through", PaperTakeProfitTradedThrough);
		Run("Paper BUY: a TP only touched closes at market with the chart's trade", PaperTakeProfitTouched);
		Run("Paper BUY: the break-even stop fills with slippage", PaperBreakEven);
		Run("Paper BUY: the stop loss", PaperStopLoss);
		Run("Paper SHORT: the mirror image", PaperShort);
		Run("Contracts per trade size every order and the result", Contracts);
		Run("Limit entries: filled a tick through, or cancelled with the chart's order", LimitEntries);
		Run("Daily loss limit: trips on a loss, blocks the day, resets the next", DailyLossLimit);
		Run("Daily loss limit: a trade whose stop could breach it is skipped", SkipTradesBeyondLimit);
		Run("Daily loss limit: a restart reads the day back from the log", RestartRestoresTheDay);
		Run("Log: a file an earlier version started keeps its columns", LogKeepsItsColumns);
		Run("Log report: the trades summed up", LogReport);
		Run("Signal hours: no signal, no order", SignalHours);
		Run("One trade at a time: hidden signals and overlapping ones are skipped", OneTradeAtATime);
		Run("Time exits: Max bars in trade and the session end close at market", TimeExits);
		Run("Flat by: closed at market at a set time, no entries before it, the next day again", FlatBy);
		Run("Break-even stop from the fill: the locked profit counted from the average fill", BreakEvenFromTheFill);
		Run("Daily profit target: done for the day once the closed trades make it", DailyProfitTarget);
		Run("Costs from the instrument: MNQ's tick value and commission, the settings otherwise", CostsByInstrument);
		Run("Day's trend: the bot trades its way only", DayTrend);
		Run("History never trades", HistoryNeverTrades);
		Run("Recalculating the chart keeps the open position and its orders", RecalculationKeepsThePosition);
		Run("Panel lines and alerts", PanelAndAlerts);
		Run("Live: not armed without its account, a loss limit or a connection", LiveNotArmed);
		Run("Live: entry, bracket, break-even and exit calls to the broker", LiveBracket);
		Run("Live: fills reported first by the order, or twice", LiveFillReports);
		Run("Live: partial fills resize the bracket", LivePartialFills);
		Run("Live: a rejected stop closes the position and halts", LiveRejectedStop);
		Run("Live: a position closed outside the indicator halts it", LiveClosedOutside);
		Run("Live: a stop cancelled outside the indicator closes the position", LiveStopCancelledOutside);
		Run("Live: one live trade per account across charts", LiveOneTradePerAccount);
		Run("Live: the account's closed P&L counts toward the daily loss limit", LiveAccountPnl);
		Run("Live: time in force for the resting orders", LiveTimeInForce);
		Run("Random markets: the chart is unchanged, the log reconciles (market entries)", () => RandomMarkets(seed: 5, configure: null));
		Run("Random markets: limit entries, expiry, overlapping signals", () => RandomMarkets(seed: 17, configure: i =>
		{
			i.Entry = FvgReactionLiquiditySweep.EntryRule.LimitPullback;
			i.MaxBarsInTrade = 40;
			i.OneTradeAtATime = false;
			i.Contracts = 2;
		}));
		Run("Random markets: ATR bracket, trades that could breach the loss limit skipped", () => RandomMarkets(seed: 29, configure: i =>
		{
			i.Bracket = FvgReactionLiquiditySweep.BracketRule.AtrMultiple;
			i.MinStopTicks = 8;
			i.DailyLossLimit = 400;
		}, expect: rows => Check(rows.Any(r => r["event"] == "SKIPPED" && r["note"].StartsWith("its stop could lose", StringComparison.Ordinal)),
			"trades skipped for their risk")));
		Run("Random markets: worst-case odds filter what is traded", () => RandomMarkets(seed: 41, configure: i =>
		{
			i.MinExpectedTicks = 1;
			i.FilterByWorstCase = true;
		}, expect: rows =>
		{
			var taken = rows.Where(r => r["event"] == "SIGNAL").ToList();
			var dropped = rows.Where(r => r["event"] == "SKIPPED" && r["note"].StartsWith("its worst-case EV", StringComparison.Ordinal)).ToList();
			Check(taken.All(r => Int(r["ev_ticks"]) >= 1 && Int(r["ev_worst_ticks"]) >= 1), "every trade taken passes on both odds");
			Check(dropped.Count > 0 && dropped.All(r => Int(r["ev_ticks"]) >= 1 && Int(r["ev_worst_ticks"]) < 1),
				$"signals the chart showed but the worst case failed are skipped: {dropped.Count}");
		}));
		Run("Random markets: the profit target ends each day", () => RandomMarkets(seed: 43, configure: i => i.DailyProfitTargetTicks = 30,
			expect: rows => Check(rows.Any(r => r["event"] == "TARGET")
				&& rows.Any(r => r["event"] == "SKIPPED" && r["note"].StartsWith("the daily profit target is reached", StringComparison.Ordinal)),
				$"the target was made and signals were skipped after it: {rows.Count(r => r["event"] == "TARGET")} targets")));
		Run("Random markets: the loss limit trips and stops each day", () => RandomMarkets(seed: 31, configure: i =>
		{
			i.DailyLossLimit = 150;
			i.SkipTradesBeyondLimit = false;
		}, expect: rows => Check(rows.Any(r => r["event"] == "BREAKER")
			&& rows.Any(r => r["event"] == "SKIPPED" && r["note"].StartsWith("the daily loss limit is reached", StringComparison.Ordinal)),
			$"the breaker tripped and signals were skipped after it: {rows.Count(r => r["event"] == "BREAKER")} breakers")));

		Console.WriteLine();
		Console.WriteLine($"{_checks} checks, {Failures.Count} failed");

		foreach (var failure in Failures.Take(40))
			Console.WriteLine("FAIL: " + failure);

		if (Failures.Count == 0)
		{
			try
			{
				Directory.Delete(LogRoot, true);
			}
			catch (IOException)
			{
			}
		}
		else
			Console.WriteLine($"logs kept in {LogRoot}");

		return Failures.Count == 0 ? 0 : 1;
	}

	#region Paper

	private static void Defaults()
	{
		var fresh = new FvgReactionLiquiditySweep();
		Check(!fresh.ExecuteSignals && fresh.PaperTrading, "execution off, paper trading on");
		Check(fresh.Contracts == 1 && fresh.DailyLossLimit == 0 && fresh.SkipTradesBeyondLimit && fresh.LiveAccount.Length == 0,
			"one contract, no loss limit until set, no live account");
		Check(fresh.TickValue == 5 && fresh.CommissionPerContract == 5 && fresh.SlippageTicks == 1 && fresh.AlertOnOrders && fresh.ExecutionLogFolder.Length == 0,
			"NQ's tick value, the backtest's costs, alerts, the default log folder");
		Check(fresh.FlatByTime == TimeSpan.Zero && fresh.FlatByLastEntryMinutes == 10, "no flat-by time; with one, no entries in its last 10 minutes");
		Check(fresh.CountAccountPnl, "live, the account's closed P&L counts toward the limit");
		Check(fresh.LiveTimeInForce == FvgReactionLiquiditySweep.OrderLifetime.ConnectionDefault, "live orders at the connection's time in force");
		Check(!fresh.BreakEvenFromFill, "the break-even stop at the signal's price, as on the chart");
		Check(!fresh.FilterByWorstCase, "the signals the chart shows, by its own odds");
		Check(fresh.DailyProfitTargetTicks == 140 && fresh.CostsFromInstrument && !fresh.OnlyWithDayTrend,
			"a +140t daily profit target, the instrument's own costs, signals whatever the day's trend");

		var group = IndicatorType.GetProperties()
			.Where(p => p.GetCustomAttribute<DisplayAttribute>()?.GetGroupName() == "Execution")
			.Select(p => p.Name)
			.ToList();
		Check(group.Count == 19, $"nineteen settings under Execution: {string.Join(", ", group)}");

		// off: the chart signals as ever, and the executor does nothing at all
		var broker = new FakeBroker();
		var off = Buy(i =>
		{
			i.ExecuteSignals = false;
			i.PaperTrading = false;
			i.LiveAccount = "SIM-1";
			i.DailyLossLimit = 1000;
			i.HarnessTradingManager = broker;
		});
		var bar = OpenBar(off, 103.5m);
		AddTick(off, bar, 115);
		AddTick(off, bar, 124);

		var trades = Trades(off);
		Check(trades.Count == 1 && trades[0].IsShown && trades[0].Outcome == "TakeProfit", "the chart still has its BUY, TP hit");
		Check(!Directory.Exists(off.ExecutionLogFolder), "no log");
		Check(broker.Calls.Count == 0, "no order call, even armed for live");
		Check(!Panel(off).Any(s => s.StartsWith("Execution", StringComparison.Ordinal)), "no execution line on the panel");
	}

	private static void PaperTakeProfitTradedThrough()
	{
		var ind = Buy();
		var rows = Rows(ind);
		Check(rows.Count == 1 && rows[0]["event"] == "MODE"
			&& rows[0]["note"] == "paper · 1 contract · no daily loss limit · daily profit target +140t · tick value $5.00 · commission $5.00 · slippage 1t · "
			+ "TP 80t · SL 80t · BE +40t → +20t · entry at the close · signals all hours · one trade at a time · NQ's tick value and commission",
			$"a MODE row once real time starts: {Describe(rows)}");

		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });                      // 20: its first tick closes bar 19 -> BUY
		rows = Rows(ind);
		var signal = Only(rows, "SIGNAL");
		Check(signal["signal"] == "20260302-094900-BUY" && signal["signal_bar"] == "19" && signal["side"] == "BUY" && signal["note"] == "taken: market entry",
			$"the signal by its New York time and side: {signal["signal"]}");
		Check(signal["setup"] == "FVG" && signal["trigger"] == "FVG" && signal["patterns"] == "Hammer" && signal["confirmations"] == "2"
			&& signal["trend"] == "no" && signal["delta"] == "yes" && signal["order_flow"] == "no" && signal["pattern"] == "yes",
			"setup, pattern and confirmations as the scoreboard counts them");
		Check(signal["odds_tp"] == "22" && signal["odds_be"] == "45" && signal["odds_sl"] == "33" && signal["ev_ticks"] == "0", "the odds on its label");
		Check(signal["ev_worst_ticks"] == "0", "and the worst case's, the same on a fresh chart");
		Check(signal["entry"] == "103.5" && signal["take_profit"] == "123.5" && signal["stop_loss"] == "83.5" && signal["be_trigger"] == "113.5"
			&& signal["be_stop"] == "108.5", "the chart trade's own prices");

		var fill = Only(rows, "FILL", "entry");
		Check(fill["type"] == "buy market" && fill["qty"] == "1" && fill["price"] == "103.75" && fill["position"] == "1",
			$"market BUY filled a tick above the 103.50 print: {fill["price"]}");

		var stop = Only(rows, "ORDER", "stop");
		var target = Only(rows, "ORDER", "take profit");
		Check(stop["type"] == "sell stop" && stop["price"] == "83.5" && target["type"] == "sell limit" && target["price"] == "123.5",
			"a stop at 83.50 and a take profit at 123.50: the chart's");
		Check(rows.IndexOf(stop) < rows.IndexOf(target), "the stop goes in first");

		StreamBar(ind, new[] { 109m, 115, 124 });                             // 21: +46t moves the stop, then trades through the TP
		rows = Rows(ind);
		Check(Trail(rows) == "SIGNAL ORDER:entry FILL:entry ORDER:stop ORDER:take_profit MODIFY:stop FILL:take_profit CANCEL:stop CANCELLED:stop CLOSED CHART",
			$"order trail: {Trail(rows)}");
		Check(Only(rows, "MODIFY", "stop")["price"] == "108.5", "break-even: the stop moved to 108.50");
		Check(Only(rows, "FILL", "take profit")["price"] == "123.5", "the take profit fills at its price");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "79" && closed["pnl_usd"] == "390.00" && closed["day_pnl_usd"] == "390.00" && closed["position"] == "0",
			$"+79t = $395 less $5 commission: {closed["pnl_ticks"]}t {closed["pnl_usd"]}");
		Check(closed["chart_outcome"] == "TP" && closed["chart_ticks"] == "80", "next to the chart's TP +80t");

		var chart = Only(rows, "CHART");
		Check(chart["chart_outcome"] == "TP" && chart["note"] == "Take profit hit on bar 21 (+80t)", "the chart's result, as its tooltip says it");
		Check(Trades(ind).Single().Outcome == "TakeProfit", "the chart's own trade is untouched");
		Check(DayPnl(ind, paper: true) == 390, "the day stands at +$390");
	}

	private static void PaperTakeProfitTouched()
	{
		var ind = Buy();
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });
		StreamBar(ind, new[] { 109m, 115, 123.5m, 123 });                     // touches 123.50, never trades through

		var rows = Rows(ind);
		Check(Trail(rows) == "SIGNAL ORDER:entry FILL:entry ORDER:stop ORDER:take_profit MODIFY:stop CHART EXIT CANCEL:take_profit CANCELLED:take_profit "
			+ "CANCEL:stop CANCELLED:stop ORDER:exit FILL:exit CLOSED", $"order trail: {Trail(rows)}");
		Check(Only(rows, "EXIT")["note"] == "the chart's trade ended (TP)", "closed because the chart's trade ended");

		var exit = Only(rows, "FILL", "exit");
		Check(exit["type"] == "sell market" && exit["price"] == "123.25", "at market, a tick under the 123.50 touch");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "78" && closed["pnl_usd"] == "385.00" && closed["chart_outcome"] == "TP", $"+78t, $385: {closed["pnl_usd"]}");
	}

	private static void PaperBreakEven()
	{
		var ind = Buy();
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });
		StreamBar(ind, new[] { 109m, 115, 110, 108.5m, 108 });                // +46t, then back to the moved stop

		var rows = Rows(ind);
		Check(Trail(rows) == "SIGNAL ORDER:entry FILL:entry ORDER:stop ORDER:take_profit MODIFY:stop FILL:stop CANCEL:take_profit CANCELLED:take_profit CLOSED CHART",
			$"order trail: {Trail(rows)}");
		Check(Only(rows, "FILL", "stop")["price"] == "108.25", "the stop at 108.50 fills a tick worse");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "18" && closed["pnl_usd"] == "85.00" && closed["chart_outcome"] == "BE" && closed["chart_ticks"] == "20",
			$"+18t ($85) where the chart's break-even made +20t: {closed["pnl_ticks"]}");
	}

	private static void PaperStopLoss()
	{
		var ind = Buy();
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });
		StreamBar(ind, new[] { 103.5m, 95, 88, 83.5m, 83 });

		var rows = Rows(ind);
		Check(Trail(rows) == "SIGNAL ORDER:entry FILL:entry ORDER:stop ORDER:take_profit FILL:stop CANCEL:take_profit CANCELLED:take_profit CLOSED CHART",
			$"order trail: {Trail(rows)}");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "-82" && closed["pnl_usd"] == "-415.00" && closed["day_pnl_usd"] == "-415.00"
			&& closed["chart_outcome"] == "SL" && closed["chart_ticks"] == "-80", $"-82t = -$410 less $5: {closed["pnl_usd"]}");
		Check(!rows.Any(r => r["event"] == "BREAKER"), "no daily loss limit set, no breaker");
	}

	private static void PaperShort()
	{
		// a bearish engulfing at the bullish gap: SHORT at 100.25, TP 80.25, SL 120.25, break-even
		// at 90.25 moving the stop to 95.25
		var ind = LiveIndicator(FvgSetup());
		StreamBar(ind, new[] { 102.75m, 103.25m, 102, 103 });                 // 19
		StreamBar(ind, new[] { 103m, 103.25m, 100, 100.25m }, delta: -30);    // 20
		StreamBar(ind, new[] { 100.25m, 95, 90, 85, 80 });                    // 21: in, stop moved, TP traded through

		var rows = Rows(ind);
		Check(Only(rows, "SIGNAL")["side"] == "SHORT", "a SHORT");
		Check(Only(rows, "FILL", "entry")["type"] == "sell market" && Only(rows, "FILL", "entry")["price"] == "100", "sold a tick under 100.25");
		Check(Only(rows, "ORDER", "stop")["type"] == "buy stop" && Only(rows, "ORDER", "stop")["price"] == "120.25"
			&& Only(rows, "ORDER", "take profit")["type"] == "buy limit" && Only(rows, "ORDER", "take profit")["price"] == "80.25", "a buy stop above, a buy limit below");
		Check(Only(rows, "MODIFY", "stop")["price"] == "95.25", "the stop moves down to 95.25");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "79" && closed["pnl_usd"] == "390.00" && closed["position"] == "0" && Only(rows, "FILL", "entry")["position"] == "-1",
			$"short 1, +79t: {closed["pnl_ticks"]}");
	}

	private static void Contracts()
	{
		var ind = Buy(i => i.Contracts = 3);
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });
		StreamBar(ind, new[] { 109m, 115, 124 });

		var rows = Rows(ind);
		Check(Only(rows, "FILL", "entry")["qty"] == "3" && Only(rows, "ORDER", "stop")["qty"] == "3" && Only(rows, "ORDER", "take profit")["qty"] == "3"
			&& Only(rows, "FILL", "entry")["position"] == "3", "3 contracts in, the stop and the TP for 3");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "79" && closed["pnl_usd"] == "1170.00", $"79t a contract on 3: 3 x $395 - 3 x $5 = {closed["pnl_usd"]}");
	}

	private static void LimitEntries()
	{
		// the limit sits 25% of the hammer's 1.75 range (2 ticks) under its 103.50 close: 103.00,
		// with its bracket counted from there
		Action<FvgReactionLiquiditySweep> limit = i => i.Entry = FvgReactionLiquiditySweep.EntryRule.LimitPullback;

		var filled = Buy(limit);
		var bar = OpenBar(filled, 103.5m);
		var rows = Rows(filled);
		var order = Only(rows, "ORDER", "entry");
		Check(Only(rows, "SIGNAL")["note"] == "taken: limit entry" && order["type"] == "buy limit" && order["price"] == "103", "a buy limit at 103.00, the chart's");

		AddTick(filled, bar, 103);
		Check(!Rows(filled).Any(r => r["event"] == "FILL"), "a touch doesn't fill it");

		AddTick(filled, bar, 102.75m);
		rows = Rows(filled);
		Check(Only(rows, "FILL", "entry")["price"] == "103", "a tick through does, at its own price");
		Check(Only(rows, "ORDER", "stop")["price"] == "83" && Only(rows, "ORDER", "take profit")["price"] == "123", "the bracket 80 ticks from 103.00");
		Check(Trades(filled).Single().FillBar == 20, "on the bar the chart's limit filled too");

		var missed = Buy(limit);
		StreamBar(missed, new[] { 103.5m, 104, 103.25m, 103.75m });           // 20
		StreamBar(missed, new[] { 103.75m, 104.5m, 103.5m, 104 });            // 21
		StreamBar(missed, new[] { 104m, 104.5m, 103.25m, 104.25m });          // 22: the chart's order runs out with it
		OpenBar(missed, 104.25m);                                             // 23

		rows = Rows(missed);
		Check(Trail(rows) == "SIGNAL ORDER:entry CHART CANCEL:entry CANCELLED:entry NOFILL", $"order trail: {Trail(rows)}");
		Check(Only(rows, "CHART")["chart_outcome"] == "NO FILL" && Only(rows, "CANCEL", "entry")["note"] == "the chart's limit order expired unfilled",
			"cancelled with the chart's");
		Check(Trades(missed).Single().Outcome == "Missed" && DayPnl(missed, paper: true) == 0, "no trade, nothing counted");
	}

	private static void DailyLossLimit()
	{
		var ind = Buy(i =>
		{
			i.DailyLossLimit = 400;
			i.SkipTradesBeyondLimit = false;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly;
			i.EnableShortSignals = false;
		});
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });
		StreamBar(ind, new[] { 103.5m, 95, 88, 83.5m, 83 });                  // stopped out: -$415

		var rows = Rows(ind);
		var breaker = Only(rows, "BREAKER");
		Check(rows.IndexOf(breaker) == rows.IndexOf(Only(rows, "CLOSED")) + 1
			&& breaker["note"] == "daily loss limit reached: -$415.00 today, limit -$400.00; no new entries until the next trading day",
			$"the breaker trips on the losing close: {breaker["note"]}");
		Check(ind.Alerts.Contains("Daily loss limit reached (-$415.00 today): no new paper entries until the next trading day"), "an alert says so");
		Check(Panel(ind).Contains("Execution PAPER stopped for today: -$415.00 reached the -$400.00 limit"), "and the panel");

		// the same day, another BUY: the chart shows it, the executor doesn't take it
		StreamPattern(ind, 60);
		var buys = Trades(ind).Where(t => t.IsLong && t.IsShown).ToList();
		Check(buys.Count == 2, $"the chart's second BUY: {buys.Count}");
		var skipped = Only(Rows(ind), "SKIPPED");
		Check(skipped["note"] == "the daily loss limit is reached (-$415.00 today, limit -$400.00)" && skipped["signal_bar"] == buys[1].EntryBar.ToString(CultureInfo.InvariantCulture),
			$"skipped: {skipped["note"]}");

		// the next trading day starts at 18:00 New York, from zero
		StreamPattern(ind, 40, Start.Date.AddHours(23).AddMinutes(5));
		rows = Rows(ind);
		var taken = rows.Where(r => r["event"] == "SIGNAL").ToList();
		Check(taken.Count == 2 && taken[1]["trading_day"] == "2026-03-03", $"the next day's BUY is taken: {Describe(taken)}");
		Check(Directory.GetFiles(ind.ExecutionLogFolder).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
			.SequenceEqual(new[] { "2026-03-02_NQ_paper.csv", "2026-03-03_NQ_paper.csv" }), "a file per trading day");
		Check(rows.Count(r => r["event"] == "MODE") == 2, "each day's file starts with the settings");
		Check(Panel(ind).Any(s => s.StartsWith("Execution PAPER · 1 contract · today $0.00 of -$400.00", StringComparison.Ordinal)), "the panel's day is new");
	}

	private static void SkipTradesBeyondLimit()
	{
		// 80 ticks to the stop from the 103.50 print, plus a tick of slippage in and one out: 82t x $5 + $5
		var tight = Buy(i => i.DailyLossLimit = 300);
		OpenBar(tight, 103.5m);
		var rows = Rows(tight);
		Check(Only(rows, "SKIPPED")["note"] == "its stop could lose $415.00, more than the $300.00 left before the daily loss limit",
			$"skipped: {Describe(rows)}");
		Check(!rows.Any(r => r["event"] == "ORDER"), "no order");

		var enough = Buy(i => i.DailyLossLimit = 415);
		OpenBar(enough, 103.5m);
		Check(Rows(enough).Any(r => r["event"] == "SIGNAL"), "a limit that covers the whole stop takes it");

		var off = Buy(i =>
		{
			i.DailyLossLimit = 300;
			i.SkipTradesBeyondLimit = false;
		});
		OpenBar(off, 103.5m);
		Check(Rows(off).Any(r => r["event"] == "SIGNAL"), "with the check off it only stops after a loss");
	}

	private static void RestartRestoresTheDay()
	{
		var folder = NewFolder();
		Action<FvgReactionLiquiditySweep> limit = i =>
		{
			i.DailyLossLimit = 400;
			i.SkipTradesBeyondLimit = false;
		};

		var first = Buy(limit, folder);
		StreamBar(first, new[] { 103.5m, 104, 110, 109 });
		StreamBar(first, new[] { 103.5m, 95, 88, 83.5m, 83 });                // -$415: the day is over

		// ATAS restarts during the day: a new indicator reads the day back from the log
		var second = Buy(limit, folder);
		Check(DayPnl(second, paper: true) == -415, $"restored: {DayPnl(second, paper: true)}");
		OpenBar(second, 103.5m);

		var rows = Rows(second);
		Check(rows.Last(r => r["event"] == "RESTORE")["note"] == "1 closed trade today, -$415.00: read back from this log", "a RESTORE row");
		Check(rows.Last(r => r["event"] == "SKIPPED")["note"] == "the daily loss limit is reached (-$415.00 today, limit -$400.00)", "the day stays stopped");
		Check(rows.Count(r => r["event"] == "BREAKER") == 1, "without tripping twice");

		// live counts apart from paper
		var live = Buy(Live(new FakeBroker(), limit), folder);
		Check(DayPnl(live, paper: false) == 0, "the live day doesn't see the paper loss");
	}

	private static void LogKeepsItsColumns()
	{
		// today's file was started by an earlier version, without the ev_worst_ticks column, and holds
		// a -$100 trade: new rows keep the file's columns, and the day is read back from it
		var folder = NewFolder();
		Directory.CreateDirectory(folder);
		var old = ExecutionColumns().Where(c => c != "ev_worst_ticks").ToList();
		var closed = old.Select(c => c == "event" ? "CLOSED" : c == "pnl_usd" || c == "day_pnl_usd" ? "-100.00" : string.Empty);
		File.WriteAllText(Path.Combine(folder, "2026-03-02_NQ_paper.csv"),
			string.Join(",", old) + Environment.NewLine + string.Join(",", closed) + Environment.NewLine, new UTF8Encoding(true));

		var ind = Buy(folder: folder);
		OpenBar(ind, 103.5m);
		var rows = Rows(ind);
		Check(DayPnl(ind, paper: true) == -100 && rows.Any(r => r["event"] == "RESTORE"), $"the day is read back: {DayPnl(ind, paper: true)}");
		Check(Only(rows, "SIGNAL")["setup"] == "FVG" && !rows[0].ContainsKey("ev_worst_ticks"), "new rows under the file's own columns");

		// the next day's file is new, with every column
		StreamPattern(ind, 60, Start.Date.AddHours(23).AddMinutes(5));
		var header = File.ReadLines(Path.Combine(folder, "2026-03-03_NQ_paper.csv")).First().TrimStart('﻿');
		Check(header == string.Join(",", ExecutionColumns()), $"a new file has today's columns: {header}");
	}

	private static void LogReport()
	{
		// one BUY that hits its take profit, and a second one hidden while it was open
		var ind = Buy(i =>
		{
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly;
			i.EnableShortSignals = false;
		});
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });                      // in at 103.75
		StreamPattern(ind, 105);                                              // a second BUY, hidden: skipped
		StreamBar(ind, new[] { 108.5m, 115, 124 });                           // the first one's TP: +79t, $390

		var report = ExecutionLogReport.Build(Rows(ind));
		Check(report.Total.Count == 1 && report.Total.Tp == 1 && report.Total.Ticks == 79 && report.Total.Dollars == 390 && report.Taken == 1,
			$"one trade, TP, +79t, $390: {report.Total.Count} {report.Total.Ticks} {report.Total.Dollars}");
		Check(report.BySetup.Single().Name == "FVG" && report.ByHour.Single().Name == "09:00" && report.ByConfirmations.Single().Name == "2 confirmations",
			"by setup, hour and confirmations");
		Check(report.Skipped.Count == 1 && report.Skipped[0] == ("hidden on the chart", 1), $"the skip, without its details: {report.Skipped.FirstOrDefault()}");

		var text = ExecutionLogReport.Format(report, 1);
		Check(text.StartsWith("Execution log: paper, 2026-03-02 to 2026-03-02, 1 trading day, 1 file\n1 closed trade, 0 entries not filled, 1 signal skipped\n", StringComparison.Ordinal)
			&& text.Contains("The trades made +79.0 ticks each (+$390.00 after commission)"), $"the report:\n{text}");
	}

	private static void SignalHours()
	{
		Action<FvgReactionLiquiditySweep> regular = i => i.SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.RegularHours;

		// the hammer at 08:49 New York: before regular hours
		var early = LiveIndicator(FvgSetup(Start.AddHours(-1)), regular);
		StreamBar(early, Hammer, delta: 50);
		OpenBar(early, 103.5m);
		Check(Trades(early).Count == 0 && !Rows(early).Any(r => r["event"] == "SIGNAL" || r["event"] == "SKIPPED"), "no signal before 09:30, nothing to execute");

		var open = LiveIndicator(FvgSetup(), regular);
		StreamBar(open, Hammer, delta: 50);
		OpenBar(open, 103.5m);
		Check(Rows(open).Any(r => r["event"] == "SIGNAL"), "at 09:49 it is taken");
	}

	private static void OneTradeAtATime()
	{
		// a second BUY, from a gap and a hammer at 105, while the first position is open
		Action<FvgReactionLiquiditySweep> buysOnly = i =>
		{
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly;
			i.EnableShortSignals = false;
		};

		var one = Buy(buysOnly);
		StreamBar(one, new[] { 103.5m, 104, 110, 109 });
		StreamPattern(one, 105);
		var trades = Trades(one);
		Check(trades.Count == 2 && trades[0].Outcome == "Open" && trades[0].IsShown && !trades[1].IsShown, "the chart hides the second BUY");
		Check(Only(Rows(one), "SKIPPED")["note"] == "hidden on the chart (One trade at a time, Min TP probability or Min expected ticks)", "so it is skipped");

		var overlapping = Buy(i =>
		{
			buysOnly(i);
			i.OneTradeAtATime = false;
		});
		StreamBar(overlapping, new[] { 103.5m, 104, 110, 109 });
		StreamPattern(overlapping, 105);
		trades = Trades(overlapping);
		Check(trades.Count == 2 && trades[1].IsShown, "without One trade at a time the chart shows it");
		Check(Only(Rows(overlapping), "SKIPPED")["note"] == "a position is already open (execution holds one at a time)", "the executor still holds one position");
		Check(Rows(overlapping).Count(r => r["event"] == "FILL" && r["order"] == "entry") == 1, "one entry");
	}

	private static void TimeExits()
	{
		var aged = Buy(i => i.MaxBarsInTrade = 2);
		StreamBar(aged, new[] { 103.5m, 104, 105, 104.5m });                  // 20
		StreamBar(aged, new[] { 104.5m, 105, 104, 104.75m });                 // 21: two bars since the fill when it closes
		OpenBar(aged, 104.75m);                                               // 22

		var rows = Rows(aged);
		Check(Only(rows, "CHART")["chart_outcome"] == "EXP" && Trades(aged).Single().Outcome == "Expired", "the chart's trade expired");
		Check(Only(rows, "FILL", "exit")["price"] == "104.5" && Only(rows, "CLOSED")["pnl_usd"] == "10.00", "closed at market: +3t, $15 less $5");

		// the last bar of the session: the chart closes its trades there, the position with them
		var session = Buy(i => i.ExpireAtSessionEnd = true);
		session.SessionStarts.Add(22);
		StreamBar(session, new[] { 103.5m, 104, 105, 104.5m });               // 20
		StreamBar(session, new[] { 104.5m, 105, 104, 104.75m });              // 21: the session's last
		OpenBar(session, 106);                                                // 22 opens the next

		rows = Rows(session);
		Check(Only(rows, "CHART")["chart_outcome"] == "EXP" && Only(rows, "FILL", "exit")["price"] == "105.75",
			$"closed at the next session's first price: {Describe(rows)}");
	}

	private static void DailyProfitTarget()
	{
		// a +70t target: the TP at +79t reaches it
		var ind = Buy(i =>
		{
			i.DailyProfitTargetTicks = 70;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly;
			i.EnableShortSignals = false;
		});
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });
		StreamBar(ind, new[] { 109m, 115, 124 });                             // TP: +79t

		var rows = Rows(ind);
		var target = Only(rows, "TARGET");
		Check(rows.IndexOf(target) == rows.IndexOf(Only(rows, "CLOSED")) + 1
			&& target["note"] == "daily profit target reached: +79t today, target +70t; no new entries until the next trading day",
			$"the target right after the close that reached it: {target["note"]}");
		Check(ind.Alerts.Contains("Daily profit target reached (+79t today): no new paper entries until the next trading day"), "an alert says so");
		Check(Panel(ind).Contains("Execution PAPER done for today: +79t reached the +70t profit target"), "and the panel");

		// the same day, another BUY: skipped
		StreamPattern(ind, 130);
		var skipped = Only(Rows(ind), "SKIPPED");
		Check(skipped["note"] == "the daily profit target is reached (+79t today, target +70t)", $"skipped: {skipped["note"]}");

		// a restart the same day reads the day's ticks back
		var restarted = Buy(i => i.DailyProfitTargetTicks = 70, ind.ExecutionLogFolder);
		OpenBar(restarted, 103.5m);
		Check(Rows(restarted).Last(r => r["event"] == "SKIPPED")["note"] == "the daily profit target is reached (+79t today, target +70t)",
			"still reached after a restart");

		// the next trading day starts from zero
		StreamPattern(ind, 40, Start.Date.AddHours(23).AddMinutes(5));
		Check(Rows(ind).Count(r => r["event"] == "SIGNAL") == 2, $"the next day's BUY is taken: {Trail(Rows(ind))}");
	}

	private static void CostsByInstrument()
	{
		// the product in the names data feeds give their contracts
		var productOf = IndicatorType.GetMethod("ProductOf", BindingFlags.NonPublic | BindingFlags.Static);
		string Product(string symbol) => (string)productOf.Invoke(null, new object[] { symbol });
		Check(Product("NQZ6") == "NQ" && Product("MNQZ26") == "MNQ" && Product("NQ 12-26") == "NQ" && Product("NQZ6.CME@RITHMIC") == "NQ"
			&& Product("F.US.ENQZ26") == "NQ" && Product("mesh7") == "MES" && Product("M2KZ6") == "M2K" && Product("NQ") == "NQ",
			"NQ, MNQ, MES and M2K, whatever the feed calls them");
		Check(Product("6EZ6") == null && Product("XYZ") == null && Product("") == null && Product(null) == null, "and nothing it doesn't know");

		void Mnq(FvgReactionLiquiditySweep i) => i.InstrumentInfo = new InstrumentInfo { TickSize = Tick, Instrument = "MNQZ6" };

		// an MNQ chart: $0.50 a tick and $1.50 a round trip, whatever the two settings say
		var mnq = Buy(Mnq);
		StreamBar(mnq, new[] { 103.5m, 104, 110, 109 });
		StreamBar(mnq, new[] { 109m, 115, 124 });                             // TP: +79t
		var rows = Rows(mnq);
		var mode = Only(rows, "MODE")["note"];
		Check(Only(rows, "CLOSED")["pnl_usd"] == "38.00", $"+79t x $0.50 - $1.50 = $38.00: {Only(rows, "CLOSED")["pnl_usd"]}");
		Check(mode.Contains(" · tick value $0.50 · commission $1.50 · ") && mode.EndsWith(" · MNQ's tick value and commission", StringComparison.Ordinal),
			$"the MODE row: {mode}");

		// the risk of a trade in MNQ dollars: 82 ticks x $0.50 + $1.50
		var tight = Buy(i =>
		{
			Mnq(i);
			i.DailyLossLimit = 40;
		});
		OpenBar(tight, 103.5m);
		Check(Only(Rows(tight), "SKIPPED")["note"] == "its stop could lose $42.50, more than the $40.00 left before the daily loss limit",
			$"skipped for its MNQ risk: {Describe(Rows(tight))}");

		// switched off: the two settings
		var off = Buy(i =>
		{
			Mnq(i);
			i.CostsFromInstrument = false;
			i.TickValue = 0.5m;
			i.CommissionPerContract = 1.24m;
		});
		StreamBar(off, new[] { 103.5m, 104, 110, 109 });
		StreamBar(off, new[] { 109m, 115, 124 });
		Check(Only(Rows(off), "CLOSED")["pnl_usd"] == "38.26" && !Only(Rows(off), "MODE")["note"].Contains("MNQ's"), "the settings when switched off");

		// an instrument it doesn't know, or one whose tick size isn't the contract's: the settings, and the MODE row says why
		var unknown = Buy(i => i.InstrumentInfo = new InstrumentInfo { TickSize = Tick, Instrument = "ABCZ6" });
		Check(Only(Rows(unknown), "MODE")["note"].EndsWith(" · tick value and commission from the settings: ABCZ6 is not a contract the executor knows", StringComparison.Ordinal),
			$"an unknown instrument: {Only(Rows(unknown), "MODE")["note"]}");

		var odd = NewIndicator(i => i.InstrumentInfo = new InstrumentInfo { TickSize = 0.5m, Instrument = "MNQZ6" });
		Check(IndicatorType.GetMethod("EffectiveCommission", Private).Invoke(odd, null) is decimal c && c == 5,
			"MNQ with a 0.50 tick is not trusted to be MNQ");
	}

	private static void DayTrend()
	{
		// the scripted BUY at 103.50 comes with the day going up: above its 09:30 open (100) and VWAP
		var buy = Buy(i => i.OnlyWithDayTrend = true);
		OpenBar(buy, 103.5m);
		Check(Rows(buy).Any(r => r["event"] == "SIGNAL"), "the BUY is taken");
		Check(Panel(buy).Any(s => s.StartsWith("Day trend up (above the open and VWAP): buys only · open 100.00 · VWAP ", StringComparison.Ordinal)),
			$"the panel: {string.Join(" | ", Panel(buy).Where(s => s.Contains("trend")))}");

		// the scripted SHORT closes at 100.25: under the VWAP, but still over the day's open - no
		// clear trend, so no SHORT
		var shortSide = LiveIndicator(FvgSetup(), i => i.OnlyWithDayTrend = true);
		StreamBar(shortSide, new[] { 102.75m, 103.25m, 102, 103 });           // 19
		StreamBar(shortSide, new[] { 103m, 103.25m, 100, 100.25m }, delta: -30); // 20
		OpenBar(shortSide, 100.25m);
		Check(Trades(shortSide).Count == 0 && !Rows(shortSide).Any(r => r["event"] == "SIGNAL" || r["event"] == "SKIPPED"),
			"no SHORT on a day that isn't going down");
		Check(Panel(shortSide).Any(s => s.StartsWith("No clear day trend (between the open and VWAP): no signals", StringComparison.Ordinal)), "the panel says why");
	}

	private static void BreakEvenFromTheFill()
	{
		// filled at 103.75, a tick above the signal's 103.50: the stop moves to 108.75, +20t from the fill
		var ind = Buy(i => i.BreakEvenFromFill = true);
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });                      // 20: in at 103.75
		StreamBar(ind, new[] { 109m, 115, 110, 108.75m, 108.5m });            // 21: +46t, then back down

		var rows = Rows(ind);
		var modify = Only(rows, "MODIFY", "stop");
		Check(modify["price"] == "108.75" && modify["note"] == "break-even: +40t reached, the stop moves to +20t from the fill",
			$"the stop moves to 108.75: {modify["price"]} ({modify["note"]})");
		Check(Only(rows, "FILL", "stop")["price"] == "108.5", "and fills a tick worse, at 108.50");

		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "19" && closed["pnl_usd"] == "90.00", $"+19t, where the signal's break-even price made +18t: {closed["pnl_ticks"]}");
		Check(Only(rows, "MODE")["note"].Contains(" · break-even counted from the fill · "), "the MODE row says so");

		// live, two contracts filled at 103.75 and 104.00: +20t from their 103.875 average, up to the
		// next tick, is 109.00
		var broker = new FakeBroker();
		var live = Buy(Live(broker, i =>
		{
			i.Contracts = 2;
			i.BreakEvenFromFill = true;
		}));
		var bar = OpenBar(live, 103.5m);
		broker.Fill(live, broker.Calls[0].Order, 103.75m, 1, "F1");
		broker.Fill(live, broker.Calls[0].Order, 104, 1, "F2");
		broker.Position.Volume = 2;
		AddTick(live, bar, 115);
		var moved = broker.Calls.Last(c => c.Name == "modify");
		Check(moved.NewOrder.Type == OrderTypes.Stop && moved.NewOrder.TriggerPrice == 109 && moved.NewOrder.QuantityToFill == 2,
			$"the live stop moves to 109.00: {moved.NewOrder.TriggerPrice}");
	}

	private static void FlatBy()
	{
		// flat by 09:52, entries until 09:51: the 09:50 BUY goes in, and out at 09:52 whatever the chart does
		var ind = Buy(i =>
		{
			i.FlatByTime = new TimeSpan(9, 52, 0);
			i.FlatByLastEntryMinutes = 1;
			i.OneTradeAtATime = false;
			i.SignalSource = FvgReactionLiquiditySweep.SignalMode.FvgReactionOnly;
			i.EnableShortSignals = false;
		});
		var mode = Only(Rows(ind), "MODE")["note"];
		Check(mode.Contains(" · overlapping signals on the chart · flat by 09:52 New York, last entry 09:51 · "), $"the MODE row names it: {mode}");

		StreamBar(ind, new[] { 103.5m, 104, 106, 105 });                      // 20 (09:50): in at 103.75
		StreamBar(ind, new[] { 105m, 106, 104, 105.5m });                     // 21 (09:51)
		OpenBar(ind, 105.5m);                                                 // 22 (09:52): flat

		var rows = Rows(ind);
		Check(Trail(rows) == "SIGNAL ORDER:entry FILL:entry ORDER:stop ORDER:take_profit EXIT CANCEL:take_profit CANCELLED:take_profit CANCEL:stop "
			+ "CANCELLED:stop ORDER:exit FILL:exit CLOSED", $"order trail: {Trail(rows)}");
		Check(Only(rows, "EXIT")["note"] == "flat by 09:52 New York", $"closed for the flat-by time: {Only(rows, "EXIT")["note"]}");

		var closed = Only(rows, "CLOSED");
		Check(Only(rows, "FILL", "exit")["price"] == "105.25" && closed["pnl_ticks"] == "6" && closed["pnl_usd"] == "25.00" && closed["chart_outcome"] == "OPEN",
			$"at market, +6t ($25), with the chart's trade still open: {closed["pnl_ticks"]}t {closed["chart_outcome"]}");
		Check(Trades(ind).Single().Outcome == "Open", "the chart's trade carries on");

		// a later BUY the same day: past the last entry
		StreamPattern(ind, 60);
		var skipped = Only(Rows(ind), "SKIPPED");
		Check(skipped["note"] == "no new entries from 09:51 New York (flat by 09:52) until the trading day turns at 18:00", $"skipped: {skipped["note"]}");

		// the next trading day, from 18:00 New York: taken again
		StreamPattern(ind, 40, Start.Date.AddHours(23).AddMinutes(5));
		Check(Rows(ind).Count(r => r["event"] == "SIGNAL") == 2, $"the next day's BUY is taken: {Trail(Rows(ind))}");

		// a limit entry still waiting at the flat-by time is cancelled
		var waiting = Buy(i =>
		{
			i.Entry = FvgReactionLiquiditySweep.EntryRule.LimitPullback;
			i.FlatByTime = new TimeSpan(9, 51, 0);
			i.FlatByLastEntryMinutes = 0;
		});
		OpenBar(waiting, 103.5m);                                             // 20 (09:50): the limit waits at 103.00
		OpenBar(waiting, 103.75m);                                            // 21 (09:51): flat
		rows = Rows(waiting);
		Check(Trail(rows) == "SIGNAL ORDER:entry CANCEL:entry CANCELLED:entry NOFILL" && Only(rows, "CANCEL", "entry")["note"] == "flat by 09:51 New York",
			$"the waiting entry is cancelled: {Trail(rows)}");

		// a flat-by time in CME's daily break, when nothing trades: the position closes at the new
		// trading day's first price
		var late = LiveIndicator(FvgSetup(Start.AddHours(7)), i =>
		{
			i.FlatByTime = new TimeSpan(17, 30, 0);
			i.FlatByLastEntryMinutes = 10;
		});
		StreamBar(late, Hammer, delta: 50);                                   // 19 (16:49)
		StreamBar(late, new[] { 103.5m, 104, 105 });                          // 20 (16:50): in
		OpenBar(late, 105, Start.Date.AddHours(23).AddMinutes(1));            // 18:01 New York: the next trading day
		rows = Rows(late);
		Check(Only(rows, "EXIT")["note"] == "flat by 17:30 New York (the trading day turned before it)" && rows.Any(r => r["event"] == "CLOSED"),
			$"closed as the day turned: {Trail(rows)}");
	}

	private static void HistoryNeverTrades()
	{
		var bars = FvgSetup();
		bars.Add(Bar(103, 103.5m, 101.75m, 103.5m, 50));                      // 19: the hammer, in history
		bars.Add(Bar(103.5m, 110, 103, 109));
		bars.Add(Bar(109, 124, 108, 123));
		bars.Add(Bar(123, 124, 122, 123.5m));
		Space(bars, Start);

		var ind = LiveIndicator(bars);
		Check(Trades(ind).Count == 1 && Trades(ind)[0].Outcome == "TakeProfit", "the chart has its BUY, settled on history");
		Check(Trail(Rows(ind)).Length == 0 && Rows(ind).Count == 1, "only the MODE row: no order for a signal in history");

		StreamBar(ind, new[] { 123.5m, 124, 123, 123.25m });
		Check(Trail(Rows(ind)).Length == 0, "nor once real time runs");
	}

	private static void RecalculationKeepsThePosition()
	{
		var ind = Buy();
		StreamBar(ind, new[] { 103.5m, 104, 110, 109 });                      // in: BUY 1 @ 103.75

		// a settings change recalculates the chart; this one even takes the BUY off it
		ind.EnableBuySignals = false;
		Recalculate(ind);
		Check(Trades(ind).Count == 0, "the chart no longer has the BUY");
		Check(Rows(ind).Count(r => r["event"] == "ORDER") == 3, "the position and its two orders are still there");

		StreamBar(ind, new[] { 109m, 115, 110, 108.5m, 108 });                // +46t, then back to the break-even stop
		var rows = Rows(ind);
		Check(Only(rows, "MODIFY", "stop")["price"] == "108.5", "the stop still moves to break-even");
		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_ticks"] == "18" && closed["chart_outcome"] == "BE", $"and the position ends on it, as the signal's trade did: {closed["chart_outcome"]}");
	}

	private static void PanelAndAlerts()
	{
		var ind = Buy(i => i.DailyLossLimit = 1000);
		Check(Panel(ind).Contains("Execution PAPER · 1 contract · today $0.00 of -$1,000.00"), $"the execution line: {string.Join(" | ", Panel(ind).Where(s => s.StartsWith("Exec")))}");

		var bar = OpenBar(ind, 103.5m);
		AddTick(ind, bar, 106);
		var panel = Panel(ind);
		Check(panel.Contains("Paper BUY 1 @ 103.75 · TP 123.50 · stop 83.50 · +9t"), $"the position: {string.Join(" | ", panel.Where(s => s.StartsWith("Paper")))}");
		Check(panel.Contains("Last: Paper BUY 1 filled @ 103.75"), "the last event");
		Check(ind.Alerts.Contains("Paper BUY 1 filled @ 103.75"), "an alert on the fill");

		AddTick(ind, bar, 115);
		Check(Panel(ind).Contains("Paper BUY 1 @ 103.75 · TP 123.50 · stop 108.50 (break-even) · +45t"), "the stop at break-even");

		AddTick(ind, bar, 124);
		Check(ind.Alerts.Contains("Paper BUY closed +79t, $390.00 · today $390.00"), $"an alert on the close: {string.Join(" | ", ind.Alerts)}");
		Check(Panel(ind).Contains("Execution PAPER · 1 contract · today $390.00 of -$1,000.00"), "the day on the panel");

		var quiet = Buy(i => i.AlertOnOrders = false);
		var quietBar = OpenBar(quiet, 103.5m);
		AddTick(quiet, quietBar, 124);
		Check(quiet.Alerts.Count == 0, "no alerts on orders when switched off");

		// switched off with the position open: no new entries, the position is still managed
		var managed = Buy();
		var managedBar = OpenBar(managed, 103.5m);
		managed.ExecuteSignals = false;
		AddTick(managed, managedBar, 104);
		Check(Panel(managed).Any(s => s.StartsWith("Execution off · ", StringComparison.Ordinal)), "the panel says execution is off");
		AddTick(managed, managedBar, 124);
		Check(Rows(managed).Any(r => r["event"] == "CLOSED"), "and closes the open position all the same");
		Check(!Panel(managed).Any(s => s.StartsWith("Execution", StringComparison.Ordinal)), "then the lines go");
	}

	#endregion

	#region Live, against a fake broker

	private static void LiveNotArmed()
	{
		string Skipped(FakeBroker broker, Action<FvgReactionLiquiditySweep> more)
		{
			var ind = Buy(Live(broker, more));
			OpenBar(ind, 103.5m);
			Check(broker == null || broker.Calls.Count == 0, "no order call");
			return Rows(ind, "live").FirstOrDefault(r => r["event"] == "SKIPPED")?["note"];
		}

		Check(Skipped(new FakeBroker(), i => i.LiveAccount = string.Empty) == "live trading is not armed: Live account is empty", "without a Live account");
		Check(Skipped(new FakeBroker(), i => i.LiveAccount = "FUNDED-7") == "live trading is not armed: the chart's account (SIM-1) is not the Live account (FUNDED-7)",
			"on another account than the chart's");
		Check(Skipped(new FakeBroker(), i => i.DailyLossLimit = 0) == "live trading is not armed: live trading needs a daily loss limit", "without a daily loss limit");
		Check(Skipped(null, null) == "live trading is not armed: the chart has no trading connection", "without a connection");
		Check(Skipped(new FakeBroker { Security = new Security { TickSize = 0.5m, TickCost = 5 } }, null)
			== "live trading is not armed: the instrument's tick size (0.5) is not the chart's", "on an instrument that isn't the chart's");
		Check(Skipped(new FakeBroker { Position = new Position { Volume = 2 } }, null) == "the account already holds a position (2) in this instrument",
			"when the account already holds a position");

		var panel = Panel(Buy(Live(new FakeBroker(), i => i.LiveAccount = string.Empty)));
		Check(panel.Contains("Execution LIVE not armed: Live account is empty"), "the panel says why");

		// the account matches whatever its case and spaces
		var armed = Buy(Live(new FakeBroker(), i => i.LiveAccount = "  sim-1 "));
		Check(armed.LiveAccount == "sim-1" && !Panel(armed).Any(s => s.Contains("not armed")), "armed");
	}

	private static void LiveBracket()
	{
		var broker = new FakeBroker();
		var ind = Buy(Live(broker));
		var bar = OpenBar(ind, 103.5m);                                       // 20: BUY

		Check(broker.Calls.Count == 1, $"one call: {broker.Calls.Count}");
		var entry = broker.Calls[0];
		Check(entry.Name == "open" && entry.Order.Direction == OrderDirections.Buy && entry.Order.Type == OrderTypes.Market && entry.Order.QuantityToFill == 1,
			"a market BUY for 1");
		Check(entry.Order.Portfolio == broker.Portfolio && entry.Order.Security == broker.Security, "on the chart's account and instrument");
		Check(!entry.AskConfirmation && !entry.SetDefaultQuantity, "without a confirmation dialog, at its own size");
		Check(entry.Order.Comment == "FVG 20260302-094900-BUY entry", $"named after its signal: {entry.Order.Comment}");
		Check(!Rows(ind, "live").Any(r => r["event"] == "FILL"), "nothing filled until the broker says so");

		broker.Fill(ind, entry.Order, 103.75m, 1, "F1");
		broker.Position.Volume = 1;
		Check(broker.Calls.Count == 3, $"then two more calls: {broker.Calls.Count}");
		var stop = broker.Calls[1].Order;
		var target = broker.Calls[2].Order;
		Check(stop.Type == OrderTypes.Stop && stop.TriggerPrice == 83.5m && stop.Direction == OrderDirections.Sell && stop.QuantityToFill == 1,
			"first a sell stop at 83.50");
		Check(target.Type == OrderTypes.Limit && target.Price == 123.5m && target.Direction == OrderDirections.Sell && target.QuantityToFill == 1,
			"then a sell limit at 123.50");
		Check(stop.OCOGroup != null && stop.OCOGroup == target.OCOGroup, "one OCO group");

		AddTick(ind, bar, 110);
		AddTick(ind, bar, 115);
		var modify = broker.Calls.Single(c => c.Name == "modify");
		Check(modify.Order == stop && modify.NewOrder.TriggerPrice == 108.5m && modify.NewOrder.QuantityToFill == 1 && !modify.AskConfirmation,
			"ModifyOrderAsync moves the stop to 108.50");

		// the broker's fill report can come before the chart sees the print
		broker.Fill(ind, target, 123.5m, 1, "F2");
		broker.Position.Volume = 0;
		var cancel = broker.Calls.Last();
		Check(cancel.Name == "cancel" && cancel.Order == modify.NewOrder && !cancel.AskConfirmation, "the stop left over is cancelled");
		Check(!Rows(ind, "live").Any(r => r["event"] == "CLOSED"), "the position closes once the stop is gone");

		broker.ConfirmCancel(ind, modify.NewOrder);
		AddTick(ind, bar, 123.5m);
		var rows = Rows(ind, "live");
		var closed = Only(rows, "CLOSED");
		Check(closed["pnl_usd"] == "390.00" && closed["mode"] == "live" && closed["account"] == "SIM-1", $"closed: {closed["pnl_usd"]} on {closed["account"]}");
		Check(Trail(rows) == "SIGNAL ORDER:entry FILL:entry ORDER:stop ORDER:take_profit MODIFY:stop FILL:take_profit CANCEL:stop CANCELLED:stop CLOSED CHART",
			$"order trail: {Trail(rows)}");
		Check(broker.Calls.Count == 5, "and nothing more");
		Check(!Directory.GetFiles(ind.ExecutionLogFolder).Any(f => f.EndsWith("_paper.csv", StringComparison.Ordinal)), "nothing in a paper log");
		Check(DayPnl(ind, paper: false) == 390 && DayPnl(ind, paper: true) == 0, "counted on the live day");

		// the chart sees its TP touched first: the executor closes, racing the broker's own TP fill
		broker = new FakeBroker();
		ind = Buy(Live(broker));
		bar = OpenBar(ind, 103.5m);
		broker.Fill(ind, broker.Calls[0].Order, 103.75m, 1, "F1");
		broker.Position.Volume = 1;
		stop = broker.Calls[1].Order;
		target = broker.Calls[2].Order;
		AddTick(ind, bar, 123.5m);
		Check(broker.Calls.Count == 5 && broker.Calls[3].Name == "cancel" && broker.Calls[3].Order == target && broker.Calls[4].Name == "cancel"
			&& broker.Calls[4].Order == stop, "the chart's trade ended: its bracket is cancelled, to close at market");

		// too late to cancel: the broker filled it, and says so before its fill report comes
		target.Unfilled = 0;
		target.State = OrderStates.Done;
		ind.HarnessOrderCancelFailed(target, "order already filled");
		Check(broker.Calls.Count == 5, "a take profit that filled isn't asked to cancel again");
		broker.Fill(ind, target, 123.5m, 1, "F2");
		broker.Position.Volume = 0;
		broker.ConfirmCancel(ind, stop);
		rows = Rows(ind, "live");
		Check(broker.Calls.Count == 5, "no closing order once the take profit has closed it");
		Check(Only(rows, "CLOSED")["pnl_usd"] == "390.00" && !rows.Any(r => r["event"] == "HALT"), $"closed on the TP, no halt: {Trail(rows)}");

		// a cancel refused while the broker still shows the order working is asked again
		broker = new FakeBroker();
		ind = Buy(Live(broker));
		bar = OpenBar(ind, 103.5m);
		broker.Fill(ind, broker.Calls[0].Order, 103.75m, 1, "F1");
		broker.Position.Volume = 1;
		target = broker.Calls[2].Order;
		AddTick(ind, bar, 123.5m);
		ind.HarnessOrderCancelFailed(target, "busy");
		Check(broker.Calls.Count == 6 && broker.Calls[5].Name == "cancel" && broker.Calls[5].Order == target, "asked again");
	}

	private static void LiveFillReports()
	{
		// the order says it filled before its fill comes: the executor waits for the fill
		var broker = new FakeBroker();
		var ind = Buy(Live(broker));
		OpenBar(ind, 103.5m);
		var entry = broker.Calls[0].Order;
		entry.Unfilled = 0;
		entry.State = OrderStates.Done;
		ind.HarnessOrderChanged(entry);
		Check(broker.Calls.Count == 1, "no bracket before the fill itself");

		var fill = new MyTrade { Id = "F1", Order = entry, OrderId = entry.Id, Price = 103.75m, Volume = 1, OrderDirection = OrderDirections.Buy };
		ind.HarnessMyTrade(fill);
		Check(broker.Calls.Count == 3, "the fill brings the bracket");

		ind.HarnessMyTrade(fill);
		Check(broker.Calls.Count == 3 && Rows(ind, "live").Count(r => r["event"] == "FILL") == 1 && !Rows(ind, "live").Any(r => r["event"] == "WARN"),
			"the same fill twice counts once, known for what it is");

		// a fill of someone else's order is none of the executor's business
		ind.HarnessMyTrade(new MyTrade { Id = "X1", Order = new Order { Id = "MANUAL-1" }, OrderId = "MANUAL-1", Price = 104, Volume = 1 });
		Check(broker.Calls.Count == 3 && Rows(ind, "live").Count(r => r["event"] == "FILL") == 1, "a manual trade's fill is ignored");

		// a partial fill reported twice must not grow the position (on a new chart: the one above,
		// still holding its position on the account, is removed first)
		ind.HarnessDispose();
		broker = new FakeBroker();
		ind = Buy(Live(broker, i => i.Contracts = 2));
		OpenBar(ind, 103.5m);
		entry = broker.Calls[0].Order;
		broker.Fill(ind, entry, 103.75m, 1, "P1");
		ind.HarnessMyTrade(new MyTrade { Id = "P1", Order = entry, OrderId = entry.Id, Price = 103.75m, Volume = 1, OrderDirection = OrderDirections.Buy });
		Check(broker.Calls.Count == 3 && !broker.Calls.Any(c => c.Name == "modify") && Rows(ind, "live").Last(r => r["event"] == "FILL")["position"] == "1",
			"1 of 2 filled, reported twice: still 1 held, the bracket for 1");
	}

	private static void LivePartialFills()
	{
		var broker = new FakeBroker();
		var ind = Buy(Live(broker, i => i.Contracts = 2));
		OpenBar(ind, 103.5m);
		var entry = broker.Calls[0].Order;
		Check(entry.QuantityToFill == 2, "a market BUY for 2");

		broker.Fill(ind, entry, 103.75m, 1, "F1");
		Check(broker.Calls.Count == 3 && broker.Calls[1].Order.QuantityToFill == 1 && broker.Calls[2].Order.QuantityToFill == 1, "half filled: a bracket for 1");

		broker.Fill(ind, entry, 104, 1, "F2");
		var resized = broker.Calls.Where(c => c.Name == "modify").ToList();
		Check(resized.Count == 2 && resized.All(c => c.NewOrder.QuantityToFill == 2) && resized[0].Order == broker.Calls[1].Order
			&& resized[0].NewOrder.TriggerPrice == 83.5m && resized[1].NewOrder.Price == 123.5m, "all filled: both resized to 2, at the same prices");

		broker.Fill(ind, resized[1].NewOrder, 123.5m, 2, "F3");
		broker.ConfirmCancel(ind, resized[0].NewOrder);
		var closed = Only(Rows(ind, "live"), "CLOSED");
		Check(closed["pnl_ticks"] == "78.5" && closed["pnl_usd"] == "775.00", $"78.5t a contract on 2 from 103.875: {closed["pnl_ticks"]}t {closed["pnl_usd"]}");
	}

	private static void LiveRejectedStop()
	{
		var broker = new FakeBroker();
		var ind = Buy(Live(broker, i => i.OneTradeAtATime = false));
		var bar = OpenBar(ind, 103.5m);
		broker.Fill(ind, broker.Calls[0].Order, 103.75m, 1, "F1");
		var stop = broker.Calls[1].Order;
		var target = broker.Calls[2].Order;

		ind.HarnessOrderRegisterFailed(stop, "stop price not valid");
		Check(broker.Calls.Count == 4 && broker.Calls[3].Name == "cancel" && broker.Calls[3].Order == target, "unprotected: the take profit is cancelled first");
		Check(ind.Alerts.Any(a => a == "Execution halted: the stop order was rejected (stop price not valid): the unprotected position is closed at market"),
			$"an alert: {string.Join(" | ", ind.Alerts)}");

		broker.ConfirmCancel(ind, target);
		var exit = broker.Calls.Last();
		Check(exit.Name == "open" && exit.Order.Type == OrderTypes.Market && exit.Order.Direction == OrderDirections.Sell && exit.Order.QuantityToFill == 1,
			"then it closes at market");

		broker.Fill(ind, exit.Order, 103.5m, 1, "F2");
		var closed = Only(Rows(ind, "live"), "CLOSED");
		Check(closed["pnl_ticks"] == "-1" && closed["pnl_usd"] == "-10.00", $"-1t, -$10: {closed["pnl_usd"]}");

		// halted: the next signal is skipped until Execute signals is switched off and on
		AddTick(ind, bar, 109);
		StreamPattern(ind, 105);
		Check(Rows(ind, "live").Last(r => r["event"] == "SKIPPED")["note"].StartsWith("execution is halted: the stop order was rejected", StringComparison.Ordinal),
			"halted");
		Check(Panel(ind).Any(s => s.StartsWith("Execution HALTED: ", StringComparison.Ordinal)), "the panel says so");

		ind.ExecuteSignals = false;
		ind.ExecuteSignals = true;
		Check(!Panel(ind).Any(s => s.Contains("HALTED")), "off and on again resumes");
	}

	private static void LiveClosedOutside()
	{
		var broker = new FakeBroker();
		var ind = Buy(Live(broker));
		var bar = OpenBar(ind, 103.5m);
		broker.Fill(ind, broker.Calls[0].Order, 103.75m, 1, "F1");
		broker.Position.Volume = 1;
		AddTick(ind, bar, 104);

		// flattened by hand in ATAS: the fills of that aren't the executor's
		broker.Position.Volume = 0;
		AddTick(ind, bar, 104.25m);
		AddTick(ind, bar, 104);
		Check(!Rows(ind, "live").Any(r => r["event"] == "HALT"), "a moment without a position is no proof: fills can come after it");

		AddTick(ind, bar, 104.25m);
		AddTick(ind, bar, 104.5m);
		AddTick(ind, bar, 104.25m);
		var rows = Rows(ind, "live");
		Check(rows.Any(r => r["event"] == "HALT" && r["note"].StartsWith("the account shows no position while the indicator held one", StringComparison.Ordinal)),
			$"a few seconds without it: halted ({Describe(rows)})");
		Check(broker.Calls.Count(c => c.Name == "cancel") == 2, "its stop and take profit are cancelled");
	}

	private static void LiveStopCancelledOutside()
	{
		// cancelled by hand: a few seconds on, the position is closed at market and execution halts
		var broker = new FakeBroker();
		var ind = Buy(Live(broker));
		var bar = OpenBar(ind, 103.5m);
		broker.Fill(ind, broker.Calls[0].Order, 103.75m, 1, "F1");
		broker.Position.Volume = 1;
		var stop = broker.Calls[1].Order;
		var target = broker.Calls[2].Order;

		broker.ConfirmCancel(ind, stop);
		Check(Only(Rows(ind, "live"), "CANCELLED", "stop")["note"].StartsWith("not asked for by the indicator", StringComparison.Ordinal),
			"logged as a cancel the executor didn't ask for");

		AddTick(ind, bar, 104);
		AddTick(ind, bar, 104.25m);
		Check(broker.Calls.Count == 3 && !Rows(ind, "live").Any(r => r["event"] == "HALT"), "a moment without the stop is no proof: its take profit may just have filled");

		AddTick(ind, bar, 104);
		AddTick(ind, bar, 104.25m);
		var rows = Rows(ind, "live");
		Check(rows.Any(r => r["event"] == "HALT" && r["note"] == "the position's stop was cancelled outside the indicator: the unprotected position is closed at market"),
			$"halted: {Describe(rows.Where(r => r["event"] == "HALT"))}");
		Check(broker.Calls.Count == 4 && broker.Calls[3].Name == "cancel" && broker.Calls[3].Order == target, "the take profit is cancelled");

		broker.ConfirmCancel(ind, target);
		var exit = broker.Calls.Last();
		Check(broker.Calls.Count == 5 && exit.Name == "open" && exit.Order.Type == OrderTypes.Market && exit.Order.Direction == OrderDirections.Sell,
			"then it closes at market");

		// the OCO partner: the take profit fills and the broker cancels the stop, reported first (on a
		// new chart: the one above still holds the account)
		ind.HarnessDispose();
		broker = new FakeBroker();
		ind = Buy(Live(broker));
		bar = OpenBar(ind, 103.5m);
		broker.Fill(ind, broker.Calls[0].Order, 103.75m, 1, "F1");
		broker.Position.Volume = 1;
		broker.ConfirmCancel(ind, broker.Calls[1].Order);
		AddTick(ind, bar, 110);
		broker.Fill(ind, broker.Calls[2].Order, 123.5m, 1, "F2");
		broker.Position.Volume = 0;

		for (var i = 0; i < 5; i++)
			AddTick(ind, bar, 110);

		rows = Rows(ind, "live");
		Check(!rows.Any(r => r["event"] == "HALT") && Only(rows, "CLOSED")["pnl_usd"] == "390.00" && broker.Calls.Count == 3,
			$"closed on its take profit: no halt, no extra order ({Trail(rows)})");
	}

	private static void LiveAccountPnl()
	{
		// the scripted BUY on a live chart whose account closed `account` today; null: it was taken
		string Skipped(decimal account, Action<FvgReactionLiquiditySweep> more = null)
		{
			var broker = new FakeBroker();
			broker.Portfolio.ClosedPnL = account;
			var ind = Buy(Live(broker, more));
			OpenBar(ind, 103.5m);
			var note = broker.Calls.Count > 0 ? null : Rows(ind, "live").FirstOrDefault(r => r["event"] == "SKIPPED")?["note"] ?? "no row";
			ind.HarnessDispose();
			return note;
		}

		// the account lost $1,000 today (by hand, or on another chart): the $1,000 limit is reached
		var reached = Skipped(-1000);
		Check(reached == "the account's closed P&L for the session (-$1,000.00, from the trading connection) has reached the daily loss limit (-$1,000.00)",
			$"skipped: {reached}");

		// $700 down: the trade's $415 of risk no longer fits in what is left
		var room = Skipped(-700);
		Check(room == "its stop could lose $415.00, more than the $300.00 left before the daily loss limit", $"skipped for its risk: {room}");

		// a profit elsewhere doesn't loosen the limit; switched off, only the executor's own trades count
		Check(Skipped(500) == null, "taken with the account up");
		Check(Skipped(-1000, i => i.CountAccountPnl = false) == null, "taken with the setting off");

		// the panel says why
		var down = new FakeBroker();
		down.Portfolio.ClosedPnL = -1200;
		var ind = Buy(Live(down));
		Check(Panel(ind).Contains("Execution LIVE stopped for today: the account's closed P&L -$1,200.00 reached the -$1,000.00 limit"),
			$"the panel: {string.Join(" | ", Panel(ind).Where(s => s.StartsWith("Exec", StringComparison.Ordinal)))}");
		Check(Only(Rows(ind, "live"), "MODE")["note"].Contains("the account's closed P&L counts toward the limit"), "the MODE row says it counts");

		// paper trading goes by its own trades only
		var paper = Buy(i =>
		{
			i.DailyLossLimit = 1000;
			i.HarnessTradingManager = down;
		});
		OpenBar(paper, 103.5m);
		Check(Rows(paper).Any(r => r["event"] == "SIGNAL") && !Only(Rows(paper), "MODE")["note"].Contains("closed P&L"), "paper trades all the same");
	}

	private static void LiveTimeInForce()
	{
		// the connection's default: the executor sets none
		var plain = new FakeBroker();
		var ind = Buy(Live(plain));
		OpenBar(ind, 103.5m);
		plain.Fill(ind, plain.Calls[0].Order, 103.75m, 1, "F1");
		Check(plain.Calls.Count == 3 && plain.Calls.All(c => c.Order.TimeInForce == TimeInForce.None), "left to the connection by default");
		ind.HarnessDispose();

		// good till cancelled: the stop and the take profit, not the market entry
		var gtc = new FakeBroker();
		ind = Buy(Live(gtc, i => i.LiveTimeInForce = FvgReactionLiquiditySweep.OrderLifetime.GoodTillCancel));
		var bar = OpenBar(ind, 103.5m);
		gtc.Fill(ind, gtc.Calls[0].Order, 103.75m, 1, "F1");
		Check(gtc.Calls[0].Order.TimeInForce == TimeInForce.None && gtc.Calls[1].Order.TimeInForce == TimeInForce.GoodTillCancel
			&& gtc.Calls[2].Order.TimeInForce == TimeInForce.GoodTillCancel, "the stop and the take profit are good till cancelled");

		AddTick(ind, bar, 115);
		Check(gtc.Calls.Single(c => c.Name == "modify").NewOrder.TimeInForce == TimeInForce.GoodTillCancel, "and stay so when the stop moves");
		Check(Only(Rows(ind, "live"), "MODE")["note"].Contains(" · live orders good till cancelled · "), "the MODE row says so");
		ind.HarnessDispose();

		// a limit entry good for the day
		var day = new FakeBroker();
		ind = Buy(Live(day, i =>
		{
			i.LiveTimeInForce = FvgReactionLiquiditySweep.OrderLifetime.Day;
			i.Entry = FvgReactionLiquiditySweep.EntryRule.LimitPullback;
		}));
		OpenBar(ind, 103.5m);
		Check(day.Calls[0].Order.Type == OrderTypes.Limit && day.Calls[0].Order.TimeInForce == TimeInForce.Day, "a limit entry good for the day");

		// paper orders never leave the indicator: nothing to set
		var paper = Buy(i => i.LiveTimeInForce = FvgReactionLiquiditySweep.OrderLifetime.GoodTillCancel);
		OpenBar(paper, 103.5m);
		Check(!Only(Rows(paper), "MODE")["note"].Contains("good till"), "paper doesn't mention it");
	}

	private static void LiveOneTradePerAccount()
	{
		// two charts armed for the same account: the second one's signal waits for the first position
		var first = new FakeBroker();
		var a = Buy(Live(first));
		OpenBar(a, 103.5m);
		Check(first.Calls.Count == 1, "the first chart sends its entry");

		var second = new FakeBroker();
		var b = Buy(Live(second));
		OpenBar(b, 103.5m);
		Check(second.Calls.Count == 0, "the second chart sends nothing");
		Check(Only(Rows(b, "live"), "SKIPPED")["note"] == "another chart (NQ) has a live trade on this account: one live trade per account at a time",
			$"skipped: {Describe(Rows(b, "live"))}");

		// paper doesn't touch the account, and another account is its own
		var paper = Buy();
		OpenBar(paper, 103.5m);
		Check(Rows(paper).Any(r => r["event"] == "SIGNAL"), "a paper chart trades all the same");

		var other = new FakeBroker { Portfolio = new Portfolio { AccountID = "SIM-2" } };
		var c = Buy(Live(other, i => i.LiveAccount = "SIM-2"));
		OpenBar(c, 103.5m);
		Check(other.Calls.Count == 1, "a chart on another account trades");

		// the first position ends: the account is free again
		first.Fill(a, first.Calls[0].Order, 103.75m, 1, "F1");
		first.Fill(a, first.Calls[2].Order, 123.5m, 1, "F2");
		first.ConfirmCancel(a, first.Calls[1].Order);
		Check(Rows(a, "live").Any(r => r["event"] == "CLOSED"), $"the first position closed: {Trail(Rows(a, "live"))}");

		var third = new FakeBroker();
		var d = Buy(Live(third));
		OpenBar(d, 103.5m);
		Check(third.Calls.Count == 1, "then another chart may trade the account");

		// removing a chart gives its account back, even with its entry still working
		d.HarnessDispose();
		var fourth = new FakeBroker();
		var e = Buy(Live(fourth));
		OpenBar(e, 103.5m);
		Check(fourth.Calls.Count == 1, "once the chart holding it is removed, the account is free");
	}

	private static Action<FvgReactionLiquiditySweep> Live(FakeBroker broker, Action<FvgReactionLiquiditySweep> more = null)
	{
		return i =>
		{
			i.PaperTrading = false;
			i.LiveAccount = "SIM-1";
			i.DailyLossLimit = 1000;
			i.HarnessTradingManager = broker;
			more?.Invoke(i);
		};
	}

	// A trading connection that records the calls; each check answers them itself
	private sealed class FakeBroker : ITradingManager
	{
		private int _ids;

		public Portfolio Portfolio { get; set; } = new Portfolio { AccountID = "SIM-1" };
		public Security Security { get; set; } = new Security { Code = "NQZ6", Instrument = "NQ", TickSize = Tick, TickCost = 5 };
		public Position Position { get; set; } = new Position();
		public List<(string Name, Order Order, Order NewOrder, bool AskConfirmation, bool SetDefaultQuantity)> Calls { get; } =
			new List<(string Name, Order Order, Order NewOrder, bool AskConfirmation, bool SetDefaultQuantity)>();

		public Task OpenOrderAsync(Order order, bool setDefaultQuantity, bool askConfirmation = true, bool checkOrderStates = true)
		{
			order.Id = $"B{++_ids}";
			order.State = OrderStates.Active;
			order.Unfilled = order.QuantityToFill;
			Calls.Add(("open", order, null, askConfirmation, setDefaultQuantity));
			return Task.CompletedTask;
		}

		public Task ModifyOrderAsync(Order order, Order newOrder, bool askConfirmation = true, bool checkOrderStates = true)
		{
			newOrder.Unfilled = newOrder.QuantityToFill - (order.QuantityToFill - order.Unfilled);
			Calls.Add(("modify", order, newOrder, askConfirmation, false));
			return Task.CompletedTask;
		}

		public Task CancelOrderAsync(Order order, bool askConfirmation = true, bool checkOrderStates = true)
		{
			Calls.Add(("cancel", order, null, askConfirmation, false));
			return Task.CompletedTask;
		}

		// the broker's answers: a fill (reported as a trade, then as the order's new state), a cancel
		public void Fill(FvgReactionLiquiditySweep ind, Order order, decimal price, decimal volume, string id)
		{
			order.Unfilled -= volume;

			if (order.Unfilled <= 0)
				order.State = OrderStates.Done;

			ind.HarnessMyTrade(new MyTrade { Id = id, Order = order, OrderId = order.Id, Price = price, Volume = volume, OrderDirection = order.Direction });
			ind.HarnessOrderChanged(order);
		}

		public void ConfirmCancel(FvgReactionLiquiditySweep ind, Order order)
		{
			order.State = OrderStates.Done;
			order.Canceled = true;
			ind.HarnessOrderChanged(order);
		}
	}

	#endregion

	#region Random markets

	// Two indicators see the same random market, history then tick by tick, one executing (paper)
	// and one not. The chart must come out the same, and the executor's log must match the chart:
	// every order from a signal the chart showed, at its prices, one position at a time, its copy of
	// each trade ending as the chart's did, and each day's result adding up.
	private static void RandomMarkets(int seed, Action<FvgReactionLiquiditySweep> configure, Action<List<Dictionary<string, string>>> expect = null)
	{
		const int history = 150;
		var paths = RandomPaths(1400, seed);

		FvgReactionLiquiditySweep Execute(bool execute)
		{
			var ind = NewIndicator(i =>
			{
				i.TakeProfitTicks = 24;
				i.StopLossTicks = 24;
				i.BreakEvenTriggerTicks = 12;
				i.BreakEvenStopTicks = 6;
				i.DailyProfitTargetTicks = 0;
				configure?.Invoke(i);
				i.ExecuteSignals = execute;
			});

			for (var b = 0; b < history; b++)
			{
				var path = paths[b];
				ind.Candles.Add(new IndicatorCandle
				{
					Open = path[0], High = path.Max(), Low = path.Min(), Close = path[path.Count - 1], Time = Start.AddMinutes(b), LastTime = Start.AddMinutes(b)
				});
			}

			ind.HarnessRecalculate();

			for (var b = 0; b < history; b++)
				ind.HarnessCalculate(b);

			for (var b = history; b < paths.Count; b++)
				StreamBar(ind, paths[b]);

			return ind;
		}

		var plain = Execute(false);
		var executing = Execute(true);

		string Key(ChartTrade t) => $"{t.EntryBar}|{t.IsLong}|{t.Trigger}|{t.EntryPrice}|{t.IsShown}|{t.Outcome}|{t.ExitBar}|{t.ExitPrice}|{t.FillBar}";
		var expected = Trades(plain).Select(Key).ToList();
		var actual = Trades(executing).Select(Key).ToList();
		Check(expected.SequenceEqual(actual), $"the chart's trades are the same with execution on ({expected.Count} vs {actual.Count})");

		foreach (var field in new[] { "_longWins", "_longBreakEvens", "_longLosses", "_shortWins", "_shortBreakEvens", "_shortLosses", "_expired", "_missed", "_filtered" })
			Check(Equals(Get(plain, field), Get(executing, field)), $"panel counter {field}");

		// the last history bar's signal is decided when the first live tick closes it: real time
		var chart = Trades(executing).Where(t => t.EntryBar >= history - 1).ToDictionary(t => (t.EntryBar, t.IsLong));
		var rows = Rows(executing);
		var taken = rows.Where(r => r["event"] == "SIGNAL").ToList();
		var closed = rows.Where(r => r["event"] == "CLOSED").ToList();
		Check(taken.Count >= 8 && closed.Count >= 5, $"the market exercised the executor: {taken.Count} taken, {closed.Count} closed");

		// every real-time signal has one row saying what the executor did with it
		var decided = rows.Where(r => r["event"] == "SIGNAL" || r["event"] == "SKIPPED").ToList();
		Check(decided.Count == chart.Count && decided.All(r => chart.ContainsKey((Int(r["signal_bar"]), r["side"] == "BUY"))),
			$"one SIGNAL or SKIPPED row per real-time signal: {decided.Count} rows, {chart.Count} signals");
		Check(decided.All(r => r["ev_ticks"].Length > 0 && r["ev_worst_ticks"].Length > 0), "each with its EV on the label and in the worst case");

		foreach (var row in taken)
		{
			var trade = chart[(Int(row["signal_bar"]), row["side"] == "BUY")];
			Check(trade.IsShown && Dec(row["entry"]) == trade.EntryPrice && Dec(row["take_profit"]) == trade.TakeProfitPrice
				&& Dec(row["stop_loss"]) == trade.StopLossPrice, $"signal {row["signal"]}: shown, at the chart's prices");
		}

		foreach (var row in rows.Where(r => r["event"] == "CHART" && !r["note"].StartsWith("no longer followed", StringComparison.Ordinal)))
		{
			var trade = chart[(Int(row["signal_bar"]), row["side"] == "BUY")];
			var ticks = row["chart_ticks"].Length == 0 ? (decimal?)null : Dec(row["chart_ticks"]);
			var chartTicks = trade.Outcome == "Missed" ? (decimal?)null : (trade.ExitPrice - trade.EntryPrice) / Tick * (trade.IsLong ? 1 : -1);
			Check(row["chart_outcome"] == Badge(trade.Outcome) && ticks == chartTicks, $"signal {row["signal"]}: the copy ended {row["chart_outcome"]} {ticks}, the chart {trade.Outcome} {chartTicks}");
		}

		// one position at a time: each SIGNAL waits for the last position to close
		var open = false;

		foreach (var row in rows)
		{
			if (row["event"] == "SIGNAL")
			{
				Check(!open, $"signal {row["signal"]} taken while a position was open");
				open = true;
			}
			else if (row["event"] == "CLOSED" || row["event"] == "NOFILL")
				open = false;
		}

		// each trading day adds up, and a day stopped by the limit takes nothing more
		foreach (var day in closed.GroupBy(r => r["trading_day"]))
		{
			var total = day.Sum(r => Dec(r["pnl_usd"]));
			Check(Dec(day.Last()["day_pnl_usd"]) == total, $"day {day.Key}: {day.Last()["day_pnl_usd"]} is the sum {total}");
		}

		// the breaker trips on the day's first close at or below the limit, and at no other time
		foreach (var day in rows.GroupBy(r => r["trading_day"]))
		{
			var dayRows = day.ToList();
			var breakers = dayRows.Where(r => r["event"] == "BREAKER").ToList();
			var trip = executing.DailyLossLimit > 0
				? dayRows.FirstOrDefault(r => r["event"] == "CLOSED" && Dec(r["day_pnl_usd"]) <= -executing.DailyLossLimit)
				: null;

			if (trip == null)
			{
				Check(breakers.Count == 0, $"day {day.Key}: no breaker without reaching the limit");
				continue;
			}

			Check(breakers.Count == 1 && dayRows.IndexOf(breakers[0]) == dayRows.IndexOf(trip) + 1, $"day {day.Key}: the breaker right after the close that reached the limit");
			Check(!dayRows.Skip(dayRows.IndexOf(trip)).Any(r => r["event"] == "SIGNAL"), $"day {day.Key}: nothing taken after the breaker");
		}

		// the profit target ends the day on the first close that makes it, and at no other time
		foreach (var day in rows.GroupBy(r => r["trading_day"]))
		{
			var dayRows = day.ToList();
			var targets = dayRows.Where(r => r["event"] == "TARGET").ToList();
			var made = 0m;
			var reached = (Dictionary<string, string>)null;

			foreach (var row in dayRows.Where(r => r["event"] == "CLOSED"))
			{
				made += Dec(row["pnl_ticks"]);

				if (reached == null && executing.DailyProfitTargetTicks > 0 && made >= executing.DailyProfitTargetTicks)
					reached = row;
			}

			if (reached == null)
			{
				Check(targets.Count == 0, $"day {day.Key}: no target without making it");
				continue;
			}

			Check(targets.Count == 1 && dayRows.IndexOf(targets[0]) == dayRows.IndexOf(reached) + 1, $"day {day.Key}: the target right after the close that made it");
			Check(!dayRows.Skip(dayRows.IndexOf(reached)).Any(r => r["event"] == "SIGNAL"), $"day {day.Key}: nothing taken after the target");
		}

		foreach (var row in closed)
		{
			var ticks = Dec(row["pnl_ticks"]);
			Check(Math.Abs(ticks) < 200, $"signal {row["signal"]}: a result in range ({ticks}t)");
		}

		// the log report adds up to the log, table by table
		var report = ExecutionLogReport.Build(rows);
		Check(report.Total.Count == closed.Count && report.Total.Dollars == closed.Sum(r => Dec(r["pnl_usd"])) && report.Total.Ticks == closed.Sum(r => Dec(r["pnl_ticks"])),
			$"the report sums the log: {report.Total.Count} trades, {report.Total.Dollars}");

		foreach (var table in new[] { report.BySide, report.BySetup, report.ByConfirmations, report.ByLabel, report.ByHour })
			Check(table.Sum(g => g.Tally.Count) == closed.Count && table.Sum(g => g.Tally.Dollars) == report.Total.Dollars, "each table adds up to the total");

		Check(report.Taken == taken.Count && report.Skipped.Sum(r => r.Count) == rows.Count(r => r["event"] == "SKIPPED"), "and counts what was taken and skipped");

		expect?.Invoke(rows);
	}

	// random tick paths: mostly small steps, now and then a burst that leaves gaps behind
	private static List<List<decimal>> RandomPaths(int count, int seed)
	{
		var random = new Random(seed);
		var price = 18000m;
		var paths = new List<List<decimal>>();

		for (var b = 0; b < count; b++)
		{
			var path = new List<decimal> { price };
			var burst = random.NextDouble() < 0.06 ? (random.Next(2) == 0 ? -1 : 1) : 0;
			var steps = random.Next(3, 10);

			for (var s = 0; s < steps; s++)
			{
				price += (random.Next(-4, 5) + burst * random.Next(1, 5)) * Tick;
				path.Add(price);
			}

			paths.Add(path);
		}

		return paths;
	}

	private static string Badge(string outcome)
	{
		switch (outcome)
		{
			case "TakeProfit":
				return "TP";

			case "BreakEven":
				return "BE";

			case "StopLoss":
				return "SL";

			case "Expired":
				return "EXP";

			case "Missed":
				return "NO FILL";

			default:
				return "OPEN";
		}
	}

	#endregion

	#region Markets and the harness

	// the signal tests' market: 15 quiet bars, a bullish gap 100.50 - 102.50 (bars 15 - 17), a drift (18)
	private static List<IndicatorCandle> FvgSetup(DateTime? start = null)
	{
		var bars = Enumerable.Range(0, 15).Select(_ => Bar(100, 100.5m, 99.5m, 100)).ToList();
		bars.Add(Bar(100, 100.5m, 99.5m, 100.25m));
		bars.Add(Bar(100.25m, 104, 100.25m, 103.75m));
		bars.Add(Bar(103.75m, 105, 102.5m, 104.75m));
		bars.Add(Bar(104.75m, 104.75m, 103.5m, 103.75m));
		Space(bars, start ?? Start);
		return bars;
	}

	// bar 19 dips into the gap and closes back above it as a hammer: the next bar's first tick
	// makes it a BUY at 103.50 (TP 123.50, SL 83.50, stop to 108.50 once 113.50 trades)
	private static readonly decimal[] Hammer = { 103, 102.5m, 101.75m, 102.75m, 103.25m, 103.5m };

	private static FvgReactionLiquiditySweep Buy(Action<FvgReactionLiquiditySweep> configure = null, string folder = null)
	{
		var ind = LiveIndicator(FvgSetup(), configure, folder);
		StreamBar(ind, Hammer, delta: 50);
		return ind;
	}

	// quiet bars at `level`, then FvgSetup's gap and hammer moved there, and the first tick of the
	// next bar: a BUY at level + 3.50
	private static void StreamPattern(FvgReactionLiquiditySweep ind, decimal level, DateTime? time = null)
	{
		var first = true;

		void Candle(decimal open, decimal high, decimal low, decimal close)
		{
			StreamBar(ind, close >= open ? new[] { open, low, high, close } : new[] { open, high, low, close }, 0, first ? time : null);
			first = false;
		}

		for (var i = 0; i < 5; i++)
			Candle(level, level + 0.5m, level - 0.5m, level);

		Candle(level, level + 0.5m, level - 0.5m, level + 0.25m);
		Candle(level + 0.25m, level + 4, level + 0.25m, level + 3.75m);
		Candle(level + 3.75m, level + 5, level + 2.5m, level + 4.75m);
		Candle(level + 4.75m, level + 4.75m, level + 3.5m, level + 3.75m);
		StreamBar(ind, Hammer.Select(p => p - 100 + level).ToList(), 50);
		OpenBar(ind, level + 3.5m);
	}

	private static IndicatorCandle Bar(decimal open, decimal high, decimal low, decimal close, decimal delta = 0)
	{
		return new IndicatorCandle { Open = open, High = high, Low = low, Close = close, Delta = delta };
	}

	// one bar a minute from `start`
	private static void Space(List<IndicatorCandle> bars, DateTime start)
	{
		for (var i = 0; i < bars.Count; i++)
			bars[i].Time = bars[i].LastTime = start.AddMinutes(i);
	}

	// Signals at any hour, a fixed big-fill size and no key levels, as most signal tests have them,
	// and execution on in paper mode, logging to a folder of its own
	private static FvgReactionLiquiditySweep NewIndicator(Action<FvgReactionLiquiditySweep> configure = null, string folder = null)
	{
		var ind = new FvgReactionLiquiditySweep
		{
			InstrumentInfo = new InstrumentInfo { TickSize = Tick, Instrument = "NQ" },
			ChartInfo = new FakeChart(),
			SignalHours = FvgReactionLiquiditySweep.SignalHoursRule.AllHours,
			FillSize = FvgReactionLiquiditySweep.FillSizeRule.FixedContracts,
			LevelPriorDay = false,
			LevelOvernight = false,
			LevelOpeningRange = false,
			LevelEqual = false,
			OnlyWithDayTrend = false,
			ExecuteSignals = true,
			ExecutionLogFolder = folder ?? NewFolder()
		};

		configure?.Invoke(ind);
		return ind;
	}

	private static string NewFolder()
	{
		return Path.Combine(LogRoot, Guid.NewGuid().ToString("N"));
	}

	// history loaded, real time from here
	private static FvgReactionLiquiditySweep LiveIndicator(List<IndicatorCandle> history, Action<FvgReactionLiquiditySweep> configure = null, string folder = null)
	{
		var ind = NewIndicator(configure, folder);
		ind.Candles.AddRange(history);
		Recalculate(ind);
		return ind;
	}

	// what ATAS does after a settings change: calculate every bar again
	private static void Recalculate(FvgReactionLiquiditySweep ind)
	{
		ind.HarnessRecalculate();

		for (var i = 0; i < ind.Candles.Count; i++)
			ind.HarnessCalculate(i);
	}

	// a new bar, fed one tick a second, as ATAS does in real time
	private static IndicatorCandle StreamBar(FvgReactionLiquiditySweep ind, IList<decimal> path, decimal delta = 0, DateTime? time = null)
	{
		var candle = OpenBar(ind, path[0], time, delta);

		for (var i = 1; i < path.Count; i++)
			AddTick(ind, candle, path[i]);

		return candle;
	}

	private static IndicatorCandle OpenBar(FvgReactionLiquiditySweep ind, decimal price, DateTime? time = null, decimal delta = 0)
	{
		var at = time ?? ind.Candles[ind.Candles.Count - 1].Time.AddMinutes(1);
		var candle = new IndicatorCandle { Open = price, High = price, Low = price, Close = price, Delta = delta, Time = at, LastTime = at };
		ind.Candles.Add(candle);
		ind.HarnessCalculate(ind.Candles.Count - 1);
		return candle;
	}

	private static void AddTick(FvgReactionLiquiditySweep ind, IndicatorCandle candle, decimal price)
	{
		candle.High = Math.Max(candle.High, price);
		candle.Low = Math.Min(candle.Low, price);
		candle.Close = price;
		candle.LastTime = candle.LastTime.AddSeconds(1);
		ind.HarnessCalculate(ind.Candles.Count - 1);
	}

	private static List<string> Panel(FvgReactionLiquiditySweep ind)
	{
		ind.FirstVisibleBarNumber = Math.Max(0, ind.Candles.Count - 80);
		ind.LastVisibleBarNumber = ind.Candles.Count - 1;
		var context = new RenderContext();
		ind.HarnessRender(context);
		return context.Strings;
	}

	private static decimal DayPnl(FvgReactionLiquiditySweep ind, bool paper)
	{
		var day = IndicatorType.GetField(paper ? "_paperDay" : "_liveDay", Private).GetValue(ind);
		return (decimal)day.GetType().GetField("Pnl").GetValue(day);
	}

	private sealed class ChartTrade
	{
		public int EntryBar;
		public int FillBar;
		public int ExitBar;
		public bool IsLong;
		public bool IsShown;
		public string Outcome;
		public string Trigger;
		public decimal EntryPrice;
		public decimal TakeProfitPrice;
		public decimal StopLossPrice;
		public decimal ExitPrice;
	}

	private static List<ChartTrade> Trades(FvgReactionLiquiditySweep ind)
	{
		return ((IList)IndicatorType.GetField("_trades", Private).GetValue(ind)).Cast<object>()
			.Select(t => new ChartTrade
			{
				EntryBar = (int)Field(t, "EntryBar"),
				FillBar = (int)Field(t, "FillBar"),
				ExitBar = (int)Field(t, "ExitBar"),
				IsLong = (bool)Field(t, "IsLong"),
				IsShown = (bool)Field(t, "IsShown"),
				Outcome = Field(t, "Outcome").ToString(),
				Trigger = Field(t, "Trigger").ToString(),
				EntryPrice = (decimal)Field(t, "EntryPrice"),
				TakeProfitPrice = (decimal)Field(t, "TakeProfitPrice"),
				StopLossPrice = (decimal)Field(t, "StopLossPrice"),
				ExitPrice = (decimal)Field(t, "ExitPrice")
			})
			.ToList();
	}

	private static object Field(object obj, string name)
	{
		return obj.GetType().GetField(name).GetValue(obj);
	}

	private static object Get(FvgReactionLiquiditySweep ind, string field)
	{
		return IndicatorType.GetField(field, Private).GetValue(ind);
	}

	#endregion

	#region The log, read back

	// every row of the indicator's log files for one mode, oldest file first, by column name
	private static List<Dictionary<string, string>> Rows(FvgReactionLiquiditySweep ind, string mode = "paper")
	{
		var rows = new List<Dictionary<string, string>>();

		if (!Directory.Exists(ind.ExecutionLogFolder))
			return rows;

		foreach (var file in Directory.GetFiles(ind.ExecutionLogFolder, $"*_{mode}.csv").OrderBy(f => f, StringComparer.Ordinal))
		{
			var lines = File.ReadAllLines(file, Encoding.UTF8);
			var header = Cells(lines[0]);

			foreach (var line in lines.Skip(1))
			{
				var cells = Cells(line);
				Check(cells.Count == header.Count, $"{Path.GetFileName(file)}: {cells.Count} cells under {header.Count} columns");
				rows.Add(header.Select((name, i) => (name, value: i < cells.Count ? cells[i] : string.Empty)).ToDictionary(c => c.name, c => c.value));
			}
		}

		return rows;
	}

	// a CSV line, quotes and all
	private static List<string> Cells(string line)
	{
		var cells = new List<string>();
		var cell = new StringBuilder();
		var quoted = false;

		for (var i = 0; i < line.Length; i++)
		{
			var c = line[i];

			if (quoted)
			{
				if (c != '"')
					cell.Append(c);
				else if (i + 1 < line.Length && line[i + 1] == '"')
				{
					cell.Append('"');
					i++;
				}
				else
					quoted = false;
			}
			else if (c == '"')
				quoted = true;
			else if (c == ',')
			{
				cells.Add(cell.ToString());
				cell.Clear();
			}
			else
				cell.Append(c);
		}

		cells.Add(cell.ToString());
		return cells;
	}

	// the order events in sequence, e.g. "SIGNAL ORDER:entry FILL:entry ..." (MODE rows left out)
	private static string Trail(List<Dictionary<string, string>> rows)
	{
		return string.Join(" ", rows.Where(r => r["event"] != "MODE")
			.Select(r => r["order"].Length > 0 ? $"{r["event"]}:{r["order"].Replace(' ', '_')}" : r["event"]));
	}

	private static Dictionary<string, string> Only(List<Dictionary<string, string>> rows, string evt, string order = null)
	{
		var found = rows.Where(r => r["event"] == evt && (order == null || r["order"] == order)).ToList();

		if (found.Count == 1)
			return found[0];

		Check(false, $"expected one {evt}{(order != null ? ":" + order : string.Empty)} row, found {found.Count}: {Describe(rows)}");
		return ExecutionColumns().ToDictionary(c => c, _ => string.Empty);
	}

	private static IEnumerable<string> ExecutionColumns()
	{
		return (string[])IndicatorType.GetField("ExecutionLogColumns", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
	}

	private static string Describe(IEnumerable<Dictionary<string, string>> rows)
	{
		return string.Join(" | ", rows.Select(r => $"{r["event"]} {r["order"]} {r["price"]} {r["note"]}".Trim()));
	}

	private static int Int(string text)
	{
		return int.Parse(text, CultureInfo.InvariantCulture);
	}

	private static decimal Dec(string text)
	{
		return decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
	}

	#endregion

	#region Harness

	private sealed class FakeChart : IChart
	{
		private readonly Container _container = new Container();

		public decimal TopPrice { get; set; } = 130;
		public ChartVisualModes ChartVisualMode { get; set; } = ChartVisualModes.Candles;
		public IChartContainer PriceChartContainer => _container;
		public Rectangle Region => _container.Region;
		public MouseLocationInfo MouseLocationInfo { get; } = new MouseLocationInfo();

		public int GetXByBar(int bar, bool isStartOfBar = true)
		{
			return bar * 8 + (isStartOfBar ? 0 : 4);
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

	private static void Run(string name, Action test)
	{
		var before = Failures.Count;

		// each check starts as if ATAS had just started: no chart holds a live account
		var holders = (IDictionary)IndicatorType.GetField("LiveAccountHolders", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

		lock (holders)
			holders.Clear();

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

	#endregion
}

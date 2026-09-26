// The executor's CSV logs summed up: how the trades it took ended, by setup, confirmation, New York
// hour and the odds on their labels, next to what the chart's own trades made - so weeks of paper
// trading say whether the signals pay after costs, including the footprint ones (fills, delta,
// order flow) that the backtest's bar data can't test. The command line is tests/LogReport;
// tests/ExecutionTests checks that the report adds up to the log.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class ExecutionLogReport
{
	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

	// a closed position, as its CLOSED row has it
	public sealed class Trade
	{
		public string Signal;
		public string Day;
		public string Mode;
		public bool IsLong;
		public string Setup;
		public int Confirmations;
		public bool Trend;
		public bool Delta;
		public bool OrderFlow;
		public bool Pattern;
		public int? LabelEv;           // ticks, as the signal's label said
		public int? WorstEv;           // the same in the worst case inside each bar (logs from before it: none)
		public decimal Ticks;          // made per contract, slippage included
		public decimal Dollars;        // after commission, all contracts
		public string ChartOutcome;    // TP, BE, SL, EXP, or OPEN: closed before the chart's trade ended
		public decimal? ChartTicks;    // what the chart's own trade made, once it ended
		public int Hour;               // New York hour of the signal bar
	}

	// the trades of one group, summed
	public sealed class Tally
	{
		public int Count;
		public int Tp;
		public int Be;
		public int Sl;
		public int Other;              // expired, or closed before the chart's trade ended (flat-by time, a halt)
		public decimal Ticks;
		public decimal Squares;
		public decimal Dollars;
		public decimal LabelEv;
		public int LabelCount;
		public decimal WorstEv;
		public int WorstCount;
		public decimal VersusChart;    // the executor's ticks less the chart's, where the chart's trade ended
		public int VersusCount;

		public void Add(Trade t)
		{
			Count++;
			Ticks += t.Ticks;
			Squares += t.Ticks * t.Ticks;
			Dollars += t.Dollars;

			switch (t.ChartOutcome)
			{
				case "TP":
					Tp++;
					break;

				case "BE":
					Be++;
					break;

				case "SL":
					Sl++;
					break;

				default:
					Other++;
					break;
			}

			if (t.LabelEv.HasValue)
			{
				LabelEv += t.LabelEv.Value;
				LabelCount++;
			}

			if (t.WorstEv.HasValue)
			{
				WorstEv += t.WorstEv.Value;
				WorstCount++;
			}

			if (t.ChartTicks.HasValue)
			{
				VersusChart += t.Ticks - t.ChartTicks.Value;
				VersusCount++;
			}
		}

		public decimal TicksPerTrade => Count == 0 ? 0 : Ticks / Count;

		// how many standard errors the average is from zero (0 with fewer than two trades)
		public double StandardErrors
		{
			get
			{
				if (Count < 2)
					return 0;

				var mean = (double)Ticks / Count;
				var variance = ((double)Squares - Count * mean * mean) / (Count - 1);
				return variance <= 0 ? 0 : mean / Math.Sqrt(variance / Count);
			}
		}
	}

	public sealed class Summary
	{
		public readonly List<Trade> Trades = new List<Trade>();
		public readonly Tally Total = new Tally();
		public readonly List<(string Name, Tally Tally)> BySide = new List<(string, Tally)>();
		public readonly List<(string Name, Tally Tally)> BySetup = new List<(string, Tally)>();
		public readonly List<(string Name, Tally Tally)> ByConfirmations = new List<(string, Tally)>();
		public readonly List<(string Name, Tally Tally)> ByConfirmation = new List<(string, Tally)>();
		public readonly List<(string Name, Tally Tally)> ByLabel = new List<(string, Tally)>();
		public readonly List<(string Name, Tally Tally)> ByHour = new List<(string, Tally)>();
		public readonly List<(string Reason, int Count)> Skipped = new List<(string, int)>();
		public int Taken;
		public int NoFills;
		public int Days;
		public string FirstDay;
		public string LastDay;
		public string Modes;
	}

	// every row of the files, by column name - each file by its own header, as the indicator writes them
	public static List<Dictionary<string, string>> Read(IEnumerable<string> files)
	{
		var rows = new List<Dictionary<string, string>>();

		foreach (var file in files)
		{
			var lines = File.ReadAllLines(file, Encoding.UTF8);

			if (lines.Length == 0)
				continue;

			var header = Cells(lines[0].TrimStart('﻿'));

			foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
			{
				var cells = Cells(line);
				var row = new Dictionary<string, string>(StringComparer.Ordinal);

				for (var i = 0; i < header.Count; i++)
					row[header[i]] = i < cells.Count ? cells[i] : string.Empty;

				rows.Add(row);
			}
		}

		return rows;
	}

	public static Summary Build(IEnumerable<Dictionary<string, string>> rows)
	{
		var s = new Summary();
		var all = rows.ToList();

		string Cell(Dictionary<string, string> row, string column) => row.TryGetValue(column, out var value) ? value : string.Empty;

		foreach (var row in all.Where(r => Cell(r, "event") == "CLOSED"))
		{
			var signal = Cell(row, "signal");

			s.Trades.Add(new Trade
			{
				Signal = signal,
				Day = Cell(row, "trading_day"),
				Mode = Cell(row, "mode"),
				IsLong = Cell(row, "side") == "BUY",
				Setup = Cell(row, "setup").Length > 0 ? Cell(row, "setup") : "?",
				Confirmations = int.TryParse(Cell(row, "confirmations"), NumberStyles.Integer, Inv, out var confirmations) ? confirmations : 0,
				Trend = Cell(row, "trend") == "yes",
				Delta = Cell(row, "delta") == "yes",
				OrderFlow = Cell(row, "order_flow") == "yes",
				Pattern = Cell(row, "pattern") == "yes",
				LabelEv = int.TryParse(Cell(row, "ev_ticks"), NumberStyles.Integer, Inv, out var label) ? label : (int?)null,
				WorstEv = int.TryParse(Cell(row, "ev_worst_ticks"), NumberStyles.Integer, Inv, out var worst) ? worst : (int?)null,
				Ticks = Number(Cell(row, "pnl_ticks")) ?? 0,
				Dollars = Number(Cell(row, "pnl_usd")) ?? 0,
				ChartOutcome = Cell(row, "chart_outcome"),
				ChartTicks = Number(Cell(row, "chart_ticks")),
				Hour = signal.Length >= 11 && int.TryParse(signal.Substring(9, 2), NumberStyles.Integer, Inv, out var hour) ? hour : -1
			});
		}

		s.Taken = all.Count(r => Cell(r, "event") == "SIGNAL");
		s.NoFills = all.Count(r => Cell(r, "event") == "NOFILL");

		var days = all.Select(r => Cell(r, "trading_day")).Where(d => d.Length > 0).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
		s.Days = days.Count;
		s.FirstDay = days.FirstOrDefault();
		s.LastDay = days.LastOrDefault();
		s.Modes = string.Join(" and ", all.Select(r => Cell(r, "mode")).Where(m => m.Length > 0).Distinct().OrderBy(m => m, StringComparer.Ordinal));

		foreach (var t in s.Trades)
			s.Total.Add(t);

		List<(string, Tally)> Groups<TKey>(Func<Trade, TKey> key, Func<TKey, string> name, IComparer<TKey> order = null)
		{
			return s.Trades.GroupBy(key)
				.OrderBy(g => g.Key, order ?? Comparer<TKey>.Default)
				.Select(g => (name(g.Key), Sum(g)))
				.ToList();
		}

		s.BySide.AddRange(Groups(t => t.IsLong ? 0 : 1, k => k == 0 ? "Buys" : "Shorts"));
		s.BySetup.AddRange(s.Trades.GroupBy(t => t.Setup).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
			.Select(g => (g.Key, Sum(g))));
		s.ByConfirmations.AddRange(Groups(t => t.Confirmations, k => $"{k} confirmation{(k == 1 ? string.Empty : "s")}"));

		foreach (var (name, has) in new (string, Func<Trade, bool>)[]
		{
			("trend", t => t.Trend), ("delta", t => t.Delta), ("order flow", t => t.OrderFlow), ("pattern", t => t.Pattern)
		})
		{
			var with = s.Trades.Where(has).ToList();
			var without = s.Trades.Where(t => !has(t)).ToList();

			if (with.Count > 0)
				s.ByConfirmation.Add(($"{name}: yes", Sum(with)));

			if (without.Count > 0)
				s.ByConfirmation.Add(($"{name}: no", Sum(without)));
		}

		s.ByLabel.AddRange(Groups(t => !t.LabelEv.HasValue ? 9 : t.LabelEv < 0 ? 0 : t.LabelEv < 5 ? 1 : t.LabelEv < 10 ? 2 : 3,
			k => new[] { "below 0", "0 to 5", "5 to 10", "10 and more" }.ElementAtOrDefault(k) ?? "not logged"));
		s.ByHour.AddRange(Groups(t => t.Hour, k => k < 0 ? "?" : $"{k:00}:00"));

		// skip reasons without their details (in brackets) and amounts, so alike ones count together
		s.Skipped.AddRange(all.Where(r => Cell(r, "event") == "SKIPPED")
			.GroupBy(r => Regex.Replace(Regex.Replace(Cell(r, "note"), @"\s*\([^)]*\)", string.Empty), @"-?\$[\d,]+(\.\d+)?", "$#").Trim())
			.OrderByDescending(g => g.Count())
			.ThenBy(g => g.Key, StringComparer.Ordinal)
			.Select(g => (g.Key, g.Count())));

		return s;
	}

	private static Tally Sum(IEnumerable<Trade> trades)
	{
		var tally = new Tally();

		foreach (var t in trades)
			tally.Add(t);

		return tally;
	}

	public static string Format(Summary s, int files = 0)
	{
		var text = new StringBuilder();

		void Line(string line = "")
		{
			text.Append(line).Append('\n');
		}

		string Signed(decimal value, string format = "0.0") => value.ToString("+" + format + ";-" + format + ";0", Inv);
		string Money(decimal value) => value.ToString("+$#,##0.00;-$#,##0.00;$0.00", Inv);
		string Average(decimal sum, int count) => count == 0 ? "-" : Signed(sum / count);

		Line($"Execution log: {(s.Modes.Length > 0 ? s.Modes : "no rows")}"
			+ (s.Days > 0 ? $", {s.FirstDay} to {s.LastDay}, {s.Days} trading day{(s.Days == 1 ? string.Empty : "s")}" : string.Empty)
			+ (files > 0 ? $", {files} file{(files == 1 ? string.Empty : "s")}" : string.Empty));
		Line($"{s.Total.Count} closed trade{(s.Total.Count == 1 ? string.Empty : "s")}, {s.NoFills} entr{(s.NoFills == 1 ? "y" : "ies")} not filled, "
			+ $"{s.Skipped.Sum(r => r.Count)} signal{(s.Skipped.Sum(r => r.Count) == 1 ? string.Empty : "s")} skipped");
		Line();

		void Table(string title, IEnumerable<(string Name, Tally Tally)> groups)
		{
			var rows = groups.Where(g => g.Tally.Count > 0).ToList();

			if (rows.Count == 0)
				return;

			var width = Math.Max(title.Length, rows.Max(r => r.Name.Length));
			Line($"{title.PadRight(width)}  {"trades",6}  {"TP",4}  {"BE",4}  {"SL",4}  {"other",5}  {"ticks/tr",8}  {"$ net",12}  {"label EV",8}  {"worst EV",8}  {"vs chart",8}");

			foreach (var (name, t) in rows)
			{
				Line($"{name.PadRight(width)}  {t.Count,6}  {t.Tp,4}  {t.Be,4}  {t.Sl,4}  {t.Other,5}  {Signed(t.TicksPerTrade),8}  {Money(t.Dollars),12}  "
					+ $"{Average(t.LabelEv, t.LabelCount),8}  {Average(t.WorstEv, t.WorstCount),8}  {Average(t.VersusChart, t.VersusCount),8}");
			}

			Line();
		}

		Table("Closed trades", new[] { ("All", s.Total) }.Concat(s.BySide));
		Table("By setup", s.BySetup);
		Table("By number of confirmations", s.ByConfirmations);
		Table("By confirmation", s.ByConfirmation);
		Table("By expected ticks on the label", s.ByLabel);
		Table("By New York hour of the signal", s.ByHour);

		if (s.Total.Count > 0)
		{
			var t = s.Total;
			var errors = Math.Round(t.StandardErrors, 1);

			if (errors == 0)
				errors = 0;   // not "-0.0"

			Line($"The trades made {Signed(t.TicksPerTrade)} ticks each ({Money(t.Dollars)} after commission), "
				+ $"{errors.ToString("0.0", Inv)} standard errors from zero{(Math.Abs(errors) < 2 ? ": not yet told apart from chance" : string.Empty)}.");

			if (t.LabelCount > 0)
				Line($"Their labels said {Average(t.LabelEv, t.LabelCount)} ticks each"
					+ (t.WorstCount > 0 ? $", the worst case inside bars {Average(t.WorstEv, t.WorstCount)}" : string.Empty) + ".");

			if (t.VersusCount > 0)
				Line($"Against the chart's own trades: {Average(t.VersusChart, t.VersusCount)} ticks a trade (slippage, and exits at market).");

			Line("\"other\": expired, or closed before the chart's trade ended (a flat-by time, a halt).");
			Line();
		}

		if (s.Skipped.Count > 0)
		{
			Line("Signals skipped");

			foreach (var (reason, count) in s.Skipped)
				Line($"{count,6}  {reason}");

			Line();
		}

		return text.ToString();
	}

	private static decimal? Number(string text)
	{
		return decimal.TryParse(text, NumberStyles.Number, Inv, out var value) ? value : (decimal?)null;
	}

	// a CSV line's cells, quotes and all
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
}

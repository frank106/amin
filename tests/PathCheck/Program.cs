// How far off is a backtest on 1-minute bars? Historical bars only have an open, high, low and
// close, so the indicator has to assume how price moved inside each one. This builds a market of
// pure noise tick by tick, then settles the same signals twice: once on the path inside the bars
// (as a live chart does) and once on the finished bars with each high / low order rule. The
// difference is what the bars get wrong for those settings.
//
//   dotnet run -c Release --project tests/PathCheck -- [options]
//
// Options:
//   --days <n>            trading days of noise (default 250)
//   --seed <n>            random seed (default 1)
//   --volatility <x>      scale of the price moves (default 1.4: about NQ's 1-minute ranges in
//                         2024 - 2026, some 50 ticks a bar in regular hours)
//   --steps <n>           price steps inside each minute (default 60)
//   --set <Name>=<Value>  any indicator setting, e.g. --set TakeProfitTicks=40 (repeatable)
//
// The noise has no drift, so no signal has an edge; what matters is how far each rule lands from
// the path.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using ATAS.Indicators;
using ATAS.Indicators.Technical;

internal static class Program
{
	private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
	private const decimal Tick = 0.25m;
	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
	private static readonly TimeZoneInfo NewYork = FindZone("America/New_York");

	private sealed class Options
	{
		public int Days = 250;
		public int Seed = 1;
		public double Volatility = 1.4;
		public int Steps = 60;
		public List<(string Name, string Value)> Settings = new List<(string, string)>();
	}

	private sealed class Settled
	{
		public int EntryBar;
		public bool IsLong, IsShown;
		public string Outcome;
		public decimal Ticks;
		public bool Closed => Outcome == "TakeProfit" || Outcome == "BreakEven" || Outcome == "StopLoss";
	}

	public static int Main(string[] args)
	{
		Options o;

		try
		{
			o = ParseArgs(args);
			New(o, FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst);
		}
		catch (Exception e) when (e is ArgumentException || e is FormatException || e is InvalidCastException)
		{
			Console.Error.WriteLine(e.Message);
			Console.Error.WriteLine("usage: dotnet run -c Release --project tests/PathCheck -- [--days 250] [--seed 1] [--volatility 1.4] [--steps 60] [--set Name=Value] ...");
			return 2;
		}

		var (bars, paths) = Generate(o);
		var sample = New(o, FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst);
		var bracket = (string)typeof(FvgReactionLiquiditySweep).GetMethod("BracketText", Private).Invoke(sample, null);
		Console.WriteLine($"Noise on the CME schedule: {o.Days} trading days, {bars.Count.ToString("N0", Inv)} bars of {o.Steps} steps, volatility x{o.Volatility.ToString("0.##", Inv)}; "
			+ $"median bar {MedianRange(bars, true):0} ticks in regular hours, {MedianRange(bars, false):0} outside them");
		Console.WriteLine($"Signal hours {sample.SignalHours}, entry {sample.Entry}, bracket {bracket}");
		Console.WriteLine();
		Console.WriteLine($"{"Settled on",-40}  {"signals",7}  {"TP",6}  {"BE",6}  {"SL",6}  {"ticks a trade",13}  {"end differently",15}  {"vs the path",11}");

		var truth = Stream(o, bars, paths);
		Line("the path inside the bars (the truth)", truth, null);

		Line("bars, open to the nearer extreme first", History(o, bars, FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst), truth);
		Line("bars, candle direction", History(o, bars, FvgReactionLiquiditySweep.SameBarHitRule.CandleDirection), truth);
		Line("bars, worst case for the trade", History(o, bars, FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst), truth);

		Console.WriteLine();
		Console.WriteLine("Every signal that ended at TP / BE / SL, hidden ones included. The last two columns compare each rule with the path, signal by signal,");
		Console.WriteLine("over the signals both found: how many ended another way, and the average ticks a trade the rule adds. Noise has no edge, so the");
		Console.WriteLine("truth's own ticks a trade are chance (and the stops filled exactly at their price, even when a step jumps past them).");
		return 0;
	}

	private static Options ParseArgs(string[] args)
	{
		var o = new Options();

		string Next(ref int i)
		{
			if (i + 1 >= args.Length)
				throw new ArgumentException($"{args[i]} needs a value");

			return args[++i];
		}

		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--days":
					o.Days = Math.Max(1, int.Parse(Next(ref i), Inv));
					break;

				case "--seed":
					o.Seed = int.Parse(Next(ref i), Inv);
					break;

				case "--volatility":
					o.Volatility = double.Parse(Next(ref i), Inv);
					break;

				case "--steps":
					o.Steps = Math.Max(1, int.Parse(Next(ref i), Inv));
					break;

				case "--set":
					var pair = Next(ref i);
					var equals = pair.IndexOf('=');

					if (equals <= 0)
						throw new ArgumentException($"--set takes Name=Value, not {pair}");

					o.Settings.Add((pair.Substring(0, equals), pair.Substring(equals + 1)));
					break;

				default:
					throw new ArgumentException($"unknown option {args[i]}");
			}
		}

		return o;
	}

	// an indicator with the settings, and the given high / low order for finished bars
	private static FvgReactionLiquiditySweep New(Options o, FvgReactionLiquiditySweep.SameBarHitRule rule)
	{
		var ind = new FvgReactionLiquiditySweep { InstrumentInfo = new InstrumentInfo { TickSize = Tick } };

		foreach (var (name, value) in o.Settings)
		{
			var property = typeof(FvgReactionLiquiditySweep).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
				?? throw new ArgumentException($"no setting called {name}");
			var type = property.PropertyType;
			object parsed = type.IsEnum ? Enum.Parse(type, value, true)
				: type == typeof(TimeSpan) ? TimeSpan.Parse(value, Inv)
				: type == typeof(bool) ? bool.Parse(value)
				: Convert.ChangeType(value, type, Inv);
			property.SetValue(ind, parsed);
		}

		ind.SameBarRule = rule;
		return ind;
	}

	// Driftless noise on the CME schedule (Sunday 18:00 to Friday 17:00 New York, a daily halt at
	// 17:00), busiest in the first hour of regular hours, then the rest of them, then pre-market.
	private static (List<IndicatorCandle> Bars, List<decimal[]> Paths) Generate(Options o)
	{
		var random = new Random(o.Seed);
		var bars = new List<IndicatorCandle>();
		var paths = new List<decimal[]>();
		var time = new DateTime(2025, 1, 5, 23, 0, 0, DateTimeKind.Utc);    // a Sunday, 18:00 New York
		long price = 80000;                                                  // in ticks: 20,000.00
		var days = 0;
		var lastDay = DateTime.MinValue;

		double Gauss()
		{
			var u1 = 1.0 - random.NextDouble();
			var u2 = random.NextDouble();
			return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
		}

		while (true)
		{
			var ny = TimeZoneInfo.ConvertTimeFromUtc(time, NewYork);
			var minute = ny.Hour * 60 + ny.Minute;
			var weekday = ny.DayOfWeek;
			var closed = weekday == DayOfWeek.Saturday
				|| (weekday == DayOfWeek.Sunday && minute < 18 * 60)
				|| (weekday == DayOfWeek.Friday && minute >= 17 * 60)
				|| (minute >= 17 * 60 && minute < 18 * 60);

			if (!closed)
			{
				var day = ny.TimeOfDay >= new TimeSpan(18, 0, 0) ? ny.Date.AddDays(1) : ny.Date;

				if (day != lastDay)
				{
					if (++days > o.Days)
						break;

					lastDay = day;
				}

				// standard deviation of a minute's move in ticks, before the volatility scale
				var sigma = minute >= 570 && minute < 630 ? 40 : minute >= 630 && minute < 960 ? 24 : minute >= 510 && minute < 570 ? 16 : 8;
				var step = sigma * o.Volatility / Math.Sqrt(o.Steps);
				var path = new decimal[o.Steps + 1];
				path[0] = price * Tick;

				for (var i = 1; i <= o.Steps; i++)
				{
					price += (long)Math.Round(Gauss() * step);
					path[i] = price * Tick;
				}

				bars.Add(new IndicatorCandle { Open = path[0], High = path.Max(), Low = path.Min(), Close = path[o.Steps], Time = time });
				paths.Add(path);
			}

			time = time.AddMinutes(1);
		}

		return (bars, paths);
	}

	private static double MedianRange(List<IndicatorCandle> bars, bool regularHours)
	{
		var ranges = bars
			.Where(b =>
			{
				var t = TimeZoneInfo.ConvertTimeFromUtc(b.Time, NewYork).TimeOfDay;
				return (t >= new TimeSpan(9, 30, 0) && t < new TimeSpan(16, 0, 0)) == regularHours;
			})
			.Select(b => (double)((b.High - b.Low) / Tick))
			.OrderBy(x => x)
			.ToList();

		return ranges.Count == 0 ? 0 : ranges[ranges.Count / 2];
	}

	// finished bars, as a chart loading history sees them
	private static List<Settled> History(Options o, List<IndicatorCandle> bars, FvgReactionLiquiditySweep.SameBarHitRule rule)
	{
		var ind = New(o, rule);
		ind.Candles.AddRange(bars.Select(b => new IndicatorCandle { Open = b.Open, High = b.High, Low = b.Low, Close = b.Close, Time = b.Time }));
		ind.HarnessRecalculate();

		for (var i = 0; i < bars.Count; i++)
			ind.HarnessCalculate(i);

		return Trades(ind);
	}

	// every bar built step by step from its path, as ATAS feeds a live chart
	private static List<Settled> Stream(Options o, List<IndicatorCandle> bars, List<decimal[]> paths)
	{
		var ind = New(o, FvgReactionLiquiditySweep.SameBarHitRule.NearestExtremeFirst);
		ind.HarnessRecalculate();

		for (var b = 0; b < bars.Count; b++)
		{
			var path = paths[b];
			var candle = new IndicatorCandle { Open = path[0], High = path[0], Low = path[0], Close = path[0], Time = bars[b].Time };
			ind.Candles.Add(candle);

			foreach (var price in path)
			{
				candle.High = Math.Max(candle.High, price);
				candle.Low = Math.Min(candle.Low, price);
				candle.Close = price;
				ind.HarnessCalculate(b);
			}
		}

		return Trades(ind);
	}

	private static List<Settled> Trades(FvgReactionLiquiditySweep ind)
	{
		object Get(object obj, string field) => obj.GetType().GetField(field).GetValue(obj);
		var list = (IList)typeof(FvgReactionLiquiditySweep).GetField("_trades", Private).GetValue(ind);

		return list.Cast<object>().Select(t =>
		{
			var isLong = (bool)Get(t, "IsLong");
			return new Settled
			{
				EntryBar = (int)Get(t, "EntryBar"),
				IsLong = isLong,
				IsShown = (bool)Get(t, "IsShown"),
				Outcome = Get(t, "Outcome").ToString(),
				Ticks = ((decimal)Get(t, "ExitPrice") - (decimal)Get(t, "EntryPrice")) / Tick * (isLong ? 1 : -1)
			};
		}).ToList();
	}

	private static void Line(string name, List<Settled> trades, List<Settled> truth)
	{
		var closed = trades.Where(t => t.Closed).ToList();
		string Share(string outcome) => closed.Count == 0 ? "-" : (100.0 * closed.Count(t => t.Outcome == outcome) / closed.Count).ToString("0.0", Inv) + "%";
		var average = closed.Count == 0 ? 0 : closed.Average(t => t.Ticks);
		var line = $"{name,-40}  {closed.Count,7}  {Share("TakeProfit"),6}  {Share("BreakEven"),6}  {Share("StopLoss"),6}  {average.ToString("+0.00;-0.00;0.00", Inv),13}";

		if (truth != null)
		{
			// the same signal settled both ways; a signal only one side found is left out
			var byKey = truth.Where(t => t.Closed).ToDictionary(t => (t.EntryBar, t.IsLong));
			var pairs = closed.Where(t => byKey.ContainsKey((t.EntryBar, t.IsLong))).Select(t => (Bars: t, Path: byKey[(t.EntryBar, t.IsLong)])).ToList();
			var differ = pairs.Count(p => p.Bars.Outcome != p.Path.Outcome);
			var delta = pairs.Count == 0 ? 0 : pairs.Average(p => p.Bars.Ticks - p.Path.Ticks);
			line += $"  {(pairs.Count == 0 ? "-" : (100.0 * differ / pairs.Count).ToString("0.0", Inv) + "%"),15}  {delta.ToString("+0.00;-0.00;0.00", Inv),11}";
		}

		Console.WriteLine(line);
	}

	private static TimeZoneInfo FindZone(string id)
	{
		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(id);
		}
		catch (TimeZoneNotFoundException)
		{
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
	}
}

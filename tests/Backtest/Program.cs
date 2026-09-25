// Replays bars from a CSV through FvgReactionLiquiditySweep, built against the ATAS API stubs
// like the tests, and reports how its signals ended: the same walk-forward results the chart's
// panel shows, over any stretch of history.
//
//   dotnet run -c Release --project tests/Backtest -- <bars.csv> [more files] [options]
//
// Options:
//   --tz <zone>             time zone of times without an offset (default UTC), e.g. America/New_York
//   --bar-time open|close   whether a time stamps the bar's open (default) or its close
//   --from, --to <date>     only bars from / before this date (UTC)
//   --tick <size>           tick size (default 0.25, NQ); prices are rounded to it
//   --tick-value <dollars>  per tick and contract (default 5, NQ; MNQ is 0.5)
//   --commission <ticks>    commission per round trip (default 1: about $5 on NQ)
//   --slippage <ticks>      slippage per market order: an entry at the close and every exit (default 1);
//                           a limit entry has none, so it costs commission + 1 slippage, a market one + 2
//   --split <date>          also report the signals before and from this date apart (year 1 / year 2)
//   --summary               print one line of JSON (all / before / from the split, and the same with the
//                           worst-case high / low order under "worst", and on the bars alone under
//                           "assumed" when there are ticks) instead of the report
//   --price-scale <x>       multiply every price, for files with scaled integer prices
//   --symbol <name>         keep only the rows of this symbol, when the file has a symbol column;
//                           otherwise each day keeps its most traded symbol
//   --preset defaults|1min  settings to start from (default: the indicator's defaults)
//   --set <Name>=<Value>    any indicator setting, e.g. --set SignalHours=AllHours (repeatable)
//   --trades <file.csv>     write every signal (shown and hidden) to a CSV
//   --ticks <file>          ticks (time, price) inside the bars: where a bar has them, trades are settled
//                           on them, as a live chart does, instead of on an assumed path (repeatable)
//
// Files can be .csv, .csv.gz or .zip. Columns are found by their header (time / date + time,
// open, high, low, close, volume, symbol) or, without a header, taken as time, open, high, low,
// close, volume (a date and a time may be two columns). Times can be text or Unix seconds,
// milliseconds or nanoseconds. Bars carry no footprint, so big fills (and the Fill signals and
// order-flow confirmation they give) and the delta confirmation need data this runner does not
// read; everything built from candles works as on the chart. With --ticks, bars that have ticks are
// replayed tick by tick, so trades follow the real path inside them, as on a live chart.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

using ATAS.Indicators;
using ATAS.Indicators.Technical;

internal static class Program
{
	private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
	private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
	private static readonly Type IndicatorType = typeof(FvgReactionLiquiditySweep);
	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
	private static readonly TimeZoneInfo NewYork = FindZone("America/New_York");

	private sealed class Options
	{
		public List<string> Files = new List<string>();
		public List<string> TickFiles = new List<string>();
		public TimeZoneInfo Zone = TimeZoneInfo.Utc;
		public bool CloseTimes;
		public DateTime From = DateTime.MinValue;
		public DateTime To = DateTime.MaxValue;
		public decimal Tick = 0.25m;
		public decimal TickValue = 5m;
		public decimal Commission = 1m;
		public decimal Slippage = 1m;
		public DateTime? Split;
		public bool Summary;
		public decimal PriceScale = 1m;
		public string Symbol;
		public string Preset = "defaults";
		public List<(string Name, string Value)> Settings = new List<(string, string)>();
		public string TradesFile;
	}

	private sealed class Row
	{
		public DateTime Time;
		public decimal Open, High, Low, Close, Volume;
		public string Symbol;
	}

	private sealed class Trade
	{
		public int EntryBar, ExitBar;
		public DateTime SignalTime;     // the signal bar's open, UTC
		public bool IsLong, IsShown, Ambiguous;
		public string Setup, SetupName, Patterns, Outcome, Level;
		public decimal Entry, TakeProfit, StopLoss, Exit, Ticks;
		public int LabelTp;
		public double TpOdds, BeOdds, SlOdds, ExpectedTicks;
		public bool LimitEntry;
		public int FillBar;
		public int StepTicks;           // from the break-even trigger back to the moved stop (0 = no break-even)
		public decimal Net;             // ticks after commission and slippage
		public bool Closed => Outcome == "TakeProfit" || Outcome == "BreakEven" || Outcome == "StopLoss";
		public bool Ended => Closed || Outcome == "Expired";
	}

	// trades that ended: at TP, break-even, SL, or expired (closed at market); gross ticks and
	// the net after costs
	private sealed class Tally
	{
		public int Tp, Be, Sl, Ex;
		public decimal Ticks, Net, Won, Lost;
		public double NetSquares;
		public int Count => Tp + Be + Sl + Ex;
		public decimal NetPerTrade => Count > 0 ? Net / Count : 0;

		// how many standard errors the average net is above 0
		public double TStat => Count < 2 ? 0 : (double)NetPerTrade / Math.Sqrt(Math.Max(1e-12, (NetSquares - (double)(Net * Net) / Count) / (Count - 1)) / Count);

		public void Add(Trade t)
		{
			if (t.Outcome == "TakeProfit")
				Tp++;
			else if (t.Outcome == "BreakEven")
				Be++;
			else if (t.Outcome == "StopLoss")
				Sl++;
			else
				Ex++;

			Ticks += t.Ticks;
			Net += t.Net;
			NetSquares += (double)(t.Net * t.Net);

			if (t.Net > 0)
				Won += t.Net;
			else
				Lost -= t.Net;
		}
	}

	public static int Main(string[] args)
	{
		var started = DateTime.UtcNow;
		Options options;
		List<IndicatorCandle> candles;
		List<string> notes;
		List<string> changed;
		var ind = new FvgReactionLiquiditySweep();
		int[][] paths = null;

		try
		{
			options = ParseArgs(args);
			var rows = new List<Row>();

			foreach (var file in options.Files)
				rows.AddRange(ReadRows(file, options));

			candles = BuildCandles(rows, options, out notes);
			ind.InstrumentInfo = new InstrumentInfo { TickSize = options.Tick };
			changed = ApplySettings(ind, options);

			if (options.TickFiles.Count > 0)
			{
				var ticks = new List<(long Time, int Price)>();

				foreach (var file in options.TickFiles)
					ticks.AddRange(ReadTicks(file, options));

				paths = AttachTicks(candles, ticks, options, notes);
			}
		}
		catch (Exception e) when (e is ArgumentException || e is FormatException || e is IOException || e is InvalidDataException)
		{
			Console.Error.WriteLine(e.Message);
			Console.Error.WriteLine("usage: dotnet run -c Release --project tests/Backtest -- <bars.csv> [--tz America/New_York] [--preset 1min] [--set Name=Value] ...");
			return 2;
		}

		if (candles.Count < 100)
		{
			Console.Error.WriteLine($"only {candles.Count} bars in range; nothing to test");
			return 1;
		}

		FvgReactionLiquiditySweep Fresh()
		{
			var fresh = new FvgReactionLiquiditySweep { InstrumentInfo = new InstrumentInfo { TickSize = options.Tick } };
			ApplySettings(fresh, options);
			return fresh;
		}

		// with ticks, the trades settled on them are the result; the bars with the assumed order
		// inside them are what a chart loading this history would show
		List<Trade> assumed = null;
		List<Trade> trades;

		if (paths != null)
		{
			var onBars = Fresh();
			Replay(onBars, candles);
			assumed = ReadTrades(onBars, candles, o: options);
			ReplayTicks(ind, candles, paths);
		}
		else
			Replay(ind, candles);

		trades = ReadTrades(ind, candles, o: options);

		// the same history again, with the high / low order inside a bar that is worst for every
		// trade: on OHLC bars the truth usually lies between the two
		List<Trade> worst = null;

		if (ind.SameBarRule != FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst || paths != null)
		{
			var pessimist = Fresh();
			pessimist.SameBarRule = FvgReactionLiquiditySweep.SameBarHitRule.StopLossFirst;
			Replay(pessimist, candles);
			worst = ReadTrades(pessimist, candles, o: options);
		}

		var report = Report(ind, candles, trades, assumed, worst, paths, options, changed, notes, DateTime.UtcNow - started);
		Console.Write(report);

		if (options.TradesFile != null)
			WriteTrades(options.TradesFile, trades, candles);

		return 0;
	}

	// feeds every bar to the indicator, as a chart loading this history would
	private static void Replay(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles)
	{
		ind.Candles.AddRange(candles);

		// the first bar of each CME trading day (18:00 New York) starts a session
		for (var i = 1; i < candles.Count; i++)
		{
			if (TradingDay(candles[i].Time) != TradingDay(candles[i - 1].Time))
				ind.SessionStarts.Add(i);
		}

		ind.HarnessRecalculate();

		for (var i = 0; i < candles.Count; i++)
			ind.HarnessCalculate(i);
	}

	// the same bars, but each one that has ticks is built tick by tick, as ATAS feeds a live
	// chart, so open trades follow the real path inside it
	private static void ReplayTicks(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles, int[][] paths)
	{
		var tick = ind.InstrumentInfo.TickSize;

		for (var i = 1; i < candles.Count; i++)
		{
			if (TradingDay(candles[i].Time) != TradingDay(candles[i - 1].Time))
				ind.SessionStarts.Add(i);
		}

		ind.HarnessRecalculate();

		for (var i = 0; i < candles.Count; i++)
		{
			var bar = candles[i];
			var path = paths[i];

			if (path == null)
			{
				ind.Candles.Add(bar);
				ind.HarnessCalculate(i);
				continue;
			}

			var first = path[0] * tick;
			var forming = new IndicatorCandle { Open = first, High = first, Low = first, Close = first, Volume = bar.Volume, Time = bar.Time };
			ind.Candles.Add(forming);

			foreach (var step in path)
			{
				var price = step * tick;
				forming.High = Math.Max(forming.High, price);
				forming.Low = Math.Min(forming.Low, price);
				forming.Close = price;
				ind.HarnessCalculate(i);
			}
		}
	}

	#region Input

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
				case "--tz":
					o.Zone = FindZone(Next(ref i));
					break;

				case "--bar-time":
					var which = Next(ref i);
					o.CloseTimes = which == "close" ? true : which == "open" ? false : throw new ArgumentException("--bar-time takes open or close");
					break;

				case "--from":
					o.From = DateTime.Parse(Next(ref i), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
					break;

				case "--to":
					o.To = DateTime.Parse(Next(ref i), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
					break;

				case "--tick":
					o.Tick = decimal.Parse(Next(ref i), Inv);
					break;

				case "--tick-value":
					o.TickValue = decimal.Parse(Next(ref i), Inv);
					break;

				case "--commission":
					o.Commission = decimal.Parse(Next(ref i), Inv);
					break;

				case "--slippage":
					o.Slippage = decimal.Parse(Next(ref i), Inv);
					break;

				case "--split":
					o.Split = DateTime.Parse(Next(ref i), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
					break;

				case "--summary":
					o.Summary = true;
					break;

				case "--price-scale":
					o.PriceScale = decimal.Parse(Next(ref i), NumberStyles.Float, Inv);
					break;

				case "--symbol":
					o.Symbol = Next(ref i);
					break;

				case "--preset":
					o.Preset = Next(ref i);

					if (o.Preset != "defaults" && o.Preset != "1min")
						throw new ArgumentException("--preset takes defaults or 1min");

					break;

				case "--set":
					var pair = Next(ref i);
					var eq = pair.IndexOf('=');

					if (eq <= 0)
						throw new ArgumentException($"--set takes Name=Value, not {pair}");

					o.Settings.Add((pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim()));
					break;

				case "--trades":
					o.TradesFile = Next(ref i);
					break;

				case "--ticks":
					o.TickFiles.Add(Next(ref i));
					break;

				default:
					if (args[i].StartsWith("--", StringComparison.Ordinal))
						throw new ArgumentException($"unknown option {args[i]}");

					o.Files.Add(args[i]);
					break;
			}
		}

		if (o.Files.Count == 0)
			throw new ArgumentException("no bar file given");

		return o;
	}

	private static TimeZoneInfo FindZone(string id)
	{
		if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || id.Equals("GMT", StringComparison.OrdinalIgnoreCase))
			return TimeZoneInfo.Utc;

		try
		{
			return TimeZoneInfo.FindSystemTimeZoneById(id);
		}
		catch (TimeZoneNotFoundException) when (id == "America/New_York")
		{
			return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
		}
		catch (TimeZoneNotFoundException)
		{
			throw new ArgumentException($"unknown time zone {id}");
		}
	}

	private static IEnumerable<string> ReadLines(string path)
	{
		if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			using var zip = ZipFile.OpenRead(path);

			foreach (var entry in zip.Entries.Where(e => e.Length > 0).OrderBy(e => e.FullName, StringComparer.Ordinal))
			{
				using var reader = new StreamReader(entry.Open());
				string line;

				while ((line = reader.ReadLine()) != null)
					yield return line;
			}

			yield break;
		}

		using var stream = File.OpenRead(path);
		using var text = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
			? new StreamReader(new GZipStream(stream, CompressionMode.Decompress))
			: new StreamReader(stream);
		string next;

		while ((next = text.ReadLine()) != null)
			yield return next;
	}

	private static List<Row> ReadRows(string path, Options o)
	{
		var rows = new List<Row>();
		char separator = ',';
		int time = -1, date = -1, clock = -1, open = -1, high = -1, low = -1, close = -1, volume = -1, symbol = -1;
		var first = true;
		var lineNumber = 0;
		var skipped = 0;

		foreach (var raw in ReadLines(path))
		{
			lineNumber++;
			var line = raw.Trim();

			if (line.Length == 0)
				continue;

			if (first)
			{
				first = false;
				separator = new[] { ',', ';', '\t', '|' }.OrderByDescending(c => line.Count(ch => ch == c)).First();
				var names = Split(line, separator).Select(n => n.Trim('<', '>', ' ').ToLowerInvariant()).ToArray();

				if (names.Any(n => n.Length > 0 && char.IsLetter(n[0]) && !n.StartsWith("nan", StringComparison.Ordinal)))
				{
					int Find(params string[] candidates) => Array.FindIndex(names, n => candidates.Contains(n));

					time = Find("timestamp", "ts_event", "datetime", "date_time", "date time", "time (utc)", "gmt time", "local time", "utc", "ts", "bar time", "time");
					date = Find("date", "day");
					clock = Find("time", "hour");
					open = Find("open", "o", "open price", "opening price", "first");
					high = Find("high", "h", "high price", "max");
					low = Find("low", "l", "low price", "min");
					close = Find("close", "c", "close price", "last", "closing price", "settle");
					volume = Find("volume", "vol", "v", "tickvol", "tick volume", "total volume", "tickvolume");
					symbol = Find("symbol", "ticker", "instrument", "raw_symbol", "contract");

					// "date" and "time" as two columns, or one column with both
					if (date >= 0 && clock >= 0 && clock != date && (time < 0 || time == clock))
						time = -1;
					else
					{
						if (time < 0)
							time = date;

						date = -1;
						clock = -1;
					}

					if ((time < 0 && date < 0) || open < 0 || high < 0 || low < 0 || close < 0)
						throw new InvalidDataException($"{path}: can't find the time / open / high / low / close columns in \"{line}\"");

					continue;
				}

				// no header: time (or date and time), open, high, low, close, volume
				var cells = Split(line, separator);
				var twoTimeColumns = cells.Length >= 6 && cells[1].Contains(':');
				var at = twoTimeColumns ? 2 : 1;
				(date, clock, time) = twoTimeColumns ? (0, 1, -1) : (-1, -1, 0);
				(open, high, low, close) = (at, at + 1, at + 2, at + 3);
				volume = cells.Length > at + 4 ? at + 4 : -1;
			}

			var f = Split(line, separator);

			try
			{
				var stamp = time >= 0 ? f[time] : f[date] + " " + f[clock];

				if (!TryParseTime(stamp, o.Zone, out var utc))
					throw new FormatException($"time \"{stamp}\"");

				if (o.Symbol != null && symbol >= 0 && f[symbol] != o.Symbol)
					continue;

				rows.Add(new Row
				{
					Time = utc,
					Open = Number(f[open]) * o.PriceScale,
					High = Number(f[high]) * o.PriceScale,
					Low = Number(f[low]) * o.PriceScale,
					Close = Number(f[close]) * o.PriceScale,
					Volume = volume >= 0 && volume < f.Length && f[volume].Length > 0 ? Number(f[volume]) : 0,
					Symbol = symbol >= 0 ? f[symbol] : null
				});
			}
			catch (Exception e) when (e is FormatException || e is IndexOutOfRangeException || e is OverflowException)
			{
				if (++skipped <= 3)
					Console.Error.WriteLine($"{path}:{lineNumber}: skipped ({e.Message})");
			}
		}

		if (skipped > 3)
			Console.Error.WriteLine($"{path}: {skipped} lines skipped in all");

		return rows;
	}

	private static string[] Split(string line, char separator)
	{
		return line.Split(separator).Select(c => c.Trim().Trim('"')).ToArray();
	}

	private static decimal Number(string text)
	{
		return decimal.Parse(text, NumberStyles.Float, Inv);
	}

	private static readonly string[] TimeFormats =
	{
		"yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
		"yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyyMMdd HHmmss", "yyyyMMdd HHmm", "yyyyMMdd HH:mm:ss", "yyyyMMdd HH:mm",
		"yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd HH:mm", "dd.MM.yyyy HH:mm:ss.FFF", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm",
		"MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "M/d/yyyy H:mm:ss", "M/d/yyyy H:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm"
	};

	// a local time in `zone` unless the text carries its own offset; Unix times are UTC
	private static bool TryParseTime(string text, TimeZoneInfo zone, out DateTime utc)
	{
		utc = default;
		text = text.Trim();

		if (long.TryParse(text, NumberStyles.Integer, Inv, out var number) && text.Length >= 9)
		{
			var ticks = number > 100_000_000_000_000_000 ? number / 100
				: number > 100_000_000_000_000 ? number * 10
				: number > 100_000_000_000 ? number * TimeSpan.TicksPerMillisecond
				: number * TimeSpan.TicksPerSecond;
			utc = DateTime.UnixEpoch.AddTicks(ticks);
			return true;
		}

		// "... GMT+0100" / "... UTC"
		var gmt = text.IndexOf(" GMT", StringComparison.OrdinalIgnoreCase);

		if (gmt < 0)
			gmt = text.IndexOf(" UTC", StringComparison.OrdinalIgnoreCase);

		if (gmt > 0)
		{
			var offsetText = text.Substring(gmt + 4).Replace(":", string.Empty);
			var offset = TimeSpan.Zero;

			if (offsetText.Length >= 5 && (offsetText[0] == '+' || offsetText[0] == '-'))
			{
				offset = new TimeSpan(int.Parse(offsetText.Substring(1, 2), Inv), int.Parse(offsetText.Substring(3, 2), Inv), 0);

				if (offsetText[0] == '-')
					offset = -offset;
			}

			if (!DateTime.TryParseExact(text.Substring(0, gmt), TimeFormats, Inv, DateTimeStyles.None, out var local))
				return false;

			utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
			return true;
		}

		var hasOffset = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
			|| (text.Length > 19 && (text[text.Length - 6] == '+' || text[text.Length - 6] == '-') && text[text.Length - 3] == ':');

		if (hasOffset && DateTimeOffset.TryParse(text, Inv, DateTimeStyles.None, out var withOffset))
		{
			utc = withOffset.UtcDateTime;
			return true;
		}

		if (!DateTime.TryParseExact(text, TimeFormats, Inv, DateTimeStyles.None, out var plain)
			&& !DateTime.TryParse(text, Inv, DateTimeStyles.None, out plain))
			return false;

		if (zone == TimeZoneInfo.Utc)
		{
			utc = DateTime.SpecifyKind(plain, DateTimeKind.Utc);
			return true;
		}

		// a time the clocks skipped when summer time began does not exist
		if (zone.IsInvalidTime(plain))
			return false;

		utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(plain, DateTimeKind.Unspecified), zone);
		return true;
	}

	// Ticks as (UTC time, price in ticks). A header names the time (or date and time) and the
	// price (price, last, bid...) columns; without one they are time, price.
	private static List<(long Time, int Price)> ReadTicks(string path, Options o)
	{
		var ticks = new List<(long, int)>();
		char separator = ',';
		int time = -1, date = -1, clock = -1, price = -1;
		var first = true;
		var skipped = 0;

		foreach (var raw in ReadLines(path))
		{
			var line = raw.Trim();

			if (line.Length == 0)
				continue;

			if (first)
			{
				first = false;
				separator = new[] { ',', ';', '\t', '|' }.OrderByDescending(c => line.Count(ch => ch == c)).First();
				var names = Split(line, separator).Select(n => n.Trim('<', '>', ' ').ToLowerInvariant()).ToArray();

				if (names.Any(n => n.Length > 0 && char.IsLetter(n[0])))
				{
					int Find(params string[] candidates) => Array.FindIndex(names, n => candidates.Contains(n));

					time = Find("timestamp", "ts_event", "datetime", "date_time", "time (utc)", "gmt time", "local time", "utc", "ts", "time");
					date = Find("date", "day");
					clock = Find("time");
					price = Find("price", "last", "trade price", "px", "bid", "close", "mid");

					if (date >= 0 && clock >= 0 && clock != date && (time < 0 || time == clock))
						time = -1;
					else
					{
						if (time < 0)
							time = date;

						date = -1;
						clock = -1;
					}

					if ((time < 0 && date < 0) || price < 0)
						throw new InvalidDataException($"{path}: can't find the time and price columns in \"{line}\"");

					continue;
				}

				(time, price) = (0, 1);
			}

			var f = Split(line, separator);

			try
			{
				var stamp = time >= 0 ? f[time] : f[date] + " " + f[clock];

				if (!TryParseTime(stamp, o.Zone, out var utc))
					throw new FormatException($"time \"{stamp}\"");

				var value = Number(f[price]) * o.PriceScale;
				ticks.Add((utc.Ticks, (int)Math.Round(value / o.Tick, MidpointRounding.AwayFromZero)));
			}
			catch (Exception e) when (e is FormatException || e is IndexOutOfRangeException || e is OverflowException)
			{
				if (++skipped <= 3)
					Console.Error.WriteLine($"{path}: skipped \"{line}\" ({e.Message})");
			}
		}

		return ticks;
	}

	// Gives each bar the ticks inside it (null where there are none) and rebuilds those bars
	// from them, so the bars and the path inside them agree.
	private static int[][] AttachTicks(List<IndicatorCandle> candles, List<(long Time, int Price)> ticks, Options o, List<string> notes)
	{
		var paths = new int[candles.Count][];

		if (candles.Count < 2 || ticks.Count == 0)
			return paths;

		// the bar length: the most common step between bars
		var length = candles.Zip(candles.Skip(1), (a, b) => b.Time - a.Time).Where(d => d > TimeSpan.Zero)
			.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key.Ticks;

		var ordered = ticks.Select((t, i) => (t.Time, t.Price, Index: i)).OrderBy(t => t.Time).ThenBy(t => t.Index).ToList();
		var bar = 0;
		var outside = 0;
		var current = new List<int>();
		var currentBar = -1;
		var changed = 0;

		void Close()
		{
			if (currentBar < 0 || current.Count == 0)
				return;

			paths[currentBar] = current.ToArray();
			var c = candles[currentBar];
			var open = current[0] * o.Tick;
			var high = current.Max() * o.Tick;
			var low = current.Min() * o.Tick;
			var close = current[current.Count - 1] * o.Tick;

			if (c.Open != open || c.High != high || c.Low != low || c.Close != close)
				changed++;

			c.Open = open;
			c.High = high;
			c.Low = low;
			c.Close = close;
			current.Clear();
		}

		foreach (var (time, price, _) in ordered)
		{
			while (bar < candles.Count && candles[bar].Time.Ticks + length <= time)
				bar++;

			if (bar >= candles.Count || candles[bar].Time.Ticks > time)
			{
				outside++;
				continue;
			}

			if (bar != currentBar)
			{
				Close();
				currentBar = bar;
			}

			current.Add(price);
		}

		Close();
		var withTicks = paths.Count(p => p != null);
		notes.Add($"{ordered.Count - outside:N0} ticks inside {withTicks:N0} bars ({100.0 * withTicks / candles.Count:0.0}% of them); trades are settled on the ticks there, "
			+ $"on the bars elsewhere{(changed > 0 ? $"; {changed} bars rebuilt from their ticks came out different" : string.Empty)}"
			+ $"{(outside > 0 ? $"; {outside:N0} ticks outside the bars left out" : string.Empty)}");
		return paths;
	}

	// sorted UTC bars on the tick grid; one symbol per trading day when a file mixes contracts
	private static List<IndicatorCandle> BuildCandles(List<Row> rows, Options o, out List<string> notes)
	{
		notes = new List<string>();

		if (o.Symbol == null && rows.Select(r => r.Symbol).Where(s => s != null).Distinct().Skip(1).Any())
		{
			var chosen = rows.GroupBy(r => TradingDay(r.Time))
				.ToDictionary(g => g.Key, g => g.GroupBy(r => r.Symbol).OrderByDescending(s => s.Sum(r => r.Volume)).First().Key);
			rows = rows.Where(r => chosen[TradingDay(r.Time)] == r.Symbol).ToList();
			var sequence = chosen.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
			var switches = sequence.Zip(sequence.Skip(1), (a, b) => a != b).Count(x => x);
			notes.Add($"several contracts in the file: each trading day keeps its most traded one ({switches} rolls, prices not adjusted)");
		}

		rows = rows.OrderBy(r => r.Time).ToList();

		if (o.CloseTimes && rows.Count > 1)
		{
			var step = rows.Zip(rows.Skip(1), (a, b) => b.Time - a.Time).Where(d => d > TimeSpan.Zero)
				.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;

			foreach (var r in rows)
				r.Time -= step;
		}

		var candles = new List<IndicatorCandle>();
		var duplicates = 0;
		var repaired = 0;
		DateTime last = DateTime.MinValue;

		decimal Grid(decimal price) => Math.Round(price / o.Tick, MidpointRounding.AwayFromZero) * o.Tick;

		foreach (var r in rows)
		{
			if (r.Time < o.From || r.Time >= o.To)
				continue;

			if (r.Time == last)
			{
				duplicates++;
				continue;
			}

			last = r.Time;
			var open = Grid(r.Open);
			var close = Grid(r.Close);
			var high = Math.Max(Grid(r.High), Math.Max(open, close));
			var low = Math.Min(Grid(r.Low), Math.Min(open, close));

			if (high != Grid(r.High) || low != Grid(r.Low))
				repaired++;

			candles.Add(new IndicatorCandle { Open = open, High = high, Low = low, Close = close, Volume = r.Volume, Time = r.Time });
		}

		if (duplicates > 0)
			notes.Add($"{duplicates} bars with a repeated time left out");

		if (repaired > 0)
			notes.Add($"{repaired} bars whose high / low did not contain the open and close were widened");

		return candles;
	}

	private static DateTime NewYorkTime(DateTime utc)
	{
		return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NewYork);
	}

	private static DateTime TradingDay(DateTime utc)
	{
		var ny = NewYorkTime(utc);
		return ny.TimeOfDay >= new TimeSpan(18, 0, 0) ? ny.Date.AddDays(1) : ny.Date;
	}

	// the preset, then every --set; returns what differs from the defaults
	private static List<string> ApplySettings(FvgReactionLiquiditySweep ind, Options o)
	{
		var settings = new List<(string, string)>();

		// the 1-minute NQ suggestions: a 30-bar swing, 2-point gaps, 10 bars between signals
		if (o.Preset == "1min")
			settings.AddRange(new[] { ("SwingLookback", "30"), ("MinFvgTicks", "8"), ("SignalCooldownBars", "10") });

		settings.AddRange(o.Settings);
		var changed = new List<string>();

		foreach (var (name, value) in settings)
		{
			var property = IndicatorType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
				?? throw new ArgumentException($"no setting called {name}");
			var type = property.PropertyType;
			object parsed = type.IsEnum ? Enum.Parse(type, value, true)
				: type == typeof(TimeSpan) ? TimeSpan.Parse(value, Inv)
				: type == typeof(bool) ? bool.Parse(value)
				: Convert.ChangeType(value, type, Inv);
			property.SetValue(ind, parsed);
			changed.Add($"{property.Name} = {property.GetValue(ind)}");
		}

		return changed;
	}

	#endregion

	#region Results

	private static object Get(object obj, string field)
	{
		return obj.GetType().GetField(field).GetValue(obj);
	}

	private static List<Trade> ReadTrades(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles, Options o)
	{
		var list = (IList)IndicatorType.GetField("_trades", Private).GetValue(ind);
		var triggerLabel = IndicatorType.GetMethod("TriggerLabel", PrivateStatic);
		var setupName = IndicatorType.GetMethod("SetupName", PrivateStatic);
		var levelTag = IndicatorType.GetMethod("LevelTag", PrivateStatic);
		var tradeType = IndicatorType.GetNestedType("SignalTrade", BindingFlags.NonPublic);
		var labelPercents = IndicatorType.GetMethod("LabelPercents", PrivateStatic, null, new[] { tradeType }, null);

		// (pattern, name, candles) tuples
		var patternNames = ((IEnumerable)IndicatorType.GetField("CandlePatternInfo", PrivateStatic).GetValue(null)).Cast<object>()
			.Select(info => (Pattern: Convert.ToInt64(Get(info, "Item1"), Inv), Name: (string)Get(info, "Item2")))
			.ToList();
		var tick = ind.InstrumentInfo.TickSize;
		var trades = new List<Trade>();

		foreach (var t in list)
		{
			var estimate = Get(t, "Estimate");
			double Odds(string name) => (double)estimate.GetType().GetProperty(name).GetValue(estimate);
			var patterns = Convert.ToInt64(Get(t, "CandlePatterns"), Inv);
			var level = Get(t, "SweptLevel");
			var isLong = (bool)Get(t, "IsLong");
			var entry = (decimal)Get(t, "EntryPrice");
			var exit = (decimal)Get(t, "ExitPrice");
			var entryBar = (int)Get(t, "EntryBar");

			trades.Add(new Trade
			{
				EntryBar = entryBar,
				ExitBar = (int)Get(t, "ExitBar"),
				SignalTime = candles[entryBar].Time,
				IsLong = isLong,
				IsShown = (bool)Get(t, "IsShown"),
				Ambiguous = (bool)Get(t, "AmbiguousExit"),
				Setup = (string)triggerLabel.Invoke(null, new[] { Get(t, "Trigger") }),
				SetupName = (string)setupName.Invoke(null, new[] { t }),
				Level = level == null ? null : (string)levelTag.Invoke(null, new[] { level }),
				Patterns = string.Join(" + ", patternNames.Where(p => (patterns & p.Pattern) != 0).Select(p => p.Name)),
				Outcome = Get(t, "Outcome").ToString(),
				Entry = entry,
				TakeProfit = (decimal)Get(t, "TakeProfitPrice"),
				StopLoss = (decimal)Get(t, "StopLossPrice"),
				Exit = exit,
				Ticks = (exit - entry) / tick * (isLong ? 1 : -1),
				LabelTp = ((int[])labelPercents.Invoke(null, new[] { t }))[0],
				TpOdds = Odds("TakeProfit"),
				BeOdds = Odds("BreakEven"),
				SlOdds = Odds("StopLoss"),
				ExpectedTicks = Odds("ExpectedTicks"),
				LimitEntry = (bool)Get(t, "LimitEntry"),
				FillBar = (int)Get(t, "FillBar"),
				StepTicks = (bool)Get(t, "HasBreakEven") ? (int)Get(t, "TriggerTicks") - (int)Get(t, "LockedTicks") : 0
			});
		}

		// commission, and a tick of slippage on every market order: the entry at the close (not a
		// limit entry) and the exit
		foreach (var t in trades.Where(t => t.Ended))
			t.Net = t.Ticks - o.Commission - o.Slippage * (t.LimitEntry ? 1 : 2);

		return trades;
	}

	private static string Report(FvgReactionLiquiditySweep ind, List<IndicatorCandle> candles, List<Trade> trades, List<Trade> assumed, List<Trade> worst,
		int[][] paths, Options o, List<string> changed, List<string> notes, TimeSpan elapsed)
	{
		var sb = new StringBuilder();
		void Line(string text = "") => sb.AppendLine(text);

		var shown = trades.Where(t => t.IsShown).ToList();
		var closed = shown.Where(t => t.Closed).OrderBy(t => t.ExitBar).ThenBy(t => t.EntryBar).ToList();
		var ended = shown.Where(t => t.Ended).OrderBy(t => t.ExitBar).ThenBy(t => t.EntryBar).ToList();

		Tally Sum(IEnumerable<Trade> group)
		{
			var tally = new Tally();

			foreach (var t in group)
				tally.Add(t);

			return tally;
		}

		var all = Sum(ended);

		// the chart's panel counts the same trades, before costs
		int Field(string name) => (int)IndicatorType.GetField(name, Private).GetValue(ind);
		decimal Ticks(string name) => (decimal)IndicatorType.GetField(name, Private).GetValue(ind);
		var panelTp = Field("_longWins") + Field("_shortWins");
		var panelBe = Field("_longBreakEvens") + Field("_shortBreakEvens");
		var panelSl = Field("_longLosses") + Field("_shortLosses");
		var panelNet = Ticks("_longNetTicks") + Ticks("_shortNetTicks");
		var panelExpired = Field("_expired");
		var agrees = panelTp == all.Tp && panelBe == all.Be && panelSl == all.Sl && panelExpired == all.Ex && panelNet == all.Ticks;

		// the other replays' trades on the chart that ended: on the bars with the assumed order
		// (when the result is settled on ticks), and with the worst-case order
		var assumedEnded = assumed?.Where(t => t.IsShown && t.Ended).ToList();
		var worstEnded = worst?.Where(t => t.IsShown && t.Ended).ToList();

		// how much of the trades' time the ticks covered: the bars after each entry, to its exit
		double? coverage = null;

		if (paths != null)
		{
			var lived = closed.Where(t => t.ExitBar > t.EntryBar).SelectMany(t => Enumerable.Range(t.EntryBar + 1, t.ExitBar - t.EntryBar)).ToList();
			coverage = lived.Count == 0 ? 0 : (double)lived.Count(i => paths[i] != null) / lived.Count;
		}

		if (o.Summary)
			return Summary(ended, assumedEnded, worstEnded, coverage, o, agrees) + Environment.NewLine;

		var first = NewYorkTime(candles[0].Time);
		var last = NewYorkTime(candles[candles.Count - 1].Time);
		var days = candles.Select(c => TradingDay(c.Time)).Distinct().Count();

		Line($"FVG Reaction + Liquidity Sweep, backtest on {string.Join(", ", o.Files.Select(Path.GetFileName))}");
		Line($"{candles.Count.ToString("N0", Inv)} bars over {days} trading days, {first:yyyy-MM-dd HH:mm} to {last:yyyy-MM-dd HH:mm} New York; tick {o.Tick}, ${o.TickValue} per tick");
		Line($"Settings: {(o.Preset == "1min" ? "1-minute preset" : "indicator defaults")}{(changed.Count > 0 ? " with " + string.Join(", ", changed) : string.Empty)}");
		Line($"Signal hours {ind.SignalHours}, entry {ind.Entry}, bracket {(string)IndicatorType.GetMethod("BracketText", Private).Invoke(ind, null)}, "
			+ $"one trade at a time {(ind.OneTradeAtATime ? "on" : "off")}, high / low order {ind.SameBarRule}");
		Line($"Costs: {o.Commission} tick{(o.Commission == 1 ? string.Empty : "s")} commission a round trip, {o.Slippage} of slippage on each market order "
			+ $"({o.Commission + 2 * o.Slippage} a trade entered at the close, {o.Commission + o.Slippage} with a limit entry)");

		foreach (var note in notes)
			Line($"Note: {note}");

		Line();
		var open = shown.Count(t => t.Outcome == "Open");
		var expired = shown.Count(t => t.Outcome == "Expired");
		var missed = shown.Count(t => t.Outcome == "Missed");
		Line($"Signals on the chart: {shown.Count} ({closed.Count} at TP / BE / SL, {expired} expired, {missed} limit orders not filled, {open} still open at the end); "
			+ $"{trades.Count - shown.Count} more were hidden (a trade was already open, or filtered) and only counted for the odds");
		Line();

		string Percent(int part, int whole) => whole == 0 ? "-" : (100.0 * part / whole).ToString("0.0", Inv) + "%";
		string Signed(decimal value, string format = "0") => value.ToString("+" + format + ";-" + format + ";0", Inv);
		string Factor(Tally t) => t.Lost == 0 ? (t.Won > 0 ? "inf" : "-") : (t.Won / t.Lost).ToString("0.00", Inv);

		// a table of tallies: rates, gross and net ticks per trade, the net total and profit factor
		void Table(string title, IEnumerable<(string Name, Tally Tally)> groups)
		{
			var rows = groups.Where(g => g.Tally.Count > 0).ToList();

			if (rows.Count == 0)
				return;

			var width = Math.Max(title.Length, rows.Max(r => r.Name.Length));
			var withExpired = rows.Any(r => r.Tally.Ex > 0);
			Line($"{title.PadRight(width)}  {"trades",6}  {"TP",5}  {"BE",5}  {"SL",5}  {(withExpired ? $"{"exp",5}  " : string.Empty)}{"TP rate",7}  {"TP+BE",6}  "
				+ $"{"gross/tr",8}  {"net/tr",7}  {"net ticks",9}  {"PF net",6}");

			foreach (var (name, t) in rows)
			{
				Line($"{name.PadRight(width)}  {t.Count,6}  {t.Tp,5}  {t.Be,5}  {t.Sl,5}  {(withExpired ? $"{t.Ex,5}  " : string.Empty)}{Percent(t.Tp, t.Count),7}  "
					+ $"{Percent(t.Tp + t.Be, t.Count),6}  {Signed(t.Ticks / t.Count, "0.0"),8}  {Signed(t.NetPerTrade, "0.0"),7}  {Signed(t.Net),9}  {Factor(t),6}");
			}

			Line();
		}

		var summaryRows = new List<(string, Tally)> { ("All", all), ("Buys", Sum(ended.Where(t => t.IsLong))), ("Shorts", Sum(ended.Where(t => !t.IsLong))) };

		if (o.Split.HasValue)
		{
			var split = o.Split.Value;
			summaryRows.Add(($"Before {split:yyyy-MM-dd}", Sum(ended.Where(t => t.SignalTime < split))));
			summaryRows.Add(($"From {split:yyyy-MM-dd}", Sum(ended.Where(t => t.SignalTime >= split))));
		}

		Table("Signals that ended", summaryRows);

		// the equity curve after costs of the trades on the chart, one contract each
		decimal equity = 0, peak = 0, drawdown = 0;
		var streak = 0;
		var worstStreak = 0;

		foreach (var t in ended)
		{
			equity += t.Net;
			peak = Math.Max(peak, equity);
			drawdown = Math.Max(drawdown, peak - equity);
			streak = t.Outcome == "StopLoss" ? streak + 1 : 0;
			worstStreak = Math.Max(worstStreak, streak);
		}

		var months = ended.GroupBy(t => NewYorkTime(t.SignalTime).ToString("yyyy-MM", Inv)).ToList();
		Line($"Before costs {Signed(all.Ticks)} ticks = {Money(all.Ticks * o.TickValue)} per contract; after costs {Signed(all.Net)} ticks = {Money(all.Net * o.TickValue)} "
			+ $"({Money(all.Net * o.TickValue / 10)} per micro), {Signed(all.NetPerTrade, "0.0")} a trade, {all.TStat.ToString("0.0", Inv)} standard errors from zero");
		Line($"After costs: largest drawdown {drawdown.ToString("0", Inv)} ticks ({Money(-drawdown * o.TickValue)}), {months.Count(m => m.Sum(t => t.Net) > 0)} of {months.Count} months up; "
			+ $"longest run of stop losses {worstStreak}");
		Line();

		// how much the result leans on the assumed order of each bar's high and low
		var onBars = assumedEnded ?? ended;
		var decided = (assumed?.Where(t => t.IsShown && t.Closed) ?? closed).Count(t => t.Ambiguous);
		var decidedOf = (assumed?.Where(t => t.IsShown && t.Closed) ?? closed).Count();

		if (worstEnded != null)
		{
			// each way of settling the trades, overall and before / from the split
			var ways = new List<(string Name, string Short, List<Trade> Trades)>();

			if (assumedEnded != null)
				ways.Add(("On the ticks", "On the ticks", ended));

			ways.Add(($"As assumed ({ind.SameBarRule})", "As assumed", onBars));
			ways.Add(("Worst case (stop first)", "Worst case", worstEnded));
			var orderRows = ways.Select(w => (w.Name, Sum(w.Trades))).ToList();

			if (o.Split.HasValue)
			{
				var split = o.Split.Value;
				orderRows.AddRange(ways.Select(w => ($"{w.Short}, before {split:yyyy-MM-dd}", Sum(w.Trades.Where(t => t.SignalTime < split)))));
				orderRows.AddRange(ways.Select(w => ($"{w.Short}, from {split:yyyy-MM-dd}", Sum(w.Trades.Where(t => t.SignalTime >= split)))));
			}

			Table(assumedEnded != null ? "Ticks against bars" : "High / low order inside a bar", orderRows);

			if (assumedEnded != null)
			{
				Line($"The first rows follow the ticks inside the bars that have them, as a live chart does ({coverage * 100:0.0}% of the bars the trades lived through); "
					+ "the others settle the same history on the bars alone, with an assumed or the worst-case order of each bar's high and low.");
			}

			Line($"Bars don't show the path inside them. On {decided} of {decidedOf} trades ({Percent(decided, decidedOf)}) the order of the exit bar's "
				+ "high and low decided the result on the bars, and more depend on the path unflagged: after a break-even trigger inside a bar, price often "
				+ "comes back to the moved stop before the close, which no order of high and low shows. On bars alone the truth usually lies between the "
				+ "assumed and the worst case: nearer the first when the break-even step is wide next to a typical bar, nearer the worst case when it is narrow.");

			// how wide the break-even step is next to the bars the trades lived through
			var ranges = closed.Where(t => t.ExitBar > t.EntryBar)
				.SelectMany(t => Enumerable.Range(t.EntryBar + 1, t.ExitBar - t.EntryBar))
				.Select(i => (candles[i].High - candles[i].Low) / o.Tick)
				.OrderBy(x => x)
				.ToList();
			var steps = closed.Where(t => t.StepTicks > 0).Select(t => t.StepTicks).OrderBy(x => x).ToList();

			if (ranges.Count > 0 && steps.Count > 0)
			{
				Line($"Here the median bar during a trade spans {ranges[ranges.Count / 2].ToString("0", Inv)} ticks, the break-even step "
					+ $"(trigger back to the moved stop) {steps[steps.Count / 2]}. "
					+ (paths != null ? "The ticks settle it where they cover the trades." : "tests/PathCheck measures what bars get wrong for a bracket on simulated tick paths; tick data (--ticks) settles it."));
			}

			Line();
		}
		else
		{
			Line($"On {decided} of {decidedOf} trades ({Percent(decided, decidedOf)}) the order of the exit bar's high and low decided the result; "
				+ "every such bar was taken in the order worst for the trade.");
			Line();
		}

		Table("By year", ended.GroupBy(t => NewYorkTime(t.SignalTime).Year).OrderBy(g => g.Key).Select(g => (g.Key.ToString(Inv), Sum(g))));
		Table("By month", months.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, Sum(g))));
		Table("By setup", ended.GroupBy(t => t.Setup).OrderByDescending(g => g.Count()).Select(g => (g.Key, Sum(g))));
		Table("Key sweeps by level", ended.Where(t => t.Level != null).GroupBy(t => t.Level).OrderByDescending(g => g.Count()).Select(g => (g.Key, Sum(g))));
		Table("By New York hour", ended.GroupBy(t => NewYorkTime(t.SignalTime).Hour).OrderBy(g => g.Key).Select(g => ($"{g.Key:00}:00", Sum(g))));
		Table("By weekday", ended.GroupBy(t => NewYorkTime(t.SignalTime).DayOfWeek).OrderBy(g => ((int)g.Key + 6) % 7).Select(g => (g.Key.ToString(), Sum(g))));
		Table("By pattern", ended.SelectMany(t => (t.Patterns.Length == 0 ? "No pattern" : t.Patterns).Split(" + "), (t, p) => (t, p))
			.GroupBy(x => x.p).OrderByDescending(g => g.Count()).Take(12).Select(g => (g.Key, Sum(g.Select(x => x.t)))));
		Table("By expected ticks on the label", ended.GroupBy(t => t.ExpectedTicks < 0 ? "below 0" : t.ExpectedTicks < 5 ? "0 to 5" : t.ExpectedTicks < 10 ? "5 to 10" : "10 and more")
			.OrderBy(g => g.Min(t => t.ExpectedTicks)).Select(g => (g.Key, Sum(g))));

		// were the odds on the labels right?
		var buckets = new[] { (0, 20), (20, 30), (30, 40), (40, 50), (50, 60), (60, 101) };
		Line("Odds check: the TP odds a label showed, and how often those signals hit TP");

		foreach (var (from, to) in buckets)
		{
			var group = closed.Where(t => t.LabelTp >= from && t.LabelTp < to).ToList();

			if (group.Count > 0)
				Line($"  said TP {from}-{Math.Min(to, 100)}%: hit {Percent(group.Count(t => t.Outcome == "TakeProfit"), group.Count)} of {group.Count}");
		}

		Line();

		var hidden = trades.Where(t => !t.IsShown && t.Ended).ToList();

		if (hidden.Count > 0)
			Table("Every signal that ended, hidden ones too", new[] { ("All", Sum(trades.Where(t => t.Ended))), ("Hidden only", Sum(hidden)) });

		Line(agrees
			? $"The chart's panel shows the same totals ({panelTp} TP, {panelBe} BE, {panelSl} SL, {panelExpired} expired, {Signed(panelNet)}t before costs)."
			: $"WARNING: the panel's totals ({panelTp} TP, {panelBe} BE, {panelSl} SL, {panelExpired} expired, {Signed(panelNet)}t) differ from the trade list.");
		Line($"Ran in {elapsed.TotalSeconds.ToString("0", Inv)} s.");
		return sb.ToString();
	}

	// one line of JSON: every signal that ended, and before / from the split when there is one
	private static string Summary(List<Trade> ended, List<Trade> assumedEnded, List<Trade> worstEnded, double? coverage, Options o, bool agrees)
	{
		string Part(IEnumerable<Trade> group)
		{
			var list = group.ToList();
			var t = new Tally();

			foreach (var trade in list)
				t.Add(trade);

			decimal equity = 0, peak = 0, drawdown = 0;

			foreach (var trade in list)
			{
				equity += trade.Net;
				peak = Math.Max(peak, equity);
				drawdown = Math.Max(drawdown, peak - equity);
			}

			var months = list.GroupBy(x => NewYorkTime(x.SignalTime).ToString("yyyy-MM", Inv)).ToList();
			var days = list.Select(x => NewYorkTime(x.SignalTime).Date).Distinct().Count();
			string N(double x) => double.IsNaN(x) || double.IsInfinity(x) ? "0" : x.ToString("0.###", Inv);
			return $"{{\"trades\":{t.Count},\"days\":{days},\"tp\":{t.Tp},\"be\":{t.Be},\"sl\":{t.Sl},\"exp\":{t.Ex},\"gross\":{N((double)t.Ticks)},\"net\":{N((double)t.Net)},"
				+ $"\"netPerTrade\":{N((double)t.NetPerTrade)},\"t\":{N(t.TStat)},\"pf\":{N(t.Lost == 0 ? 0 : (double)(t.Won / t.Lost))},\"drawdown\":{N((double)drawdown)},"
				+ $"\"monthsUp\":{months.Count(m => m.Sum(x => x.Net) > 0)},\"months\":{months.Count},\"assumed\":{list.Count(x => x.Ambiguous)}}}";
		}

		string Parts(List<Trade> list)
		{
			var parts = new List<string> { $"\"all\":{Part(list)}" };

			if (o.Split.HasValue)
			{
				parts.Add($"\"before\":{Part(list.Where(t => t.SignalTime < o.Split.Value))}");
				parts.Add($"\"from\":{Part(list.Where(t => t.SignalTime >= o.Split.Value))}");
			}

			return string.Join(",", parts);
		}

		var json = Parts(ended);

		// with ticks: the same parts on the bars alone, with the assumed order inside each bar
		if (assumedEnded != null)
			json += $",\"assumed\":{{{Parts(assumedEnded)}}},\"tickCoverage\":{coverage.GetValueOrDefault().ToString("0.####", Inv)}";

		// the same parts with the worst-case order inside each bar
		if (worstEnded != null)
			json += $",\"worst\":{{{Parts(worstEnded)}}}";

		return "{" + json + $",\"panelAgrees\":{(agrees ? "true" : "false")}}}";
	}

	private static string Money(decimal dollars)
	{
		return (dollars < 0 ? "-$" : "$") + Math.Abs(dollars).ToString("N0", Inv);
	}

	private static void WriteTrades(string path, List<Trade> trades, List<IndicatorCandle> candles)
	{
		using var writer = new StreamWriter(path);
		writer.WriteLine("signal_bar_ny,side,setup,setup_name,patterns,entry,limit_entry,fill_bar_ny,take_profit,stop_loss,exit_bar_ny,exit,outcome,ticks,net_ticks,shown,high_low_assumed,label_tp,p_tp,p_be,p_sl,ev_ticks");

		string Csv(string text) => text.Contains(',') ? $"\"{text}\"" : text;

		foreach (var t in trades.OrderBy(t => t.EntryBar))
		{
			var exitTime = t.ExitBar >= 0 ? NewYorkTime(candles[t.ExitBar].Time).ToString("yyyy-MM-dd HH:mm", Inv) : string.Empty;
			var fillTime = t.FillBar >= 0 ? NewYorkTime(candles[t.FillBar].Time).ToString("yyyy-MM-dd HH:mm", Inv) : string.Empty;
			writer.WriteLine(string.Join(",",
				NewYorkTime(t.SignalTime).ToString("yyyy-MM-dd HH:mm", Inv), t.IsLong ? "BUY" : "SHORT", Csv(t.Setup), Csv(t.SetupName), Csv(t.Patterns),
				t.Entry.ToString(Inv), t.LimitEntry ? "1" : "0", fillTime, t.TakeProfit.ToString(Inv), t.StopLoss.ToString(Inv), exitTime,
				t.ExitBar >= 0 ? t.Exit.ToString(Inv) : string.Empty, t.Outcome, t.Ended ? t.Ticks.ToString("0", Inv) : string.Empty,
				t.Ended ? t.Net.ToString("0.##", Inv) : string.Empty, t.IsShown ? "1" : "0", t.Ambiguous ? "1" : "0", t.LabelTp.ToString(Inv),
				t.TpOdds.ToString("0.000", Inv), t.BeOdds.ToString("0.000", Inv), t.SlOdds.ToString("0.000", Inv), t.ExpectedTicks.ToString("0.0", Inv)));
		}
	}

	#endregion
}

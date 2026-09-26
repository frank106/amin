// Sums up the executor's CSV logs - see ExecutionLogReport.cs and tests/README.md:
//   dotnet run -c Release --project tests/LogReport
//   dotnet run -c Release --project tests/LogReport -- D:\logs --mode live --from 2026-10-01 --to 2026-10-31
// Without a folder it reads the indicator's default one: %APPDATA%\ATAS\FvgExecution on Windows,
// ~/Library/Application Support/ATAS/FvgExecution on a Mac. Paper trading unless --mode says.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

internal static class Program
{
	private const string Usage = "usage: LogReport [folder or .csv files] [--mode paper|live|all] [--from yyyy-MM-dd] [--to yyyy-MM-dd]";

	public static int Main(string[] args)
	{
		var paths = new List<string>();
		var mode = "paper";
		DateTime? from = null;
		DateTime? to = null;

		try
		{
			for (var i = 0; i < args.Length; i++)
			{
				switch (args[i])
				{
					case "--mode":
						mode = args[++i].ToLowerInvariant();
						break;

					case "--from":
						from = DateTime.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
						break;

					case "--to":
						to = DateTime.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
						break;

					case "-h":
					case "--help":
						Console.WriteLine(Usage);
						return 0;

					default:
						paths.Add(args[i]);
						break;
				}
			}
		}
		catch (Exception ex) when (ex is IndexOutOfRangeException || ex is FormatException)
		{
			Console.Error.WriteLine(Usage);
			return 2;
		}

		if (mode != "paper" && mode != "live" && mode != "all")
		{
			Console.Error.WriteLine(Usage);
			return 2;
		}

		// where the indicator logs by default: ATAS X on a Mac keeps it under Library/Application
		// Support, which a plain .NET program there doesn't call its application data folder
		if (paths.Count == 0)
		{
			paths.Add(OperatingSystem.IsMacOS()
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ATAS", "FvgExecution")
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "FvgExecution"));
		}

		// one file per trading day, instrument and mode: 2026-09-25_NQ_paper.csv
		bool Wanted(string file)
		{
			var name = Path.GetFileNameWithoutExtension(file);

			if (mode != "all" && !name.EndsWith("_" + mode, StringComparison.OrdinalIgnoreCase))
				return false;

			if (name.Length < 10 || !DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
				return from == null && to == null;

			return (from == null || day >= from) && (to == null || day <= to);
		}

		var files = paths
			.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.csv") : File.Exists(p) ? new[] { p } : Array.Empty<string>())
			.Where(Wanted)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
			.ToList();

		if (files.Count == 0)
		{
			Console.Error.WriteLine($"no {(mode == "all" ? string.Empty : mode + " ")}logs in {string.Join(", ", paths)}");
			return 1;
		}

		Console.Write(ExecutionLogReport.Format(ExecutionLogReport.Build(ExecutionLogReport.Read(files)), files.Count));
		return 0;
	}
}

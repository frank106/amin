using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using ATAS.Indicators;

using OFT.Rendering.Context;
using OFT.Rendering.Tools;

using Color = System.Drawing.Color;

namespace ATAS.Indicators.Technical
{
	// FVG Bar Export: writes the bars a chart has loaded - and their footprint - to CSV files for
	// the backtest runner (tests/Backtest), so a backtest runs on ATAS's own data: its volume, its
	// delta, and the bid and ask volume at every price, which the big-fill signals and the delta
	// and order-flow confirmations need. Add it to a chart with as many days loaded as the data
	// feed gives: it writes once the chart has loaded its history (again after a recalculation),
	// leaves the forming bar out, and says on the chart what it wrote and where.
	//
	//   <instrument>_<period>_<first day>_<last day>_bars.csv          time,open,high,low,close,volume,delta,bid,ask
	//   <instrument>_<period>_<first day>_<last day>_footprint.csv.gz  time,price,volume,bid,ask
	//
	// Times are the bars' open times in UTC.
	[DisplayName("FVG Bar Export")]
	[Category("My Indicators")]
	public class FvgBarExport : Indicator
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly Color StatusColor = Color.FromArgb(255, 144, 150, 162);

		private readonly object _sync = new object();
		private StreamWriter _bars;
		private StreamWriter _footprint;
		private string _folder;
		private string _barsPartial;
		private string _footprintPartial;
		private int _nextBar;               // the next closed bar to write
		private int _written;
		private long _levels;
		private DateTime _firstTime;
		private DateTime _lastTime;
		private readonly List<TimeSpan> _steps = new List<TimeSpan>();
		private bool _done;
		private string _status = "FVG Bar Export: reading the chart's history...";
		private string _where = string.Empty;

		public FvgBarExport() : base(true)
		{
			DenyToChangePanel = true;
			EnableCustomDrawing = true;
			SubscribeToDrawingEvents(DrawingLayouts.Final);

			((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
			DataSeries[0].IsHidden = true;
		}

		[Display(Name = "Folder (empty = ATAS's data folder)", GroupName = "Export", Order = 10,
			Description = "Where the files go. Empty: ATAS/FvgExport in the application data folder (%APPDATA%\\ATAS\\FvgExport on Windows).")]
		public string Folder { get; set; } = string.Empty;

		[Display(Name = "Include the footprint", GroupName = "Export", Order = 11,
			Description = "Also write the volume, bid and ask at every price of every bar - a much bigger file, compressed - for the big-fill signals and the order-flow confirmation.")]
		public bool IncludeFootprint { get; set; } = true;

		protected override void OnRecalculate()
		{
			lock (_sync)
				Reset();
		}

		protected override void OnCalculate(int bar, decimal value)
		{
			lock (_sync)
			{
				if (bar == 0 && _nextBar > 0)
					Reset();

				if (_done)
					return;

				try
				{
					// every bar before this one is final
					while (_nextBar < bar)
						Write(_nextBar++);

					// the chart has loaded its history once its last bar is calculated; that one is
					// still forming and is left out
					if (bar == CurrentBar - 1)
						Finish();
				}
				catch (Exception ex)
				{
					// an export that fails says so on the chart; it never takes ATAS down with it
					Discard();
					_done = true;
					_status = $"FVG Bar Export failed: {ex.Message}";
					_where = string.Empty;
				}
			}
		}

		protected override void OnDispose()
		{
			lock (_sync)
				Discard();

			base.OnDispose();
		}

		protected override void OnRender(RenderContext context, DrawingLayouts layout)
		{
			string status, where;

			lock (_sync)
			{
				status = _status;
				where = _where;
			}

			if (ChartInfo == null)
				return;

			var region = ChartInfo.PriceChartContainer.Region;
			var font = new RenderFont("Arial", 10);
			context.DrawString(status, font, StatusColor, region.X + 10, region.Y + region.Height - 44);

			if (where.Length > 0)
				context.DrawString(where, font, StatusColor, region.X + 10, region.Y + region.Height - 26);
		}

		private void Reset()
		{
			Discard();
			_nextBar = 0;
			_written = 0;
			_levels = 0;
			_steps.Clear();
			_done = false;
			_status = "FVG Bar Export: reading the chart's history...";
			_where = string.Empty;
		}

		private void Write(int bar)
		{
			var candle = GetCandle(bar);

			if (_bars == null)
				Open(candle);

			_bars.Write(Csv(candle.Time));
			_bars.Write(',');
			_bars.Write(string.Join(",", new[] { candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, candle.Delta, candle.Bid, candle.Ask }.Select(Number)));
			_bars.Write('\n');

			if (_footprint != null)
			{
				foreach (var level in candle.GetAllPriceLevels().OrderBy(l => l.Price))
				{
					_footprint.Write(Csv(candle.Time));
					_footprint.Write(',');
					_footprint.Write(string.Join(",", Number(level.Price), Number(level.Volume), Number(level.Bid), Number(level.Ask)));
					_footprint.Write('\n');
					_levels++;
				}
			}

			if (_written > 0 && _steps.Count < 500)
				_steps.Add(candle.Time - _lastTime);

			if (_written == 0)
				_firstTime = candle.Time;

			_lastTime = candle.Time;
			_written++;
		}

		// the files are written under temporary names and take their real ones once complete, so
		// a half-written export is never taken for a whole one
		private void Open(IndicatorCandle first)
		{
			_folder = Folder.Trim().Length > 0
				? Folder.Trim()
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "FvgExport");
			Directory.CreateDirectory(_folder);

			var stem = Path.Combine(_folder, $"{FileName()}_{Guid.NewGuid():N}");
			_barsPartial = stem + "_bars.partial";
			_bars = new StreamWriter(_barsPartial, false, new UTF8Encoding(false));
			_bars.Write("time,open,high,low,close,volume,delta,bid,ask\n");

			if (!IncludeFootprint)
				return;

			_footprintPartial = stem + "_footprint.partial";
			_footprint = new StreamWriter(new GZipStream(File.Create(_footprintPartial), CompressionLevel.Optimal), new UTF8Encoding(false));
			_footprint.Write("time,price,volume,bid,ask\n");
		}

		private void Finish()
		{
			_done = true;

			if (_written == 0)
			{
				_status = "FVG Bar Export: the chart has no closed bars yet";
				return;
			}

			_bars.Dispose();
			_bars = null;
			_footprint?.Dispose();
			_footprint = null;

			var name = $"{FileName()}_{Period()}_{_firstTime:yyyy-MM-dd}_{_lastTime:yyyy-MM-dd}";
			var barsFile = Path.Combine(_folder, name + "_bars.csv");
			File.Move(_barsPartial, barsFile, true);
			_barsPartial = null;

			if (_footprintPartial != null)
			{
				File.Move(_footprintPartial, Path.Combine(_folder, name + "_footprint.csv.gz"), true);
				_footprintPartial = null;
			}

			_status = $"FVG Bar Export: {_written.ToString("N0", Inv)} bars, {_firstTime:yyyy-MM-dd HH:mm} to {_lastTime:yyyy-MM-dd HH:mm} UTC"
				+ (IncludeFootprint ? $", {_levels.ToString("N0", Inv)} footprint prices" : ", no footprint");
			_where = $"written to {Path.Combine(_folder, name)}_*";
		}

		// closes the files of an export that won't finish, and deletes them
		private void Discard()
		{
			try
			{
				_bars?.Dispose();
				_footprint?.Dispose();
			}
			catch (IOException)
			{
				// being thrown away anyway
			}

			_bars = null;
			_footprint = null;

			foreach (var partial in new[] { _barsPartial, _footprintPartial })
			{
				try
				{
					if (partial != null && File.Exists(partial))
						File.Delete(partial);
				}
				catch (IOException)
				{
					// left behind, under a name no reader takes
				}
			}

			_barsPartial = null;
			_footprintPartial = null;
		}

		private string FileName()
		{
			var name = string.IsNullOrWhiteSpace(InstrumentInfo?.Instrument) ? "chart" : InstrumentInfo.Instrument;
			var invalid = Path.GetInvalidFileNameChars();
			return new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
		}

		// the bars' usual spacing, "1m", "30s", "4h" - or "bars" for charts whose bars aren't time-based
		private string Period()
		{
			var usual = _steps.Where(s => s > TimeSpan.Zero).GroupBy(s => s).OrderByDescending(g => g.Count()).FirstOrDefault();

			if (usual == null || usual.Count() < _steps.Count * 0.6)
				return "bars";

			var step = usual.Key;
			return step.TotalHours >= 1 && step.TotalHours % 1 == 0 ? $"{step.TotalHours:0}h"
				: step.TotalMinutes >= 1 && step.TotalMinutes % 1 == 0 ? $"{step.TotalMinutes:0}m"
				: $"{step.TotalSeconds:0}s";
		}

		private static string Csv(DateTime time)
		{
			return time.ToString("yyyy-MM-dd HH:mm:ss", Inv);
		}

		private static string Number(decimal value)
		{
			return value.ToString("0.########", Inv);
		}
	}
}

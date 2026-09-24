// Compile-time stand-ins for the slice of the ATAS SDK that FvgReactionLiquiditySweep.cs
// uses, so the indicator can be built and exercised without an ATAS install. Every
// member mirrors how the official AtasPlatform/Indicators sources call it; where the
// real type is looser (e.g. float parameters), the stub is kept at least as strict.
// Members marked "harness" do not exist in ATAS - the tests use them to drive the
// indicator. Never reference this project from the real indicator build.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

using OFT.Rendering.Context;

// ATAS's platform color ("CrossColor") is System.Windows.Media.Color on classic ATAS and
// System.Drawing.Color on ATAS X. -p:WpfColor=true uses the real WPF type, -p:CrossColor=true
// the ATAS X model; by default a stand-in WPF-like struct makes any missing .Convert() fail.
#if CROSS_COLOR
using CrossColor = System.Drawing.Color;
#else
using CrossColor = System.Windows.Media.Color;
#endif

#if !WPF_COLOR && !CROSS_COLOR
namespace System.Windows.Media
{
	public struct Color
	{
		public byte A;
		public byte R;
		public byte G;
		public byte B;

		public static Color FromArgb(byte a, byte r, byte g, byte b)
		{
			return new Color { A = a, R = r, G = g, B = b };
		}
	}
}
#endif

namespace OFT.Attributes
{
	[AttributeUsage(AttributeTargets.Property)]
	public sealed class ParameterAttribute : Attribute
	{
	}
}

namespace OFT.Rendering.Tools
{
	public class RenderPen
	{
		public RenderPen(System.Drawing.Color color, float width = 1)
		{
			Color = color;
			Width = width;
		}

		public System.Drawing.Color Color { get; set; }
		public float Width { get; set; }
		public DashStyle DashStyle { get; set; }
	}

	public class RenderFont
	{
		public RenderFont(string fontFamily, float size)
			: this(fontFamily, size, FontStyle.Regular)
		{
		}

		public RenderFont(string fontFamily, float size, FontStyle style)
		{
			FontFamily = fontFamily;
			Size = size;
			Style = style;
		}

		public string FontFamily { get; }
		public float Size { get; }
		public FontStyle Style { get; }
	}

	public class RenderStringFormat
	{
		public StringAlignment Alignment { get; set; }
		public StringAlignment LineAlignment { get; set; }
		public StringTrimming Trimming { get; set; }
	}
}

namespace OFT.Rendering.Settings
{
	using OFT.Rendering.Tools;

	public class FontSetting
	{
		public FontSetting()
			: this("Arial", 10)
		{
		}

		public FontSetting(string fontFamily, int size)
		{
			FontFamily = fontFamily;
			Size = size;
		}

		public string FontFamily { get; set; }
		public int Size { get; set; }
		public RenderFont RenderObject => new RenderFont(FontFamily, Size);
	}

	public class PenSettings
	{
		public CrossColor Color { get; set; } = CrossColor.FromArgb(255, 0, 0, 0);
		public int Width { get; set; } = 1;
		public RenderPen RenderObject => new RenderPen(ATAS.Indicators.ColorExtensions.Convert(Color), Width);
	}
}

namespace OFT.Rendering.Context
{
	using OFT.Rendering.Tools;

	// harness: one recorded draw call
	public class DrawOperation
	{
		public string Kind { get; set; }
		public System.Drawing.Color Color { get; set; }
		public Rectangle Bounds { get; set; }
		public Point From { get; set; }
		public Point To { get; set; }
		public float Width { get; set; }
		public bool Dashed { get; set; }
		public string Text { get; set; }
		public float FontSize { get; set; }
	}

	// harness: records what was drawn so tests can inspect it
	public class RenderContext
	{
		public List<string> Strings { get; } = new List<string>();
		public List<DrawOperation> Operations { get; } = new List<DrawOperation>();
		public int Rectangles { get; private set; }
		public int Lines { get; private set; }

		public void DrawString(string text, RenderFont font, System.Drawing.Color color, int x, int y)
		{
			Guard(text, font);
			Strings.Add(text);
			Operations.Add(new DrawOperation { Kind = "text", Color = color, From = new Point(x, y), Text = text, FontSize = font.Size });
		}

		public void DrawString(string text, RenderFont font, System.Drawing.Color color, Rectangle rectangle, RenderStringFormat format)
		{
			Guard(text, font);
			Strings.Add(text);
			Operations.Add(new DrawOperation { Kind = "text", Color = color, From = rectangle.Location, Text = text, FontSize = font.Size });
		}

		// roughly Arial at 96 dpi: ~0.55 em per character, 1 pt = 4/3 px
		public Size MeasureString(string text, RenderFont font)
		{
			Guard(text, font);
			return new Size((int)Math.Ceiling(text.Length * font.Size * 0.74), (int)Math.Ceiling(font.Size * 1.6));
		}

		public void FillRectangle(System.Drawing.Color color, Rectangle rectangle)
		{
			Rectangles++;
			Operations.Add(new DrawOperation { Kind = "fill", Color = color, Bounds = rectangle });
		}

		public void FillRectangle(System.Drawing.Color color, Rectangle rectangle, int cornerRadius)
		{
			FillRectangle(color, rectangle);
		}

		public void DrawRectangle(RenderPen pen, Rectangle rectangle)
		{
			if (pen == null)
				throw new ArgumentNullException(nameof(pen));

			Rectangles++;
			Operations.Add(new DrawOperation { Kind = "rect", Color = pen.Color, Bounds = rectangle, Width = pen.Width });
		}

		public void DrawLine(RenderPen pen, int x1, int y1, int x2, int y2)
		{
			if (pen == null)
				throw new ArgumentNullException(nameof(pen));

			Lines++;
			Operations.Add(new DrawOperation
			{
				Kind = "line", Color = pen.Color, From = new Point(x1, y1), To = new Point(x2, y2), Width = pen.Width,
				Dashed = pen.DashStyle != DashStyle.Solid
			});
		}

		public void FillPolygon(System.Drawing.Color color, Point[] points)
		{
		}

		private static void Guard(string text, RenderFont font)
		{
			if (text == null)
				throw new ArgumentNullException(nameof(text));

			if (font == null)
				throw new ArgumentNullException(nameof(font));
		}
	}
}

namespace ATAS.Indicators
{
	public enum VisualMode
	{
		Line,
		Histogram,
		Hash,
		Block,
		Cross,
		Square,
		Dots,
		UpArrow,
		DownArrow,
		Hide
	}

	[Flags]
	public enum DrawingLayouts
	{
		None = 0,
		Historical = 1,
		LatestBar = 2,
		Final = 4
	}

	public enum ChartVisualModes
	{
		Candles,
		Clusters,
		Line
	}

	public static class ColorExtensions
	{
#if CROSS_COLOR
		// ATAS X: the platform color already is System.Drawing.Color
		public static System.Drawing.Color Convert(this System.Drawing.Color color)
		{
			return color;
		}
#else
		public static CrossColor Convert(this System.Drawing.Color color)
		{
			return CrossColor.FromArgb(color.A, color.R, color.G, color.B);
		}

		public static System.Drawing.Color Convert(this CrossColor color)
		{
			return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
		}
#endif
	}

	public class PriceVolumeInfo
	{
		public decimal Price { get; set; }
		public decimal Volume { get; set; }
		public decimal Bid { get; set; }
		public decimal Ask { get; set; }
		public decimal Between { get; set; }
		public int Ticks { get; set; }
	}

	public class IndicatorCandle
	{
		public decimal Open { get; set; }
		public decimal High { get; set; }
		public decimal Low { get; set; }
		public decimal Close { get; set; }
		public decimal Volume { get; set; }
		public decimal Delta { get; set; }
		public decimal Ask { get; set; }
		public decimal Bid { get; set; }
		public DateTime Time { get; set; }
		public DateTime LastTime { get; set; }

		// harness: footprint of the candle
		public List<PriceVolumeInfo> Levels { get; set; } = new List<PriceVolumeInfo>();

		public IEnumerable<PriceVolumeInfo> GetAllPriceLevels()
		{
			return Levels;
		}
	}

	public interface IDataSeries
	{
		bool IsHidden { get; set; }
	}

	public class ValueDataSeries : IDataSeries
	{
		private readonly Dictionary<int, decimal> _values = new Dictionary<int, decimal>();

		public ValueDataSeries(string id, string name)
		{
			Id = id;
			Name = name;
		}

		public string Id { get; }
		public string Name { get; }
		public VisualMode VisualType { get; set; }
		public CrossColor Color { get; set; }
		public int Width { get; set; }
		public bool ShowZeroValue { get; set; } = true;
		public bool ShowCurrentValue { get; set; } = true;
		public bool IsHidden { get; set; }

		public decimal this[int bar]
		{
			get => _values.TryGetValue(bar, out var value) ? value : 0;
			set
			{
				if (bar < 0)
					throw new ArgumentOutOfRangeException(nameof(bar));

				_values[bar] = value;
			}
		}

		// harness
		public void Clear()
		{
			_values.Clear();
		}
	}

	public class InstrumentInfo
	{
		public decimal TickSize { get; set; } = 0.25m;
		public string Instrument { get; set; } = "NQ";
		public TimeSpan TimeZoneOffset { get; set; }
	}

	public class MouseLocationInfo
	{
		public Point LastPosition { get; set; }
	}

	public interface IChartContainer
	{
		Rectangle Region { get; }
		decimal BarsWidth { get; }
		decimal BarSpacing { get; }
		decimal PriceRowHeight { get; }
	}

	public interface IChart
	{
		ChartVisualModes ChartVisualMode { get; }
		IChartContainer PriceChartContainer { get; }
		Rectangle Region { get; }
		MouseLocationInfo MouseLocationInfo { get; }

		int GetXByBar(int bar, bool isStartOfBar = true);

		int GetYByPrice(decimal price, bool isStartOnPriceLevel = true);

		string GetPriceString(decimal price);
	}

	public abstract class Indicator
	{
		private readonly List<IDataSeries> _dataSeries = new List<IDataSeries>();

		protected Indicator(bool useCandles = false)
		{
			_dataSeries.Add(new ValueDataSeries("Default", "Default"));
		}

		public IList<IDataSeries> DataSeries => _dataSeries;
		public bool DenyToChangePanel { get; set; }
		public bool EnableCustomDrawing { get; set; }
		public IChart ChartInfo { get; set; }                         // harness setter
		public InstrumentInfo InstrumentInfo { get; set; }            // harness setter
		public int CurrentBar => Candles.Count;
		public int FirstVisibleBarNumber { get; set; }                // harness setter
		public int LastVisibleBarNumber { get; set; }                 // harness setter
		public MouseLocationInfo MouseLocationInfo => ChartInfo?.MouseLocationInfo;

		// harness
		public List<IndicatorCandle> Candles { get; } = new List<IndicatorCandle>();
		public HashSet<int> SessionStarts { get; } = new HashSet<int>();
		public List<string> Alerts { get; } = new List<string>();
		public DrawingLayouts SubscribedLayouts { get; private set; }
		public int RecalculateRequests { get; private set; }

		public IndicatorCandle GetCandle(int bar)
		{
			if (bar < 0 || bar >= Candles.Count)
				throw new ArgumentOutOfRangeException(nameof(bar), bar, "candle index out of range");

			return Candles[bar];
		}

		protected bool IsNewSession(int bar)
		{
			if (bar < 0 || bar >= Candles.Count)
				throw new ArgumentOutOfRangeException(nameof(bar), bar, "session check out of range");

			return SessionStarts.Contains(bar);
		}

		protected void SubscribeToDrawingEvents(DrawingLayouts layouts)
		{
			SubscribedLayouts = layouts;
		}

		public void RecalculateValues()
		{
			RecalculateRequests++;
		}

		public void RedrawChart()
		{
		}

		public void AddAlert(string soundFile, string instrument, string message,
			CrossColor backgroundColor, CrossColor foregroundColor)
		{
			Alerts.Add(message);
		}

		public void AddAlert(string soundFile, string message)
		{
			Alerts.Add(message);
		}

		protected abstract void OnCalculate(int bar, decimal value);

		protected virtual void OnRecalculate()
		{
		}

		protected virtual void OnRender(RenderContext context, DrawingLayouts layout)
		{
		}

		// harness entry points (ATAS calls these internally)
		public void HarnessRecalculate()
		{
			foreach (var series in _dataSeries)
				(series as ValueDataSeries)?.Clear();

			OnRecalculate();
		}

		public void HarnessCalculate(int bar)
		{
			OnCalculate(bar, Candles[bar].Close);
		}

		public void HarnessRender(RenderContext context)
		{
			OnRender(context, DrawingLayouts.Final);
		}
	}
}

using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VcxDiag.App.Controls;

/// <summary>A rolling buffer of samples for one graphed value.</summary>
public sealed class ChartSeries
{
    public string Name { get; }
    public Color Color { get; }
    private readonly double[] _buf;
    private int _start, _count;

    public ChartSeries(string name, Color color, int capacity)
    {
        Name = name;
        Color = color;
        _buf = new double[capacity];
    }

    public int Count => _count;
    public double this[int i] => _buf[(_start + i) % _buf.Length];
    public double? Last => _count == 0 ? null : this[_count - 1];

    public void Add(double v)
    {
        if (_count < _buf.Length) _buf[(_start + _count++) % _buf.Length] = v;
        else { _buf[_start] = v; _start = (_start + 1) % _buf.Length; }
    }

    public void Clear() { _start = 0; _count = 0; }
}

/// <summary>
/// Lightweight strip chart. Each series is scaled to its own range (values have different
/// units), and the legend shows the current value with that range.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(LineChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Series { get => (IEnumerable?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    private static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var bg = TryFindResource("ChartBackground") as Brush ?? Brushes.White;
        var gridPen = new Pen(TryFindResource("ChartGrid") as Brush ?? Brushes.LightGray, 1);
        var textBrush = TryFindResource("TextMuted") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        if (w < 40 || h < 40) return;

        var list = Series?.OfType<ChartSeries>().ToList() ?? new List<ChartSeries>();
        double legendH = Math.Max(1, list.Count) * 18 + 8;
        var plot = new Rect(8, legendH, w - 16, Math.Max(10, h - legendH - 8));
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        if (list.Count == 0)
        {
            Text(dc, "Tick \"Graph\" next to a value to plot it here.", new Point(12, h / 2 - 8), textBrush, 13);
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int idx = 0;
        foreach (var s in list)
        {
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < s.Count; i++) { var v = s[i]; if (double.IsNaN(v)) continue; min = Math.Min(min, v); max = Math.Max(max, v); }
            if (min > max) { min = 0; max = 1; }
            if (max - min < 1e-9) { max += 1; min -= 1; }
            double pad = (max - min) * 0.08;
            min -= pad; max += pad;

            if (s.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    bool started = false;
                    int cap = Math.Max(s.Count, 2);
                    for (int i = 0; i < s.Count; i++)
                    {
                        var v = s[i];
                        if (double.IsNaN(v)) { started = false; continue; }
                        var p = new Point(plot.Left + plot.Width * i / (cap - 1), plot.Bottom - (v - min) / (max - min) * plot.Height);
                        if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                        else ctx.LineTo(p, true, false);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(brush, 2), geo);
            }

            double ly = 6 + idx * 18;
            dc.DrawRectangle(brush, null, new Rect(10, ly + 4, 12, 4));
            var last = s.Last is double lv ? lv.ToString("0.##", CultureInfo.CurrentCulture) : "—";
            Text(dc, $"{s.Name}: {last}   (range {min + pad:0.##} … {max - pad:0.##})", new Point(28, ly - 2), textBrush, 12, dpi);
            idx++;
        }
    }

    private static void Text(DrawingContext dc, string s, Point at, Brush brush, double size, double dpi = 1.0) =>
        dc.DrawText(new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, dpi), at);
}

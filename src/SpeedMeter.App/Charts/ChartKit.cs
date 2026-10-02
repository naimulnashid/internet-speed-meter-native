using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SpeedMeter.App.Theme;
using Windows.Foundation;

namespace SpeedMeter.App.Charts;

/// <summary>Axis maths and drawing helpers shared by every chart.</summary>
public static class ChartKit
{
    /// <summary>
    /// "Nice" ticks from zero to at least <paramref name="max"/>: Recharts'
    /// algorithm (getNiceTickValues with five ticks), so the axes land on the
    /// same round numbers the original drew - 0 B / 7 GB / 15 GB / 22 GB.
    /// </summary>
    public static double[] NiceTicks(double max, int tickCount = 5)
    {
        if (!(max > 0) || !double.IsFinite(max)) return Enumerable.Range(0, tickCount).Select(i => (double)i).ToArray();
        for (var correction = 0; correction < 20; correction++)
        {
            var step = FormatStep(max / (tickCount - 1), correction);
            var middle = max / 2;
            middle -= Mod(middle, step);
            var below = (int)Math.Ceiling(middle / step - 1e-9);
            var up = (int)Math.Ceiling((max - middle) / step - 1e-9);
            var count = below + up + 1;
            if (count > tickCount) continue;
            if (count < tickCount) up += tickCount - count;
            var min = middle - below * step;
            return Enumerable.Range(0, tickCount).Select(i => Math.Round((min + i * step) / step) * step).ToArray();
        }
        return [0, max / 4, max / 2, max * 3 / 4, max];
    }

    private static double Mod(double a, double b) => a - Math.Floor(a / b) * b;

    private static double FormatStep(double rough, int correction)
    {
        if (rough <= 0) return 1;
        var digits = (int)Math.Floor(Math.Log10(Math.Abs(rough))) + 1;
        var digitValue = Math.Pow(10, digits);
        var ratio = rough / digitValue;
        var scale = digits != 1 ? 0.05 : 0.1;
        var amended = (Math.Ceiling(ratio / scale - 1e-12) + correction) * scale;
        return amended * digitValue;
    }

    /// <summary>The rendered width of a label, for thinning an axis.</summary>
    public static double MeasureText(string text, double size = 13)
    {
        var block = Ui.Text(text, size);
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }

    /// <summary>
    /// Which category labels to draw so none overlap, keeping the LAST one
    /// (Recharts' preserveEnd): walk from the end and keep a label only when it
    /// clears the previous kept one by <paramref name="gap"/>.
    /// </summary>
    public static HashSet<int> ThinLabels(IReadOnlyList<double> centres, IReadOnlyList<double> widths, double gap, double left, double right)
    {
        var keep = new HashSet<int>();
        var boundary = double.PositiveInfinity;
        for (var i = centres.Count - 1; i >= 0; i--)
        {
            var centre = ClampCentre(centres[i], widths[i], left, right);
            var start = centre - widths[i] / 2;
            var end = centre + widths[i] / 2;
            if (end + gap <= boundary)
            {
                keep.Add(i);
                boundary = start;
            }
        }
        return keep;
    }

    /// <summary>
    /// A label's centre, nudged inward so it stays inside the chart. An edge
    /// label is moved, not dropped: the last day is the one worth naming.
    /// </summary>
    public static double ClampCentre(double centre, double width, double left, double right) =>
        Math.Clamp(centre, left + width / 2, Math.Max(left + width / 2, right - width / 2));

    /// <summary>
    /// d3's curveMonotoneX: a smooth line that never overshoots the data, which
    /// is what "type=monotone" drew in the original.
    /// </summary>
    public static PathFigure MonotoneFigure(IReadOnlyList<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = true };
        var n = points.Count;
        if (n < 2) return figure;
        if (n == 2)
        {
            figure.Segments.Add(new LineSegment { Point = points[1] });
            return figure;
        }

        static double Sign(double x) => x < 0 ? -1 : 1;
        var tangents = new double[n];
        for (var i = 1; i < n - 1; i++)
        {
            var h0 = points[i].X - points[i - 1].X;
            var h1 = points[i + 1].X - points[i].X;
            var s0 = h0 != 0 ? (points[i].Y - points[i - 1].Y) / h0 : 0;
            var s1 = h1 != 0 ? (points[i + 1].Y - points[i].Y) / h1 : 0;
            var p = (s0 * h1 + s1 * h0) / (h0 + h1);
            var t = (Sign(s0) + Sign(s1)) * Math.Min(Math.Min(Math.Abs(s0), Math.Abs(s1)), 0.5 * Math.Abs(p));
            tangents[i] = double.IsFinite(t) ? t : 0;
        }
        static double Slope2(Point a, Point b, double t)
        {
            var h = b.X - a.X;
            return h != 0 ? (3 * (b.Y - a.Y) / h - t) / 2 : t;
        }
        tangents[0] = Slope2(points[0], points[1], tangents[1]);
        tangents[n - 1] = Slope2(points[n - 2], points[n - 1], tangents[n - 2]);

        for (var i = 0; i < n - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var dx = (b.X - a.X) / 3;
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(a.X + dx, a.Y + dx * tangents[i]),
                Point2 = new Point(b.X - dx, b.Y - dx * tangents[i + 1]),
                Point3 = b,
            });
        }
        return figure;
    }

    public static Line HLine(double x1, double x2, double y, Brush brush, double thickness = 1) => new()
    {
        X1 = x1, X2 = x2, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = thickness,
    };

    public static Line VLine(double x, double y1, double y2, Brush brush, double thickness = 1) => new()
    {
        X1 = x, X2 = x, Y1 = y1, Y2 = y2, Stroke = brush, StrokeThickness = thickness,
    };

    /// <summary>Places a label with its anchor at (x, y): align -1 left, 0 centre, 1 right edge.</summary>
    public static TextBlock Label(Canvas canvas, string text, double x, double y, int align, double size = 13, Brush? brush = null)
    {
        // Axis labels sit on the hover surface: not selectable.
        var block = Ui.Text(text, size, 400, brush ?? Palette.TextFaintBrush, numeric: true, selectable: false);
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = block.DesiredSize.Width;
        var h = block.DesiredSize.Height;
        Canvas.SetLeft(block, align switch { < 0 => x, 0 => x - w / 2, _ => x - w });
        Canvas.SetTop(block, y - h / 2);
        canvas.Children.Add(block);
        return block;
    }
}

/// <summary>
/// The hover tooltip every chart shares: a card that follows the pointer.
/// </summary>
/// <remarks>
/// A <see cref="Popup"/> rather than an element inside the chart: the charts
/// sit in a scrolling page, and anything drawn inside the scroller is clipped
/// at its edge. A popup is drawn above the whole window, and is flipped to the
/// other side of the pointer when it would run off the edge.
/// </remarks>
public sealed class ChartTooltip
{
    private readonly FrameworkElement _host;
    private readonly Popup _popup = new() { IsHitTestVisible = false, ShouldConstrainToRootBounds = true };
    private readonly Border _card;

    public ChartTooltip(FrameworkElement host, bool warnBorder = false)
    {
        _host = host;
        _card = new Border
        {
            Background = Palette.TooltipBgBrush,
            BorderBrush = warnBorder ? Palette.WarnBrush : Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 12, 15, 12),
        };
        _popup.Child = _card;
        host.Unloaded += (_, _) => Hide();
    }

    public void SetWarn(bool warn) => _card.BorderBrush = warn ? Palette.WarnBrush : Palette.BorderBrightBrush;

    /// <summary>Shows <paramref name="content"/> near <paramref name="pointInHost"/>.</summary>
    public void Show(UIElement content, Point pointInHost)
    {
        if (_host.XamlRoot is null) return;
        _card.Child = content;
        _popup.XamlRoot = _host.XamlRoot;
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // A popup sits outside the zoomed page, so it scales itself to match.
        var zoom = Zoom.Level;
        _card.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = zoom, ScaleY = zoom };
        var size = new Size(_card.DesiredSize.Width * zoom, _card.DesiredSize.Height * zoom);
        var window = _host.XamlRoot.Size;
        var at = _host.TransformToVisual(null).TransformPoint(pointInHost);

        var x = at.X + 16;
        var y = at.Y + 16;
        if (x + size.Width > window.Width - 8) x = at.X - size.Width - 16;
        if (y + size.Height > window.Height - 8) y = at.Y - size.Height - 16;
        _popup.HorizontalOffset = Math.Max(8, x);
        _popup.VerticalOffset = Math.Max(8, y);
        _popup.IsOpen = true;
    }

    public void Hide() => _popup.IsOpen = false;

    /* Content builders, in the tooltip's own type scale. */

    public static StackPanel Stack() => new() { MinWidth = 180 };

    public static TextBlock Title(string text) =>
        WithMargin(Ui.Text(text, 14, 600, Palette.TextBrush), 0, 0, 0, 8);

    public static TextBlock Big(string text, Brush brush) =>
        Ui.Text(text, 21, 650, brush, numeric: true);

    public static TextBlock Muted(string text, double top = 6, double size = 14) =>
        WithMargin(Ui.Text(text, size, 400, Palette.TextMutedBrush, numeric: true), 0, top, 0, 0);

    /// <summary>A "swatch  name ........ value" row.</summary>
    public static Grid Row(Windows.UI.Color swatch, string name, string value, Brush? valueBrush = null, int valueWeight = 550)
    {
        var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(Ui.Swatch(swatch));
        left.Children.Add(Ui.Text(name, 14, 400, Palette.TextMutedBrush));
        row.Children.Add(left);
        var right = Ui.Text(value, 14, valueWeight, valueBrush ?? Palette.TextBrush, numeric: true);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    private static TextBlock WithMargin(TextBlock block, double l, double t, double r, double b)
    {
        block.Margin = new Thickness(l, t, r, b);
        return block;
    }
}

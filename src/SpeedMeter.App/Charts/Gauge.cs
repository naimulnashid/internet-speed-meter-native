using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.View;
using Windows.Foundation;

namespace SpeedMeter.App.Charts;

/// <summary>
/// The speedometer for the live rate during a test.
/// </summary>
/// <remarks>
/// <para>The scale is LOGARITHMIC, the whole design decision. A domestic
/// connection can be anything from a few hundred Kbps to a gigabit, and on a
/// linear dial sized for the fast case every ordinary speed sits pinned at the
/// stop. Four decades of arc give 10 and 100 Mbps visibly different needle
/// positions. The labelled decade ticks, and the minor ticks at 2x and 5x, are
/// what make a log dial legible rather than misleading: not decoration.</para>
/// <para>Present from the start, sitting at zero: an instrument waiting, which
/// shows the scale a reading will land on before there is one.</para>
/// </remarks>
public sealed class Gauge : Grid
{
    /// <summary>10 KB/s to 100 MB/s: about 80 Kbps to 800 Mbps.</summary>
    private const double MinBps = 1e4, MaxBps = 1e8;

    /// <summary>Opens at the bottom: starts bottom-left, sweeps over the top, ends bottom-right.</summary>
    private const double StartAngle = 135, Sweep = 270;

    private const double Cx = 120, Cy = 120, R = 92;

    private readonly Canvas _canvas = new() { Width = 264, Height = 222 };
    private readonly UnitMode _units;
    private readonly Microsoft.UI.Xaml.Shapes.Path _value = new() { StrokeThickness = 14, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Line _needle = new() { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, X1 = Cx + 12, Y1 = Cy + 10 };
    private readonly Ellipse _hub = new() { Width = 14, Height = 14, StrokeThickness = 2.5 };
    private readonly TextBlock _number = Ui.Text("0", 30, 650, Palette.TextBrush, numeric: true, selectable: false);
    private readonly TextBlock _unit = Ui.Text("", 12, 600, Palette.TextMutedBrush, selectable: false);
    private double _target;
    private double _shown;
    private bool _up;
    private bool _animating;
    private readonly Stopwatch _clock = new();
    private double _lastFrame;

    public Gauge(UnitMode units)
    {
        _units = units;
        MaxWidth = 340;
        HorizontalAlignment = HorizontalAlignment.Center;
        var box = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform };
        Children.Add(box);
        // The viewBox is sized around the tick LABELS, not the dial: they sit
        // outside it at radius 118 and overhang every side.
        var layer = new Canvas { RenderTransform = new TranslateTransform { X = 12, Y = 10 } };
        _canvas.Children.Add(layer);

        // A visible groove: on a near-black card the dial needs a body, or the
        // needle and the arc are marks floating in a box.
        layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Arc(1), Stroke = Palette.BorderBrightBrush, StrokeThickness = 14, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Arc(1), Stroke = Palette.TextFaintBrush, StrokeThickness = 1, Opacity = 0.35 });

        foreach (var decade in new[] { 1e4, 1e5, 1e6, 1e7 })
        {
            foreach (var minor in new[] { decade * 2, decade * 5 })
            {
                var a = StartAngle + Sweep * Fraction(minor);
                var (ix, iy) = PointAt(a, R - 6);
                var (ox, oy) = PointAt(a, R + 6);
                layer.Children.Add(new Line { X1 = ix, Y1 = iy, X2 = ox, Y2 = oy, Stroke = Palette.TextFaintBrush, StrokeThickness = 1, Opacity = 0.55 });
            }
        }

        layer.Children.Add(_value);

        foreach (var tick in new[] { 1e4, 1e5, 1e6, 1e7, 1e8 })
        {
            var a = StartAngle + Sweep * Fraction(tick);
            var (ix, iy) = PointAt(a, R - 12);
            var (ox, oy) = PointAt(a, R + 12);
            var (lx, ly) = PointAt(a, R + 26);
            layer.Children.Add(new Line { X1 = ix, Y1 = iy, X2 = ox, Y2 = oy, Stroke = Palette.TextFaintBrush, StrokeThickness = 1.5 });
            ChartKit.Label(layer, Format.RateTick(tick, units), lx, ly, 0, 9);
        }

        // The needle and hub are drawn in the layer's own coordinates.
        _needle.X1 = Cx;
        _needle.Y1 = Cy;
        layer.Children.Add(_needle);
        Canvas.SetLeft(_hub, Cx - 7);
        Canvas.SetTop(_hub, Cy - 7);
        _hub.Fill = Palette.SurfaceBrush;
        layer.Children.Add(_hub);

        Center(layer, _number, Cy + 40);
        Center(layer, _unit, Cy + 62);
        Paint();
        Unloaded += (_, _) => StopAnimating();
    }

    /// <summary>The live rate. Upload swaps the colour; the needle eases rather than jumping.</summary>
    public void Set(double bytesPerSecond, bool up)
    {
        _target = Math.Max(0, bytesPerSecond);
        if (_up != up)
        {
            _up = up;
            Paint();
        }
        if (!Motion.Enabled || (_target == 0 && _shown == 0))
        {
            _shown = _target;
            Paint();
            return;
        }
        if (_animating) return;
        _animating = true;
        _clock.Restart();
        _lastFrame = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var dt = Math.Max(1, now - _lastFrame);
        _lastFrame = now;
        // Ease in log space, so a jump from 1 to 100 Mbps sweeps as evenly as one from 10 to 1000 Kbps.
        var from = Fraction(_shown);
        var to = Fraction(_target);
        var k = 1 - Math.Exp(-dt / 70);
        var next = from + (to - from) * k;
        _shown = Math.Abs(to - next) < 0.002 ? _target : FromFraction(next);
        if (_target == 0 && next < 0.003) _shown = 0;
        Paint();
        if (_shown == _target) StopAnimating();
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void Paint()
    {
        // A skeleton's dial stays the neutral shape it was turned into.
        if (Views.Skeleton.Contains(this)) return;
        var brush = _up ? Palette.UpBrush : Palette.DownBrush;
        var f = Fraction(_shown);
        // Omitted at rest rather than drawn as a zero-length arc, which a round
        // cap turns into a dot at the start of an idle dial.
        _value.Stroke = brush;
        _value.Data = _shown > 0 ? Arc(Math.Max(f, 0.004)) : null;
        var (nx, ny) = PointAt(StartAngle + Sweep * f, R - 20);
        _needle.X2 = nx;
        _needle.Y2 = ny;
        _needle.Stroke = brush;
        _hub.Stroke = brush;
        // At rest a dial reads zero, not "0.00 bps".
        var (value, unit) = _shown > 0 ? Format.SplitRate(_shown, _units) : ("0", _units == UnitMode.Bits ? "bps" : "B/s");
        _number.Text = value;
        _unit.Text = unit;
        Recenter(_number);
        Recenter(_unit);
    }

    private static double Fraction(double bps) =>
        !(bps > 0) ? 0 : Math.Clamp((Math.Log10(bps) - Math.Log10(MinBps)) / (Math.Log10(MaxBps) - Math.Log10(MinBps)), 0, 1);

    private static double FromFraction(double f) => f <= 0 ? 0 : Math.Pow(10, Math.Log10(MinBps) + f * (Math.Log10(MaxBps) - Math.Log10(MinBps)));

    private static (double X, double Y) PointAt(double angleDeg, double radius)
    {
        var rad = angleDeg * Math.PI / 180;
        return (Cx + radius * Math.Cos(rad), Cy + radius * Math.Sin(rad));
    }

    private static Geometry Arc(double f)
    {
        var end = StartAngle + Sweep * f;
        var (x0, y0) = PointAt(StartAngle, R);
        var (x1, y1) = PointAt(end, R);
        var figure = new PathFigure { StartPoint = new Point(x0, y0), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x1, y1),
            Size = new Size(R, R),
            IsLargeArc = Sweep * f > 180,
            SweepDirection = SweepDirection.Clockwise,
        });
        return new PathGeometry { Figures = { figure } };
    }

    private static void Center(Canvas layer, TextBlock text, double y)
    {
        text.Tag = y;
        layer.Children.Add(text);
        Recenter(text);
    }

    private static void Recenter(TextBlock text)
    {
        if (text.Tag is not double y) return;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(text, Cx - text.DesiredSize.Width / 2);
        Canvas.SetTop(text, y - text.DesiredSize.Height / 2);
    }
}

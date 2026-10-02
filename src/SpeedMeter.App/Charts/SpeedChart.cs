using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.View;
using Windows.Foundation;

namespace SpeedMeter.App.Charts;

/// <summary>One sample of a leg: seconds since it started, and the rate.</summary>
public readonly record struct SpeedPoint(double T, double Bps);

/// <summary>
/// The rate through one leg of a test, as it ran. The headline is one number,
/// and one number hides the shape - a slow ramp, a plateau that sagged, a link
/// that stalled and recovered. This is the shape. It fills live while the leg
/// runs and stays once the result is in, with the headline average drawn
/// across it so the two read against each other.
/// </summary>
/// <remarks>Always its full height, empty or not, so the card does not grow when the first sample lands.</remarks>
public sealed class SpeedChart : ChartSurface
{
    private const double Top = 8, Right = 4, Bottom = 4, Left = 0, YAxisWidth = 86, XAxisHeight = 24;

    private readonly bool _up;
    private readonly UnitMode _units;
    private IReadOnlyList<SpeedPoint> _points = [];
    private double? _average;
    private Line? _cursor;
    private Ellipse? _dot;

    public SpeedChart(bool up, UnitMode units, double height = 130) : base(height, animate: false)
    {
        _up = up;
        _units = units;
    }

    /// <summary>New samples (or the finished leg's average): redraws in place.</summary>
    public void Set(IReadOnlyList<SpeedPoint> points, double? average)
    {
        _points = points;
        _average = average;
        Refresh();
    }

    protected override void Draw(bool animate)
    {
        _cursor = null;
        _dot = null;
        if (_points.Count < 2) return;

        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var plotBottom = H - Bottom - XAxisHeight;
        var max = Math.Max(_points.Max(p => p.Bps), _average ?? 0);
        var top = Axes.RateGrid(Canvas, max, _units, plotLeft, plotRight, Top, plotBottom, tickCount: 3);
        var span = Math.Max(1, _points[^1].T);
        double X(double t) => plotLeft + t / span * (plotRight - plotLeft);
        double Y(double v) => plotBottom - (top > 0 ? v / top : 0) * (plotBottom - Top);

        // Second marks along the bottom, about every five seconds.
        var step = span > 30 ? 10 : span > 12 ? 5 : 2;
        for (var s = 0.0; s <= span + 0.01; s += step) ChartKit.Label(Canvas, $"{Math.Round(s)}s", X(s), plotBottom + 13, 0);

        var colour = _up ? Theme.Palette.Up : Theme.Palette.Down;
        var points = _points.Select(p => new Point(X(p.T), Y(p.Bps))).ToList();
        var fill = ChartKit.MonotoneFigure(points);
        fill.Segments.Add(new LineSegment { Point = new Point(points[^1].X, plotBottom) });
        fill.Segments.Add(new LineSegment { Point = new Point(points[0].X, plotBottom) });
        fill.IsClosed = true;
        Canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { fill } },
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Theme.Palette.WithAlpha(colour, 0.28), Offset = 0 },
                    new GradientStop { Color = Theme.Palette.WithAlpha(colour, 0.02), Offset = 1 },
                },
            },
        });
        Canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { ChartKit.MonotoneFigure(points) } },
            Stroke = _up ? Theme.Palette.UpBrush : Theme.Palette.DownBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        if (_average is { } average && average > 0)
        {
            var line = ChartKit.HLine(plotLeft, plotRight, Y(average), Theme.Palette.TextMutedBrush);
            line.StrokeDashArray = [4, 4];
            Canvas.Children.Add(line);
        }

        _cursor = new Line { Stroke = Theme.Palette.TextFaintBrush, StrokeThickness = 1, StrokeDashArray = [3, 3], Y1 = Top, Y2 = plotBottom, Visibility = Visibility.Collapsed };
        _dot = new Ellipse { Width = 8, Height = 8, Fill = _up ? Theme.Palette.UpBrush : Theme.Palette.DownBrush, Stroke = Theme.Palette.SurfaceBrush, StrokeThickness = 2, Visibility = Visibility.Collapsed };
        Canvas.Children.Add(_cursor);
        Canvas.Children.Add(_dot);
    }

    protected override void OnHover(Point at)
    {
        if (_points.Count < 2 || _cursor is null || _dot is null) return;
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        var span = Math.Max(1, _points[^1].T);
        var t = Math.Clamp((at.X - plotLeft) / Math.Max(1, plotRight - plotLeft) * span, 0, span);
        var nearest = _points.MinBy(p => Math.Abs(p.T - t));
        var x = plotLeft + nearest.T / span * (plotRight - plotLeft);
        var plotBottom = H - Bottom - XAxisHeight;
        var max = Math.Max(_points.Max(p => p.Bps), _average ?? 0);
        var factor = _units == UnitMode.Bits ? 8.0 : 1.0;
        var top = ChartKit.NiceTicks(max * factor, 3)[^1] / factor;
        var y = plotBottom - (top > 0 ? nearest.Bps / top : 0) * (plotBottom - Top);
        _cursor.X1 = _cursor.X2 = x;
        _cursor.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_dot, x - 4);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_dot, y - 4);
        _dot.Visibility = Visibility.Visible;

        var body = ChartTooltip.Stack();
        body.MinWidth = 140;
        body.Children.Add(ChartTooltip.Title($"{nearest.T:0.0}s"));
        body.Children.Add(ChartTooltip.Row(_up ? Theme.Palette.Up : Theme.Palette.Down, _up ? "Upload" : "Download", Format.Rate(nearest.Bps, _units)));
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_cursor is not null) _cursor.Visibility = Visibility.Collapsed;
        if (_dot is not null) _dot.Visibility = Visibility.Collapsed;
    }
}

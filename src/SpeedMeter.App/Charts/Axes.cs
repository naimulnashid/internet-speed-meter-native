using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.View;
using Windows.Foundation;

namespace SpeedMeter.App.Charts;

/// <summary>The axis furniture the cartesian charts share.</summary>
public static class Axes
{
    /// <summary>
    /// Horizontal grid lines and their labels for a RATE axis. The nice ticks
    /// are taken in the unit shown - bits or bytes - so a bits axis reads
    /// 0 / 50 / 100 / 150 Mbps rather than the bit value of round bytes.
    /// Returns the rate (bytes/s) mapped to the top of the plot.
    /// </summary>
    public static double RateGrid(Canvas canvas, double maxBytesPerSecond, UnitMode units, double plotLeft, double plotRight, double plotTop, double plotBottom, int tickCount = 5)
    {
        var factor = units == UnitMode.Bits ? 8.0 : 1.0;
        var ticks = ChartKit.NiceTicks(maxBytesPerSecond * factor, tickCount).Select(t => t / factor).ToArray();
        var top = ticks[^1];
        foreach (var tick in ticks)
        {
            var y = plotBottom - (top > 0 ? tick / top : 0) * (plotBottom - plotTop);
            canvas.Children.Add(ChartKit.HLine(plotLeft, plotRight, y, Palette.GridBrush));
            ChartKit.Label(canvas, Format.RateTick(tick, units), plotLeft - 8, y, 1);
        }
        return top;
    }

    /// <summary>Category labels under an axis, thinned so none overlap, the last kept.</summary>
    public static void CategoryLabels(Canvas canvas, IReadOnlyList<string> labels, IReadOnlyList<double> centres, double y, double width, double gap = 28)
    {
        var widths = labels.Select(l => ChartKit.MeasureText(l)).ToList();
        var keep = ChartKit.ThinLabels(centres, widths, gap, 0, width);
        for (var i = 0; i < labels.Count; i++)
            if (keep.Contains(i)) ChartKit.Label(canvas, labels[i], ChartKit.ClampCentre(centres[i], widths[i], 0, width), y, 0);
    }

    /// <summary>Grows a layer up from the baseline.</summary>
    public static void GrowUp(Microsoft.UI.Xaml.UIElement layer, double baseline, int ms = 800)
    {
        var scale = new ScaleTransform { ScaleY = 0, CenterY = baseline };
        layer.RenderTransform = scale;
        var story = new Storyboard();
        var grow = new DoubleAnimation { From = 0, To = 1, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(grow, scale);
        Storyboard.SetTargetProperty(grow, "ScaleY");
        story.Children.Add(grow);
        story.Begin();
    }

    /// <summary>A rectangle with only its top corners rounded.</summary>
    public static Geometry RoundedTop(Rect r, double radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width / 2, r.Height)));
        var figure = new PathFigure { StartPoint = new Point(r.Left, r.Bottom), IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = new Point(r.Left, r.Top + radius) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Left + radius, r.Top), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right - radius, r.Top) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r.Right, r.Top + radius), Size = new Size(radius, radius), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r.Right, r.Bottom) });
        return new PathGeometry { Figures = { figure } };
    }
}

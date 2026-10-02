using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace SpeedMeter.App.Charts;

/// <summary>
/// The base every chart builds on: a fixed-height canvas that redraws itself
/// to its width, plus the hover plumbing.
/// </summary>
/// <remarks>
/// Charts are drawn with plain XAML shapes rather than a chart library, so the
/// details that carry meaning here - a day never collected drawn as a gap and
/// not a zero, upload as a tint of its app's own colour, the busiest hour at
/// full strength - are ours to get exactly right.
/// </remarks>
public abstract class ChartSurface : Grid
{
    protected readonly Canvas Canvas = new() { Background = SpeedMeter.App.Theme.Palette.TransparentBrush };
    protected readonly ChartTooltip Tooltip;
    private double _lastWidth = -1;
    private bool _animateNext;

    protected ChartSurface(double height, bool animate)
    {
        Height = height;
        _animateNext = animate && SpeedMeter.App.Theme.Motion.Enabled;
        Children.Add(Canvas);
        Tooltip = new ChartTooltip(this);
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _lastWidth) < 0.5) return;
            _lastWidth = e.NewSize.Width;
            Redraw();
        };
        Canvas.PointerMoved += (_, e) => OnHover(e.GetCurrentPoint(Canvas).Position);
        Canvas.PointerExited += (_, _) => ClearHover();
        Canvas.PointerCanceled += (_, _) => ClearHover();
    }

    protected double W => ActualWidth;
    protected double H => Height;

    /// <summary>Redraws in place at the current size: for a chart whose data changes while it is shown.</summary>
    public void Refresh() => Redraw();

    private void Redraw()
    {
        Canvas.Children.Clear();
        if (W <= 0) return;
        Draw(_animateNext);
        // Animate the first draw only; a resize just redraws in place.
        _animateNext = false;
    }

    /// <summary>Rebuilds every shape for the current size.</summary>
    protected abstract void Draw(bool animate);

    protected virtual void OnHover(Point at) { }

    protected virtual void ClearHover() => Tooltip.Hide();
}

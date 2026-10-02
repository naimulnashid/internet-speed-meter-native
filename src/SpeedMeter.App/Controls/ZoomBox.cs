using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace SpeedMeter.App.Controls;

/// <summary>
/// Page zoom the way a browser does it: the content is laid out as if the
/// window were <c>1 / zoom</c> as wide, then scaled up to fill it. So zooming
/// in reflows the page - cards wrap, charts narrow - rather than magnifying it
/// into a sideways scroll, which is what a plain scale (or a ScrollViewer's
/// pinch zoom) would do.
/// </summary>
/// <remarks>
/// WinUI has no LayoutTransform, so this panel is one: it measures and
/// arranges its child in unscaled units and applies the scale as a
/// RenderTransform. Hit testing and <c>TransformToVisual</c> both honour a
/// RenderTransform, so pointer positions and chart tooltips land where they
/// should. Popups (flyouts, menus, drop-downs) live outside the tree and are
/// not scaled; the chart tooltips scale themselves (see ChartTooltip).
/// </remarks>
public sealed class ZoomBox : Panel
{
    private readonly ScaleTransform _scale = new();
    private double _zoom = 1;

    public ZoomBox(UIElement child)
    {
        child.RenderTransform = _scale;
        Children.Add(child);
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (value <= 0 || value == _zoom) return;
            _zoom = value;
            _scale.ScaleX = _scale.ScaleY = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var child = Children[0];
        child.Measure(new Size(available.Width / _zoom, available.Height / _zoom));
        return new Size(
            Math.Min(available.Width, child.DesiredSize.Width * _zoom),
            Math.Min(available.Height, child.DesiredSize.Height * _zoom));
    }

    protected override Size ArrangeOverride(Size final)
    {
        Children[0].Arrange(new Rect(0, 0, final.Width / _zoom, final.Height / _zoom));
        return final;
    }
}

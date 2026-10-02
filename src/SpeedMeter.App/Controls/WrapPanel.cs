using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SpeedMeter.App.Controls;

/// <summary>
/// Flow layout: WinUI has no wrap panel, and a row of chips that runs off the
/// card is worse than one that wraps. Each line is centred, or left-aligned
/// when the panel itself is.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size available)
    {
        double lineW = 0, lineH = 0, width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (lineW > 0 && lineW + HorizontalSpacing + d.Width > available.Width)
            {
                width = Math.Max(width, lineW);
                height += lineH + VerticalSpacing;
                lineW = lineH = 0;
            }
            lineW += (lineW > 0 ? HorizontalSpacing : 0) + d.Width;
            lineH = Math.Max(lineH, d.Height);
        }
        width = Math.Max(width, lineW);
        height += lineH;
        return new Size(double.IsFinite(available.Width) ? available.Width : width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var lines = new List<List<UIElement>>();
        var current = new List<UIElement>();
        double lineW = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            if (current.Count > 0 && lineW + HorizontalSpacing + w > final.Width)
            {
                lines.Add(current);
                current = [];
                lineW = 0;
            }
            lineW += (current.Count > 0 ? HorizontalSpacing : 0) + w;
            current.Add(child);
        }
        if (current.Count > 0) lines.Add(current);

        double y = 0;
        foreach (var line in lines)
        {
            var total = line.Sum(c => c.DesiredSize.Width) + HorizontalSpacing * (line.Count - 1);
            var x = HorizontalAlignment == HorizontalAlignment.Left ? 0 : Math.Max(0, (final.Width - total) / 2);
            var h = line.Max(c => c.DesiredSize.Height);
            foreach (var child in line)
            {
                child.Arrange(new Rect(x, y + (h - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + HorizontalSpacing;
            }
            y += h + VerticalSpacing;
        }
        return final;
    }
}

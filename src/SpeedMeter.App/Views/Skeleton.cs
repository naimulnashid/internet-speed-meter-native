using SpeedMeter.App.Charts;
using SpeedMeter.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace SpeedMeter.App.Views;

/// <summary>
/// Loading skeletons: a page built by its OWN <c>Build()</c> from stand-in
/// data (<see cref="Placeholder"/>), then turned into shapes - so nothing moves
/// when the real data lands, because every height came from the real layout.
/// </summary>
/// <remarks>
/// <para>The web dashboard's skeletons do the same with the page's own markup,
/// and measured to the pixel against the real pages. Here the page code itself
/// is reused, which is stronger: a skeleton cannot drift from a page it is.</para>
/// <para>What the conversion does: text keeps its layout but is painted as a
/// bar behind each line (a <see cref="TextHighlighter"/>, the XAML equivalent
/// of the web's inline shimmer); a chart becomes a plain block of its own
/// height; swatches, cells and bars go neutral; logos never load; nothing is
/// interactive; and the whole page pulses gently.</para>
/// </remarks>
public static class Skeleton
{
    /// <summary>Marks a skeleton's root, so controls inside can tell (see <see cref="Contains"/>).</summary>
    private const string Marker = "skeleton";

    /// <summary>Turns a built page into its skeleton, in place.</summary>
    public static UIElement Apply(FrameworkElement root)
    {
        root.Tag = Marker;
        root.IsHitTestVisible = false;
        Walk(root);
        Pulse(root);
        return root;
    }

    /// <summary>
    /// Whether an element sits inside a skeleton. A logo that would load, or a
    /// count-up that would animate, asks this first.
    /// </summary>
    public static bool Contains(DependencyObject element)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (e is FrameworkElement { Tag: Marker }) return true;
        return false;
    }

    private static void Walk(object? node)
    {
        switch (node)
        {
            case ChartSurface chart:
                // A chart is a block of its own height: its shapes are data.
                chart.Background = Palette.SkeletonBrush;
                chart.CornerRadius = new CornerRadius(Ui.RadiusSmall);
                foreach (var child in chart.Children) child.Visibility = Visibility.Collapsed;
                return;
            case TextBlock text:
                Bar(text);
                return;
            case RichTextBlock rich:
                rich.Foreground = Palette.TransparentBrush;
                return;
            case Shape shape:
                if (shape.Fill is not null) shape.Fill = Palette.SkeletonBrush;
                if (shape.Stroke is not null) shape.Stroke = Palette.SkeletonBrush;
                return;
            case Image image:
                image.Opacity = 0;
                return;
            case ProgressRing ring:
                ring.Visibility = Visibility.Collapsed;
                return;
            case Border border:
                // A filled box inside a card - a swatch, a share bar, a badge -
                // goes neutral. Cards themselves keep their surface.
                if (border.Background is SolidColorBrush fill && fill != Palette.SurfaceBrush && fill != Palette.BgBrush && fill.Color.A > 0)
                    border.Background = Palette.SkeletonBrush;
                Walk(border.Child);
                return;
            case Panel panel:
                if (panel.Background is SolidColorBrush pf && pf != Palette.SurfaceBrush && pf != Palette.BgBrush && pf != Palette.TransparentBrush && pf.Color.A > 0)
                    panel.Background = Palette.SkeletonBrush;
                foreach (var child in panel.Children) Walk(child);
                return;
            case Button button:
                // A chip or a button keeps its outline; an active one loses its fill.
                if (button.Background is SolidColorBrush bf && bf.Color.A > 0) button.Background = Palette.SkeletonBrush;
                button.BorderBrush = Palette.SkeletonBrush;
                button.Foreground = Palette.TransparentBrush;
                Walk(button.Content);
                return;
            case FontIcon icon:
                icon.Foreground = Palette.SkeletonBrush;
                return;
            case ContentControl control:
                Walk(control.Content);
                return;
            case Viewbox box:
                Walk(box.Child);
                return;
        }
    }

    /// <summary>Text laid out as itself, shown as a bar behind every line.</summary>
    private static void Bar(TextBlock text)
    {
        text.Foreground = Palette.TransparentBrush;
        text.IsTextSelectionEnabled = false;
        void Highlight()
        {
            text.TextHighlighters.Clear();
            var length = text.Text?.Length ?? 0;
            if (length == 0) return;
            var highlighter = new TextHighlighter { Background = Palette.SkeletonBrush, Foreground = Palette.TransparentBrush };
            highlighter.Ranges.Add(new TextRange { StartIndex = 0, Length = length });
            text.TextHighlighters.Add(highlighter);
        }
        Highlight();
        // A count-up writes its final figure after this runs.
        text.Loaded += (_, _) => Highlight();
    }

    private static void Pulse(UIElement root)
    {
        if (!Motion.Enabled) return;
        var story = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0.55,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(fade, root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        if (root is FrameworkElement fe)
        {
            fe.Loaded += (_, _) => story.Begin();
            fe.Unloaded += (_, _) => story.Stop();
        }
    }
}

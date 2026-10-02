using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace SpeedMeter.App.Theme;

/// <summary>
/// Element builders for the design system. The pages are assembled in code from
/// these, so every card, label and panel head comes out of one definition -
/// the same role the original's shared CSS classes played.
/// </summary>
public static class Ui
{
    public const double Radius = 14;
    public const double RadiusSmall = 9;

    /* ---------------------------------------------------------------- Text */

    public static TextBlock Text(
        string text,
        double size = 15,
        int weight = 400,
        Brush? brush = null,
        double spacing = 0,
        bool numeric = false,
        bool wrap = false,
        bool selectable = true)
    {
        var block = new TextBlock
        {
            // Text is selectable and copyable, as it was in the browser. Labels
            // on controls are not (see NoSelect): dragging there should press
            // the control, not start a selection.
            IsTextSelectionEnabled = selectable,
            SelectionHighlightColor = Palette.AccentBrush,
            Text = text,
            FontFamily = Fonts.Sans,
            FontSize = size,
            FontWeight = Fonts.Weight(weight),
            Foreground = brush ?? Palette.TextBrush,
            CharacterSpacing = (int)Math.Round(spacing * 1000),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            IsTextScaleFactorEnabled = true,
        };
        if (numeric) Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    /// <summary>The small uppercase label: section titles, headline labels, table heads.</summary>
    public static TextBlock Caps(string text, double size = 13, double spacing = 0.13, Brush? brush = null) =>
        Text(text.ToUpperInvariant(), size, 600, brush ?? Palette.TextFaintBrush, spacing);

    public static TextBlock Mono(string text, double size = 13.5, Brush? brush = null)
    {
        var block = Text(text, size, 400, brush ?? Palette.TextMutedBrush);
        block.FontFamily = Fonts.Mono;
        return block;
    }

    /// <summary>A paragraph that wraps, with inline <c>code</c> runs marked by backticks.</summary>
    public static TextBlock Paragraph(string text, double size = 14.5, Brush? brush = null)
    {
        var block = Text("", size, 400, brush ?? Palette.TextFaintBrush, wrap: true);
        block.Text = null;
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new Run { Text = parts[i] };
            if (i % 2 == 1)
            {
                run.FontFamily = Fonts.Mono;
                run.FontSize = size - 1;
            }
            block.Inlines.Add(run);
        }
        block.LineHeight = size * 1.55;
        return block;
    }

    /* --------------------------------------------------------------- Cards */

    public static Border Card(UIElement child, Thickness padding, bool hover = false)
    {
        var card = new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius),
            Padding = padding,
            Child = child,
        };
        if (Palette.IsLight) Elevate(card, RestZ);
        if (hover) HoverLift(card);
        return card;
    }

    /// <summary>
    /// What shadows fall on: a plain surface behind the scrolling page. In WinUI 3
    /// a <see cref="ThemeShadow"/> outside a popup draws only onto elements
    /// registered as its receivers - with none, nothing shows - and a receiver
    /// may not be an ancestor of the caster. Set once by the window.
    /// </summary>
    public static UIElement? ShadowReceiver { get; set; }

    /// <summary>A shadow that falls on <see cref="ShadowReceiver"/>.</summary>
    public static ThemeShadow Shadow()
    {
        var shadow = new ThemeShadow();
        if (ShadowReceiver is not null) shadow.Receivers.Add(ShadowReceiver);
        return shadow;
    }

    /// <summary>A card's height above the page on the light theme, at rest and hovered.</summary>
    private const float RestZ = 12, HoverZ = 32;

    /// <summary>
    /// Elevation, the light theme's separation and its hover: a soft shadow
    /// that grows as the element rises. On black a shadow is invisible, so the
    /// dark theme keeps its border-and-lift instead.
    /// </summary>
    public static void Elevate(UIElement element, float z)
    {
        element.Shadow ??= Shadow();
        var t = element.Translation;
        element.Translation = new Vector3(t.X, t.Y, z);
    }

    /// <summary>
    /// The card hover: the border brightens and the card lifts 2px, both eased.
    /// Translation rather than a margin change, so nothing around it reflows.
    /// </summary>
    public static void HoverLift(Border card, double lift = 2)
    {
        // The same TranslateTransform the entry animation uses: WinUI refuses a
        // RenderTransform on an element that also has a TranslationTransition.
        card.PointerEntered += (_, _) =>
        {
            card.BorderBrush = Palette.BorderBrightBrush;
            if (Palette.IsLight) Elevate(card, HoverZ);
            Slide(card, -lift, 200);
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = Palette.BorderBrush;
            if (Palette.IsLight) Elevate(card, RestZ);
            Slide(card, 0, 200);
        };
    }

    private static TranslateTransform Transform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void Slide(UIElement element, double to, int ms)
    {
        if (!Motion.Enabled)
        {
            Transform(element).Y = to;
            return;
        }
        var story = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
        };
        Storyboard.SetTarget(anim, Transform(element));
        Storyboard.SetTargetProperty(anim, "Y");
        story.Children.Add(anim);
        story.Begin();
    }

    /// <summary>
    /// A card with a title, an optional subtitle and an optional aside on the
    /// right (the web dashboard's CardTitle), then the body.
    /// </summary>
    public static Border Panel(string title, string? subtitle, UIElement? aside, UIElement body, bool rise = true)
    {
        var head = new Grid { ColumnSpacing = 19, Margin = new Thickness(0, 0, 0, 18.4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        var titleBlock = Text(title, 20.8, 600, spacing: -0.02);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(titleBlock, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        titles.Children.Add(titleBlock);
        if (subtitle is not null)
        {
            var sub = Paragraph(subtitle, 15, Palette.TextMutedBrush);
            sub.Margin = new Thickness(0, 5, 0, 0);
            titles.Children.Add(sub);
        }
        head.Children.Add(titles);
        if (aside is FrameworkElement r)
        {
            Grid.SetColumn(r, 1);
            r.VerticalAlignment = VerticalAlignment.Top;
            head.Children.Add(r);
        }

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(body);
        var card = Card(stack, new Thickness(25.6));
        card.Margin = new Thickness(0, 0, 0, 18.4);
        if (rise) Rise(card);
        return card;
    }

    /* --------------------------------------------------------------- Small */

    public static Border Swatch(Color color, double size = 11, double radius = 3) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(radius),
        Background = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public enum BadgeKind { Plain, Ok, Bad, Accent }

    /// <summary>The small rounded label: a run's status, an app's kind ("store", "service").</summary>
    public static Border Badge(string text, BadgeKind kind = BadgeKind.Plain, double size = 13, Thickness? padding = null)
    {
        var (fg, bg, border) = kind switch
        {
            BadgeKind.Ok => (Palette.GoodBrush, Palette.GoodDimBrush, Palette.TransparentBrush),
            BadgeKind.Bad => (Palette.WarnBrush, Palette.WarnDimBrush, Palette.TransparentBrush),
            BadgeKind.Accent => (Palette.AccentBrightBrush, Palette.AccentDimBrush, Palette.TransparentBrush),
            _ => (Palette.TextMutedBrush, Palette.TransparentBrush, Palette.BorderBrightBrush),
        };
        var label = Text(text, size, 600, fg, selectable: false);
        return new Border
        {
            Padding = padding ?? new Thickness(9.6, 3, 9.6, 3.5),
            // Half the height: WinUI does not clamp an oversized radius as CSS does.
            CornerRadius = new CornerRadius(size),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Background = bg,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
    }

    /// <summary>
    /// A range chip. The active one is SOLID: accent-fill with white text
    /// (4.62:1) and a glow - never the plain accent with white (3.87:1).
    /// </summary>
    public static Button Chip(string text, bool active, double size = 15)
    {
        var label = Text(text, size, 500, active ? Palette.OnAccentFillBrush : Palette.TextMutedBrush, selectable: false);
        var chip = new Button
        {
            Content = label,
            Padding = new Thickness(13.6, 5.4, 13.6, 6.4),
            MinWidth = 0,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Background = active ? Palette.AccentFillBrush : Palette.TransparentBrush,
            BorderBrush = active ? Palette.AccentFillBrush : Palette.BorderBrightBrush,
        };
        chip.Resources["ButtonBackgroundPointerOver"] = active ? Palette.AccentFillBrush : Palette.TransparentBrush;
        chip.Resources["ButtonBackgroundPressed"] = active ? Palette.AccentFillBrush : Palette.TransparentBrush;
        chip.Resources["ButtonBorderBrushPointerOver"] = active ? Palette.AccentFillBrush : Palette.TextFaintBrush;
        chip.Resources["ButtonBorderBrushPressed"] = active ? Palette.AccentFillBrush : Palette.TextFaintBrush;
        chip.PointerEntered += (_, _) => { if (!active) label.Foreground = Palette.TextBrush; };
        chip.PointerExited += (_, _) => { if (!active) label.Foreground = Palette.TextMutedBrush; };
        chip.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        chip.PointerEntered += (_, _) => chip.Translation = new Vector3(0, -1, Palette.IsLight ? 6 : 0);
        if (Palette.IsLight) chip.Shadow = Shadow();
        chip.PointerExited += (_, _) => chip.Translation = Vector3.Zero;
        return chip;
    }

    /// <summary>A thin share bar: the track, and a fill in the given colour.</summary>
    public static Grid ShareBar(double fraction, Brush fill, double height = 6, int delayMs = 0)
    {
        var grid = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { Background = Palette.InsetBrush, CornerRadius = new CornerRadius(height / 2) });
        var bar = new Border
        {
            Background = fill,
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        grid.Children.Add(bar);
        grid.SizeChanged += (_, e) => bar.Width = Math.Max(0, Math.Min(1, fraction)) * e.NewSize.Width;
        GrowX(bar, 700, delayMs);
        return grid;
    }

    /// <summary>Grows an element in from its left edge (the web's `grow` keyframes).</summary>
    public static void GrowX(FrameworkElement element, int ms, int delayMs = 0)
    {
        if (!Motion.Enabled) return;
        var scale = new ScaleTransform { ScaleX = 0 };
        element.RenderTransform = scale;
        var story = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
        };
        Storyboard.SetTarget(anim, scale);
        Storyboard.SetTargetProperty(anim, "ScaleX");
        story.Children.Add(anim);
        story.Begin();
    }

    /// <summary>
    /// The "i" that explains a figure: a real, focusable button whose tooltip
    /// carries the text, and whose accessible name and description say it too.
    /// </summary>
    public static Button InfoTip(string label, string text)
    {
        var glyph = Text("i", 11, 700, Palette.TextFaintBrush, selectable: false);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var button = new Button
        {
            Width = 17,
            Height = 17,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8.5),
            Content = glyph,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.TransparentBrush;
        button.Resources["ButtonBackgroundPressed"] = Palette.TransparentBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.AccentBrush;
        button.Resources["ButtonBorderBrushPressed"] = Palette.AccentBrush;
        button.PointerEntered += (_, _) => glyph.Foreground = Palette.AccentBrush;
        button.PointerExited += (_, _) => glyph.Foreground = Palette.TextFaintBrush;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetFullDescription(button, text);
        ToolTipService.SetToolTip(button, TipContent(text));
        ToolTipService.SetPlacement(button, PlacementMode.Bottom);
        return button;
    }

    /// <summary>A tooltip body in the explainer style: 290px wide, muted text.</summary>
    public static ToolTip TipContent(string text)
    {
        var block = Text(text, 13, 400, Palette.TextMutedBrush, wrap: true);
        block.LineHeight = 20;
        return new ToolTip
        {
            Content = block,
            MaxWidth = 320,
            Background = Palette.TooltipBgBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RadiusSmall),
            Padding = new Thickness(13, 11, 13, 11),
        };
    }

    /// <summary>A text-only tooltip for things like a clipped label.</summary>
    public static void SetTip(FrameworkElement element, string text) =>
        ToolTipService.SetToolTip(element, TipContent(text));

    /// <summary>Turns text selection off throughout a control's content.</summary>
    public static T NoSelect<T>(T content) where T : class
    {
        switch (content)
        {
            case TextBlock block:
                block.IsTextSelectionEnabled = false;
                break;
            case Panel panel:
                foreach (var child in panel.Children) NoSelect(child);
                break;
            case Border border when border.Child is not null:
                NoSelect(border.Child);
                break;
            case ContentControl control when control.Content is not null:
                NoSelect(control.Content);
                break;
        }
        return content;
    }

    /* ------------------------------------------------------------ Buttons */

    /// <summary>The outlined button. <paramref name="primary"/> is the accent one (Refresh).</summary>
    public static Button Button(object content, bool primary = false, double fontSize = 15, Thickness? padding = null)
    {
        var button = new Button
        {
            Content = NoSelect(content),
            FontFamily = Fonts.Sans,
            FontSize = fontSize,
            FontWeight = Fonts.Weight(570),
            Padding = padding ?? new Thickness(19, 10, 19, 10),
            CornerRadius = new CornerRadius(RadiusSmall),
            BorderThickness = new Thickness(1),
            Background = primary ? Palette.AccentDimBrush : Palette.SurfaceBrush,
            BorderBrush = primary ? Palette.AccentBorderStrongBrush : Palette.BorderBrightBrush,
            Foreground = primary ? Palette.AccentBrightBrush : Palette.TextBrush,
        };
        button.Resources["ButtonBackgroundPointerOver"] = primary ? Palette.AccentDimBrush : Palette.SurfaceHoverBrush;
        button.Resources["ButtonBackgroundPressed"] = primary ? Palette.AccentDimBrush : Palette.SurfaceHoverBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.AccentBrush;
        button.Resources["ButtonBorderBrushPressed"] = Palette.AccentBrush;
        button.Resources["ButtonForegroundPointerOver"] = primary ? Palette.AccentBrightBrush : Palette.TextBrush;
        button.Resources["ButtonForegroundPressed"] = primary ? Palette.AccentBrightBrush : Palette.TextBrush;
        button.Resources["ButtonBackgroundDisabled"] = primary ? Palette.AccentDimBrush : Palette.SurfaceBrush;
        button.Resources["ButtonBorderBrushDisabled"] = primary ? Palette.AccentBorderStrongBrush : Palette.BorderBrightBrush;
        button.Resources["ButtonForegroundDisabled"] = primary ? Palette.AccentBrightBrush : Palette.TextMutedBrush;
        button.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        button.PointerEntered += (_, _) => { if (button.IsEnabled) button.Translation = new Vector3(0, -1, Palette.IsLight ? 6 : 0); };
        if (Palette.IsLight) button.Shadow = Shadow();
        button.PointerExited += (_, _) => button.Translation = Vector3.Zero;
        return button;
    }

    /// <summary>A text link in the accent colour, like "← All projects".</summary>
    public static HyperlinkButton Link(string text, Action onClick, double size = 14.5, Brush? brush = null)
    {
        var link = new HyperlinkButton
        {
            Content = Text(text, size, 400, brush ?? Palette.TextMutedBrush, selectable: false),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Palette.TransparentBrush,
        };
        link.Resources["HyperlinkButtonBackgroundPointerOver"] = Palette.TransparentBrush;
        link.Resources["HyperlinkButtonBackgroundPressed"] = Palette.TransparentBrush;
        link.Click += (_, _) => onClick();
        return link;
    }

    /* ------------------------------------------------------------- Motion */

    /// <summary>
    /// The "rise" entry: fade in from 14px below, staggered by <paramref name="delayMs"/>.
    /// Skipped when the system asks for reduced motion.
    /// </summary>
    public static void Rise(UIElement element, int delayMs = 0)
    {
        if (!Motion.Enabled) return;
        var transform = Transform(element);
        transform.Y = 14;
        element.Opacity = 0;
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var story = new Storyboard();
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var duration = new Duration(TimeSpan.FromMilliseconds(450));

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, BeginTime = begin, EasingFunction = ease };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);

        var slide = new DoubleAnimation { From = 14, To = 0, Duration = duration, BeginTime = begin, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        story.Children.Add(slide);
        story.Begin();
    }
}

/// <summary>Whether the system allows animation (Settings, Accessibility, Visual effects).</summary>
public static class Motion
{
    private static readonly Windows.UI.ViewManagement.UISettings Settings = new();

    /// <summary>
    /// Set while a page is rebuilt for a quiet refresh (a minute landing in the
    /// history): the same page again must not replay its entrance.
    /// </summary>
    public static bool Quiet { get; set; }

    public static bool Enabled
    {
        get
        {
            if (Quiet) return false;
            try
            {
                return Settings.AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }
}

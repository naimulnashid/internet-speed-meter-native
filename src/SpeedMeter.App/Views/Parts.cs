using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SpeedMeter.App.Controls;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.Views;

/// <summary>Pieces more than one page draws.</summary>
public static class Parts
{
    /// <summary>The page's h1 and the line under it.</summary>
    public static StackPanel PageHead(string title, string? sub, UIElement? aside = null)
    {
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 30) };
        var row = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var h1 = Ui.Text(title, 35, 600, spacing: -0.02);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(h1, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        row.Children.Add(h1);
        if (aside is FrameworkElement a)
        {
            a.VerticalAlignment = VerticalAlignment.Center;
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(a, 1);
            row.Children.Add(a);
        }
        head.Children.Add(row);
        if (sub is not null)
        {
            var line = Ui.Text(sub, 15, 400, Palette.TextMutedBrush, wrap: true);
            line.Margin = new Thickness(0, 6, 0, 0);
            head.Children.Add(line);
        }
        return head;
    }

    /// <summary>The small uppercase label over a figure.</summary>
    public static TextBlock StatLabel(string text)
    {
        var label = Ui.Caps(text, 13, 0.09);
        label.IsTextSelectionEnabled = false;
        return label;
    }

    /// <summary>
    /// A rate with its unit set smaller - "234 Mbps" - counting up to its value
    /// in the final unit, so it never passes through a different one.
    /// </summary>
    public static StackPanel RateValue(double bytesPerSecond, UnitMode units, Brush? brush = null, double size = 40)
    {
        var (text, unit) = Format.SplitRate(bytesPerSecond, units);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = size * 0.14 };
        var value = Ui.Text(text, size, 650, brush ?? Palette.TextBrush, -0.035, numeric: true);
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            var decimals = text.Contains('.') ? text.Split('.')[1].Length : 0;
            CountUp.Apply(value, number, v => v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture));
        }
        row.Children.Add(value);
        var unitText = Ui.Text(unit, size * 0.42, 600, Palette.TextMutedBrush);
        unitText.VerticalAlignment = VerticalAlignment.Bottom;
        unitText.Margin = new Thickness(0, 0, 0, size * 0.14);
        row.Children.Add(unitText);
        return row;
    }

    /// <summary>A plain figure ("49", "ms") in the same type as <see cref="RateValue"/>.</summary>
    public static StackPanel Figure(string value, string? unit, Brush? brush = null, double size = 40)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = size * 0.14 };
        row.Children.Add(Ui.Text(value, size, 650, brush ?? Palette.TextBrush, -0.035, numeric: true));
        if (unit is not null)
        {
            var unitText = Ui.Text(unit, size * 0.42, 600, Palette.TextMutedBrush);
            unitText.VerticalAlignment = VerticalAlignment.Bottom;
            unitText.Margin = new Thickness(0, 0, 0, size * 0.14);
            row.Children.Add(unitText);
        }
        return row;
    }

    /// <summary>A figure card: label (with an optional icon at the right), the value, and lines under it.</summary>
    public static Border FigureCard(string label, UIElement value, IEnumerable<string?> lines, int delay = 0, UIElement? icon = null, UIElement? footer = null)
    {
        var stack = new StackPanel();
        var head = new Microsoft.UI.Xaml.Controls.Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(StatLabel(label));
        if (icon is FrameworkElement i)
        {
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(i, 1);
            head.Children.Add(i);
        }
        stack.Children.Add(head);
        if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(value);
        var first = true;
        foreach (var line in lines)
        {
            // A blank line keeps its place, so a card does not grow when its figures land.
            var text = Ui.Text(line ?? " ", first ? 15 : 14, 400, first ? Palette.TextMutedBrush : Palette.TextFaintBrush, wrap: true, numeric: true);
            text.Margin = new Thickness(0, first ? 6 : 3, 0, 0);
            stack.Children.Add(text);
            first = false;
        }
        if (footer is FrameworkElement f)
        {
            f.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(f);
        }
        var card = Ui.Card(stack, new Thickness(25.6), hover: true);
        Ui.Rise(card, delay);
        return card;
    }

    public static FitGrid Grid(double min, params UIElement[] children)
    {
        var grid = FitGrid.AutoFit(min, 18.4);
        foreach (var c in children) grid.Children.Add(c);
        grid.Margin = new Thickness(0, 0, 0, 18.4);
        return grid;
    }

    /// <summary>A red-bordered alert, for something genuinely wrong.</summary>
    public static Border Alert(string title, string body, string? detail = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 20.8, 600, Palette.WarnBrush, wrap: true));
        var b = Ui.Paragraph(body, 15.5, Palette.TextBrush);
        b.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(b);
        if (detail is not null)
        {
            var d = Ui.Paragraph(detail, 14.5, Palette.TextMutedBrush);
            d.Margin = new Thickness(0, 9, 0, 0);
            stack.Children.Add(d);
        }
        var alert = new Border
        {
            Padding = new Thickness(21, 18, 21, 18),
            CornerRadius = new CornerRadius(Ui.Radius),
            BorderBrush = Palette.WarnBrush,
            BorderThickness = new Thickness(1),
            Background = Palette.WarnDimBrush,
            Margin = new Thickness(0, 0, 0, 18.4),
            Child = stack,
        };
        Ui.Rise(alert);
        return alert;
    }

    /// <summary>
    /// The explanatory note: a bold lead sentence, then the rest, in a quiet
    /// box. <c>`code`</c> runs are set in the mono face.
    /// </summary>
    public static Border Note(string lead, string body, bool warn = false)
    {
        var text = Ui.Paragraph(body, 15, Palette.TextMutedBrush);
        text.Inlines.Insert(0, new Run { Text = lead + " ", FontWeight = Fonts.Weight(600), Foreground = warn ? Palette.WarnBrush : Palette.TextBrush });
        return new Border
        {
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = new CornerRadius(Ui.Radius),
            BorderBrush = warn ? Palette.WarnBrush : Palette.BorderBrush,
            BorderThickness = new Thickness(1),
            Background = warn ? Palette.WarnDimBrush : Palette.InsetBrush,
            Margin = new Thickness(0, 0, 0, 18.4),
            Child = text,
        };
    }

    /// <summary>The centred "nothing here" message, with an optional action.</summary>
    public static StackPanel Empty(string title, string body, UIElement? action = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 64, 16, 64), MaxWidth = 620 };
        var h = Ui.Text(title, 35, 600, spacing: -0.02);
        h.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(h);
        var p = Ui.Paragraph(body, 16, Palette.TextMutedBrush);
        p.TextAlignment = TextAlignment.Center;
        p.Margin = new Thickness(0, 13, 0, 22);
        stack.Children.Add(p);
        if (action is FrameworkElement a)
        {
            a.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(a);
        }
        return stack;
    }

    /// <summary>"■ Down  ■ Up", centred under a chart.</summary>
    public static StackPanel Legend()
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (label, brush) in new[] { ("Down", Palette.DownBrush), ("Up", Palette.UpBrush) })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = brush, VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(Ui.Text(label, 14, 400, Palette.TextMutedBrush, selectable: false));
            legend.Children.Add(item);
        }
        return legend;
    }

    /// <summary>A mono cell: the dates in the runs table.</summary>
    public static TextBlock MonoCell(string text, Brush? brush = null, double size = 14.5) => Ui.Mono(text, size, brush ?? Palette.TextBrush);

    /// <summary>A badge glyph for a result card, in the card's tone.</summary>
    public static FontIcon Icon(string glyph, Brush? brush = null) => new()
    {
        Glyph = glyph,
        FontSize = 17,
        Foreground = brush ?? Palette.TextFaintBrush,
        VerticalAlignment = VerticalAlignment.Top,
    };
}

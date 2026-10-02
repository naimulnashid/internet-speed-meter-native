using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpeedMeter.App.Theme;

namespace SpeedMeter.App.Controls;

/// <summary>
/// A column: its header, an optional info tip, a width, which side it aligns
/// to (text left, figures right, by default the first column only is left),
/// and for a sortable column the click and whether it is the sorted one.
/// </summary>
public sealed record Column(string Header, (string Label, string Text)? Tip = null, double? Width = null, bool? Left = null,
    Action? OnSort = null, string? SortMark = null)
{
    public bool IsLeft(int index) => Left ?? index == 0;
}

/// <summary>
/// A data table in the dashboard's style: small uppercase headers, figures
/// right-aligned in tabular digits, rows ruled faintly and lit on hover, and a
/// footer row ruled a shade brighter. Wider than its panel, it scrolls sideways
/// rather than squeezing.
/// </summary>
public sealed class DataTable
{
    public double MinWidth { get; init; } = 640;

    /// <summary>Vertical padding of a body cell.</summary>
    public double RowPadding { get; init; } = 13;
    private readonly Grid _grid = new();
    private readonly List<Column> _columns;
    private int _row;

    public DataTable(IReadOnlyList<Column> columns)
    {
        _columns = columns.ToList();
        for (var i = 0; i < _columns.Count; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = _columns[i].Width is { } w ? new GridLength(w) : i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }
        AddHeader();
    }

    private void AddHeader()
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _columns.Count; i++)
        {
            var column = _columns[i];
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = column.IsLeft(i) ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var caps = Ui.Caps(column.Header, 12.5, 0.08, column.SortMark is not null ? Palette.AccentBrightBrush : null);
            caps.IsTextSelectionEnabled = false;
            head.Children.Add(caps);
            if (column.SortMark is { } mark) head.Children.Add(Ui.Text(mark, 12.5, 600, Palette.AccentBrightBrush, selectable: false));
            if (column.Tip is { } tip) head.Children.Add(Ui.InfoTip(tip.Label, tip.Text));
            UIElement headContent = head;
            if (column.OnSort is { } sort)
            {
                // A button, so the sort is reachable by keyboard as well as mouse.
                var button = new Button
                {
                    Content = head,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Background = Palette.TransparentBrush,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = head.HorizontalAlignment,
                };
                button.Resources["ButtonBackgroundPointerOver"] = Palette.TransparentBrush;
                button.Resources["ButtonBackgroundPressed"] = Palette.TransparentBrush;
                button.PointerEntered += (_, _) => caps.Foreground = Palette.TextBrush;
                button.PointerExited += (_, _) => caps.Foreground = column.SortMark is not null ? Palette.AccentBrightBrush : Palette.TextFaintBrush;
                button.Click += (_, _) => sort();
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Sort by {column.Header}");
                headContent = button;
            }
            var cell = new Border
            {
                Padding = new Thickness(14, 11, 14, 11),
                BorderBrush = Palette.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = headContent,
            };
            Grid.SetColumn(cell, i);
            Grid.SetRow(cell, 0);
            _grid.Children.Add(cell);
        }
        _row = 1;
    }

    /// <summary>A plain text cell.</summary>
    public static TextBlock Cell(string text, Brush? brush = null, int weight = 400, bool numeric = true, double size = 15.5) =>
        Ui.Text(text, size, weight, brush ?? Palette.TextBrush, numeric: numeric);

    public void AddRow(IReadOnlyList<UIElement?> cells, bool separatorAbove = false)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = _row++;

        // The hover wash spans the row, behind its cells.
        var wash = new Border
        {
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.RowBorderBrush,
            BorderThickness = new Thickness(0, separatorAbove ? 1 : 0, 0, 1),
        };
        if (separatorAbove) wash.BorderBrush = Palette.BorderBrightBrush;
        Grid.SetRow(wash, row);
        Grid.SetColumnSpan(wash, _columns.Count);
        _grid.Children.Add(wash);
        void Lit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => wash.Background = Palette.SurfaceHoverBrush;
        void Unlit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => wash.Background = Palette.TransparentBrush;
        wash.PointerEntered += Lit;
        wash.PointerExited += Unlit;

        for (var i = 0; i < cells.Count && i < _columns.Count; i++)
        {
            if (cells[i] is not FrameworkElement content) continue;
            // A cell tagged "stretch" (a share bar) fills its column instead.
            if (content.Tag as string != "stretch")
                content.HorizontalAlignment = _columns[i].IsLeft(i) ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            content.VerticalAlignment = VerticalAlignment.Center;
            content.Margin = new Thickness(14, RowPadding, 14, RowPadding);
            // Cells stay hit-testable (a swatch carries a tooltip), so they
            // light the row too.
            content.PointerEntered += Lit;
            content.PointerExited += Unlit;
            Grid.SetColumn(content, i);
            Grid.SetRow(content, row);
            _grid.Children.Add(content);
        }
    }

    public int RowCount => _row - 1;

    /// <summary>The table in its sideways scroller.</summary>
    public ScrollViewer Build()
    {
        _grid.MinWidth = MinWidth;
        return new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }
}

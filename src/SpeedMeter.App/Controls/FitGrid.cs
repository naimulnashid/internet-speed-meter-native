using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SpeedMeter.App.Controls;

/// <summary>
/// A grid whose column count comes from its own width, with the cells of a row
/// stretched to the row's tallest - CSS grid's <c>auto-fit</c> in one panel.
/// </summary>
/// <remarks>
/// The count comes from the space the grid actually has, not the window's:
/// page zoom lays the page out narrower than the window, and only the panel's
/// own width says how much room its cards have.
/// </remarks>
public sealed class FitGrid : Panel
{
    private readonly Func<double, int> _columnsFor;

    public double Gap { get; init; } = 16;

    public FitGrid(Func<double, int> columnsFor) => _columnsFor = columnsFor;

    /// <summary>auto-fit, minmax(<paramref name="min"/>, 1fr).</summary>
    public static FitGrid AutoFit(double min, double gap) =>
        new(width => Math.Max(1, (int)Math.Floor((width + gap) / (min + gap)))) { Gap = gap };

    /// <summary>
    /// Never more tracks than children: auto-fit collapses empty tracks, so
    /// four cards in room for five stretch to fill the row.
    /// </summary>
    private int Columns(double width) => Math.Max(1, Math.Min(Math.Max(1, Children.Count), _columnsFor(double.IsFinite(width) ? width : 1200)));

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : 1200;
        var columns = Columns(width);
        var cellWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        double height = 0;
        for (var start = 0; start < Children.Count; start += columns)
        {
            double rowHeight = 0;
            for (var i = start; i < Math.Min(Children.Count, start + columns); i++)
            {
                Children[i].Measure(new Size(cellWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }
            height += rowHeight + (start > 0 ? Gap : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var cellWidth = Math.Max(0, (final.Width - Gap * (columns - 1)) / columns);
        double y = 0;
        for (var start = 0; start < Children.Count; start += columns)
        {
            double rowHeight = 0;
            for (var i = start; i < Math.Min(Children.Count, start + columns); i++)
            {
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }
            for (var i = start; i < Math.Min(Children.Count, start + columns); i++)
            {
                var column = i - start;
                Children[i].Arrange(new Rect(column * (cellWidth + Gap), y, cellWidth, rowHeight));
            }
            y += rowHeight + Gap;
        }
        return final;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.History;
using SpeedMeter.Core.View;
using Windows.Foundation;

namespace SpeedMeter.App.Charts;

/// <summary>
/// The fastest second of each day, down and up SIDE BY SIDE rather than
/// stacked: two directions, not two parts of a whole.
/// </summary>
/// <remarks>
/// Every day in the range has its place, and a day the meter never ran is an
/// empty slot with "Nothing recorded" in its tooltip - never a zero, which
/// would read as a day the connection did nothing.
/// </remarks>
public sealed class DailyPeakChart : ChartSurface
{
    private const double Top = 8, Right = 8, Bottom = 4, Left = 4, YAxisWidth = 82, XAxisHeight = 28;
    private const double MaxBarWidth = 46;

    private readonly IReadOnlyList<(DateTime Day, DayStats? Stats)> _days;
    private readonly UnitMode _units;
    private readonly List<(double X, double Width)> _bands = [];
    private Rectangle? _wash;
    private double _plotBottom;

    public DailyPeakChart(IReadOnlyList<(DateTime Day, DayStats? Stats)> days, UnitMode units, double height = 260, bool animate = true) : base(height, animate)
    {
        _days = days;
        _units = units;
    }

    protected override void Draw(bool animate)
    {
        _bands.Clear();
        var plotLeft = Left + YAxisWidth;
        var plotRight = W - Right;
        _plotBottom = H - Bottom - XAxisHeight;
        var plotW = Math.Max(1, plotRight - plotLeft);

        var peak = _days.Count == 0 ? 0 : _days.Max(d => d.Stats is { } s ? Math.Max(s.Down.BytesPerSecond, s.Up.BytesPerSecond) : 0);
        var top = Axes.RateGrid(Canvas, peak, _units, plotLeft, plotRight, Top, _plotBottom);
        double Y(double v) => _plotBottom - (top > 0 ? v / top : 0) * (_plotBottom - Top);

        _wash = new Rectangle { Fill = Theme.Palette.HoverWashBrush, Visibility = Visibility.Collapsed, Height = _plotBottom - Top };
        Canvas.SetTop(_wash, Top);
        Canvas.Children.Add(_wash);

        var n = _days.Count;
        var band = plotW / Math.Max(1, n);
        // Two bars and the gaps around them; a bar never wider than Recharts' maxBarSize.
        var barW = Math.Max(1, Math.Min(MaxBarWidth, band * 0.8 / 2));
        var pairW = barW * 2 + Math.Min(4, band * 0.06);
        var layer = new Canvas();
        var centres = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var x = plotLeft + i * band;
            _bands.Add((x, band));
            centres.Add(x + band / 2);
            if (_days[i].Stats is not { } s) continue;
            var left = x + (band - pairW) / 2;
            AddBar(layer, left, barW, Y(s.Down.BytesPerSecond), Theme.Palette.DownBrush);
            AddBar(layer, left + pairW - barW, barW, Y(s.Up.BytesPerSecond), Theme.Palette.UpBrush);
        }
        Canvas.Children.Add(layer);
        Axes.CategoryLabels(Canvas, _days.Select(d => Format.DayShort(d.Day)).ToList(), centres, _plotBottom + 6 + 11, W);
        if (animate) Axes.GrowUp(layer, _plotBottom);
    }

    private void AddBar(Canvas layer, double x, double width, double y, Microsoft.UI.Xaml.Media.Brush fill)
    {
        var height = _plotBottom - y;
        if (height <= 0.2) return;
        layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = Axes.RoundedTop(new Rect(x, y, width, height), Math.Min(3, width / 2)), Fill = fill });
    }

    protected override void OnHover(Point at)
    {
        if (_wash is null) return;
        var index = _bands.FindIndex(b => at.X >= b.X && at.X < b.X + b.Width);
        if (index < 0 || at.Y > _plotBottom)
        {
            ClearHover();
            return;
        }
        _wash.Width = _bands[index].Width;
        Canvas.SetLeft(_wash, _bands[index].X);
        _wash.Visibility = Visibility.Visible;

        var (day, stats) = _days[index];
        var body = ChartTooltip.Stack();
        body.Children.Add(ChartTooltip.Title(Format.DayLong(day)));
        if (stats is null)
        {
            body.Children.Add(Ui.Text("Nothing recorded", 14, 400, Theme.Palette.TextMutedBrush));
        }
        else
        {
            body.Children.Add(ChartTooltip.Row(Theme.Palette.Down, "Peak down", Format.Rate(stats.Down.BytesPerSecond, _units)));
            body.Children.Add(ChartTooltip.Row(Theme.Palette.Up, "Peak up", Format.Rate(stats.Up.BytesPerSecond, _units)));
            body.Children.Add(ChartTooltip.Muted($"Moved {Format.Bytes(stats.DownBytes)} down · {Format.Bytes(stats.UpBytes)} up", 6, 13));
            body.Children.Add(ChartTooltip.Muted($"Recorded {Format.Duration(TimeSpan.FromSeconds(stats.Seconds))}", 2, 13));
        }
        Tooltip.Show(body, at);
    }

    protected override void ClearHover()
    {
        base.ClearHover();
        if (_wash is not null) _wash.Visibility = Visibility.Collapsed;
    }
}

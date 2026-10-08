using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpeedMeter.App.Charts;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.History;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.Views;

/// <summary>Everything the Speed page shows, read in one go off the UI thread.</summary>
public sealed record SpeedData(
    UnitMode Units,
    Peak TodayDown,
    Peak TodayUp,
    Peak BestDown,
    Peak BestUp,
    IReadOnlyList<Sustained>? Sustained,
    IReadOnlyList<(DateTime Day, DayStats? Stats)> Days,
    int RangeDays,
    DateTime? FirstDay,
    int DaysRecorded,
    bool RawRecording,
    string LogFolder,
    string? Problem);

/// <summary>
/// Speed: what the connection has actually done, from the meter's own record.
/// </summary>
/// <remarks>
/// <para>The fastest seconds are records, so they are taken over ALL history;
/// only the daily peak chart follows the range, and its dropdown sits on that
/// chart because nothing else on the page changes with it.</para>
/// <para>The sustained averages need individual seconds, which exist only for
/// as long as the raw log is kept: they cover the retained days only, and say
/// so when there are none.</para>
/// </remarks>
public sealed class SpeedPage(PageContext ctx) : IPage
{
    private SpeedData? _data;

    public void Placeholder() => _data = Views.Placeholder.Speed(ctx.State.Units, ctx.State.Ui.RangeDays);

    public void Load()
    {
        var settings = ctx.State.Meter;
        var range = Ranges.Parse(ctx.State.Ui.RangeDays);
        var all = HistoryStats.AllDays(settings.LogFolder);
        var today = DateTime.Now.Date;
        var todayStats = all.LastOrDefault(d => d.Day == today);
        var (bestDown, bestUp) = HistoryStats.Peaks(all);

        var first = all.Count > 0 ? all[0].Day : (DateTime?)null;
        var start = Ranges.From(range, today);
        if (first is { } f && f > start) start = f;
        var byDay = all.ToDictionary(d => d.Day);
        var days = new List<(DateTime, DayStats?)>();
        if (first is not null)
            for (var d = start; d <= today; d = d.AddDays(1)) days.Add((d, byDay.GetValueOrDefault(d)));

        var sustained = HistoryStats.SustainedDown(settings.RawFolder, today.AddYears(-100), today);
        _data = new SpeedData(settings.Units, todayStats?.Down ?? Peak.None, todayStats?.Up ?? Peak.None, bestDown, bestUp,
            sustained, days, range, first, all.Count, settings.RawFolder.Length > 0, settings.LogFolder, ctx.State.RecordingProblem);
    }

    public UIElement Build()
    {
        var data = _data!;
        var page = new StackPanel();

        if (data.FirstDay is null)
        {
            if (data.Problem is { } p) page.Children.Add(Parts.Alert("Speed history is not being recorded", p, "Details are in `error.log` beside settings.ini."));
            page.Children.Add(Parts.Empty("Nothing recorded yet",
                $"Give the meter a minute to write its first rollup. The history is kept in `{data.LogFolder}`."));
            return page;
        }

        if (data.Problem is { } problem) page.Children.Add(Parts.Alert("Speed history is not being recorded", problem, "Details are in `error.log` beside settings.ini. Nothing is lost from before it stopped."));

        static string When(Peak peak) => peak.AtUtc is { } at ? Format.Stamp(at) : "never observed";
        page.Children.Add(Parts.Grid(215,
            Parts.FigureCard("Today's fastest second down", Parts.RateValue(data.TodayDown.BytesPerSecond, data.Units, Palette.DownBrush), [When(data.TodayDown)], 0),
            Parts.FigureCard("Today's fastest second up", Parts.RateValue(data.TodayUp.BytesPerSecond, data.Units, Palette.UpBrush), [When(data.TodayUp)], 60),
            Parts.FigureCard("Fastest second down", Parts.RateValue(data.BestDown.BytesPerSecond, data.Units, Palette.DownBrush), [When(data.BestDown)], 120),
            Parts.FigureCard("Fastest second up", Parts.RateValue(data.BestUp.BytesPerSecond, data.Units, Palette.UpBrush), [When(data.BestUp)], 180)));

        // The second row ends with how much history the figures stand on.
        var sustained = data.Sustained ?? [];
        var record = Parts.FigureCard("On record",
            Parts.Figure(Format.Count(data.DaysRecorded), data.DaysRecorded == 1 ? "day" : "days", size: 32),
            [$"since {Format.DayLong(data.FirstDay.Value)}", "recorded at the adapter once a second"], sustained.Count * 60);
        page.Children.Add(Parts.Grid(215, [.. sustained.Select((s, i) => Parts.FigureCard(
            $"Best sustained {(s.WindowSeconds < 60 ? $"{s.WindowSeconds} s" : $"{s.WindowSeconds / 60} min")}",
            Parts.RateValue(s.BytesPerSecond, data.Units, Palette.DownBrush, 32),
            [s.StartUtc is { } at ? Format.Stamp(at) : "no unbroken window that long"], i * 60)), record]));

        if (data.Sustained is null)
        {
            page.Children.Add(Parts.Note("No second-by-second data.", data.RawRecording
                ? "The best sustained averages are computed from the raw log, which is kept for the retention period only."
                : "The best sustained averages need the raw log, and raw recording is switched off (`rawfolder` is empty in settings.ini)."));
        }

        page.Children.Add(DailyPanel(data));

        page.Children.Add(Parts.Note("These are observed speeds, not a line rate.",
            "The meter records only what actually crossed the adapter, so a peak is the fastest thing that happened to run - never proof the connection could not go faster. For what it can do, run a speed test."));
        return page;
    }

    private Border DailyPanel(SpeedData data)
    {
        var range = new ComboBox { MinWidth = 130, FontFamily = Fonts.Sans, FontSize = 14.5 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(range, "Days shown");
        foreach (var days in Ranges.Offered) range.Items.Add(new ComboBoxItem { Content = Ranges.Label(days), Tag = days });
        range.SelectedIndex = Array.IndexOf(Ranges.Offered, data.RangeDays);
        range.SelectionChanged += (_, _) =>
        {
            if (range.SelectedItem is not ComboBoxItem { Tag: int days } || days == ctx.State.Ui.RangeDays) return;
            ctx.State.Ui.RangeDays = days;
            ctx.State.Ui.Save();
            ctx.Redraw();
        };

        var body = new StackPanel();
        if (data.Days.All(d => d.Stats is null))
        {
            body.Children.Add(Ui.Text("Nothing recorded in this window. Pick a longer range.", 15, 400, Palette.TextMutedBrush, wrap: true));
        }
        else
        {
            body.Children.Add(new DailyPeakChart(data.Days, data.Units));
            body.Children.Add(Parts.Legend());
        }
        return Ui.Panel("Daily peak", "The fastest single second each day.", range, body);
    }
}

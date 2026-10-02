using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpeedMeter.App.Charts;
using SpeedMeter.App.Controls;
using SpeedMeter.App.State;
using SpeedMeter.App.Theme;
using SpeedMeter.Core;
using SpeedMeter.Core.History;
using SpeedMeter.Core.SpeedTest;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.Views;

/// <summary>One saved run, and the fastest second the meter saw at the adapter during it.</summary>
public sealed record RunRow(SpeedTestResult Run, double? MeterSaw);

public sealed record TestData(UnitMode Units, IReadOnlyList<RunRow> Runs, int Total, int Page, int Pages);

/// <summary>
/// Test: an active speed test, beside every run kept so far.
/// </summary>
/// <remarks>
/// <para>This is the one page that spends data, so it says what a run will
/// cost before it costs it. The run itself lives in the app
/// (<see cref="SpeedTestSession"/>), not here, so leaving the page - or
/// closing the window - does not stop it.</para>
/// <para>Each saved run is put beside what the meter independently recorded
/// at the adapter at the same moment: agreement is a check on both, and that
/// comparison is why this page belongs to the meter rather than being a worse
/// copy of a speed-test website.</para>
/// </remarks>
public sealed class TestPage(PageContext ctx) : IPage
{
    private const int PageSize = 25;
    private TestData? _data;
    private int _page = 1;

    // The parts a run updates in place, ten times a second.
    private Gauge? _gauge;
    private Border? _progressFill;
    private Grid? _progressTrack;
    private TextBlock? _progressText;
    private SpeedChart? _downChart;
    private SpeedChart? _upChart;

    private SpeedTestSession Session => ctx.State.Test;

    public void Placeholder() => _data = Views.Placeholder.Test(ctx.State.Units);

    public void Load()
    {
        var settings = ctx.State.Meter;
        var all = SpeedTestStore.ReadAll(settings.LogFolder);
        var pages = Pages.Count(all.Count, PageSize);
        _page = Math.Clamp(_page, 1, pages);
        var slice = all.Skip((_page - 1) * PageSize).Take(PageSize).ToList();

        // Runs taken on another device measured THAT device; the meter has nothing to say about them.
        var local = slice.Where(r => r.Device is null).ToList();
        var minutes = LogReader.MinutesAt(settings.LogFolder, local.SelectMany(r =>
        {
            var (first, last) = HistoryStats.MinutesOf(r.At.ToUniversalTime(), r.DownSeconds + r.UpSeconds);
            return Enumerable.Range(0, (int)(last - first + 1)).Select(i => first + i);
        }));
        var rows = slice.Select(r => new RunRow(r, r.Device is null ? HistoryStats.MeterPeakFor(minutes, r.At.ToUniversalTime(), r.DownSeconds + r.UpSeconds) : null)).ToList();
        _data = new TestData(settings.Units, rows, all.Count, _page, pages);
    }

    public UIElement Build()
    {
        var data = _data!;
        var session = Session;
        session.LoadMeta();
        var page = new StackPanel();
        page.Children.Add(Parts.PageHead("Speed test", "How fast the line can go, measured against speed.cloudflare.com - the one thing this app sends traffic for."));

        page.Children.Add(Parts.Grid(360, SizeCard(session, data.Units), DialCard(session, data.Units)));

        foreach (var note in Notes(session)) page.Children.Add(note);

        var shown = session.Phase == TestPhase.Done ? session.Result : null;
        _downChart = new SpeedChart(false, data.Units);
        _upChart = new SpeedChart(true, data.Units);
        _downChart.Set([.. session.DownSeries], shown?.DownBps);
        _upChart.Set([.. session.UpSeries], shown?.UpBps);
        page.Children.Add(Parts.Grid(360,
            Parts.FigureCard("Download", shown is null ? Parts.Figure("-", null, Palette.DownBrush) : Parts.RateValue(shown.DownBps, data.Units, Palette.DownBrush),
                [shown is null ? null : $"peak second {Format.Rate(shown.PeakDownBps, data.Units)}",
                 shown is null ? null : $"{Format.Bytes(shown.DownBytes)} over {shown.DownSeconds:0.0}s"], 0, Parts.Icon("", Palette.DownBrush), _downChart),
            Parts.FigureCard("Upload", shown is null ? Parts.Figure("-", null, Palette.UpBrush) : Parts.RateValue(shown.UpBps, data.Units, Palette.UpBrush),
                [shown is null ? null : $"peak second {(shown.PeakUpBps is { } pu ? Format.Rate(pu, data.Units) : "-")}",
                 shown is null ? null : $"{Format.Bytes(shown.UpBytes)} over {shown.UpSeconds:0.0}s"], 60, Parts.Icon("", Palette.UpBrush), _upChart)));

        page.Children.Add(Parts.Grid(260, LatencyCards(shown)));

        if (session.Meta is { } meta) page.Children.Add(ConnectionPanel(meta));
        if (data.Runs.Count > 0) page.Children.Add(RunsPanel(data));

        session.Progress += OnProgress;
        page.Unloaded += (_, _) => session.Progress -= OnProgress;
        OnProgress();
        return page;
    }

    /* ---------------------------------------------------------- Controls */

    private Border SizeCard(SpeedTestSession session, UnitMode units)
    {
        var busy = session.Busy;
        var body = new StackPanel();
        var sizes = new WrapPanel { HorizontalSpacing = 7, VerticalSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var profile in Profile.All)
        {
            var chip = Ui.Chip($"{profile.Label} · {Format.Bytes(profile.TotalBytes)}", profile == session.Profile);
            chip.IsEnabled = !busy;
            chip.Click += (_, _) => session.SetProfile(profile);
            sizes.Children.Add(chip);
        }
        body.Children.Add(sizes);

        var label = Parts.StatLabel("Connections");
        label.Margin = new Thickness(0, 22, 0, 8);
        body.Children.Add(label);
        var connections = new WrapPanel { HorizontalSpacing = 7, VerticalSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        var parallel = Ui.Chip($"Parallel ×{session.Profile.Streams}", session.Parallel);
        parallel.IsEnabled = !busy;
        parallel.Click += (_, _) => session.SetParallel(true);
        var single = Ui.Chip("Single", !session.Parallel);
        single.IsEnabled = !busy;
        single.Click += (_, _) => session.SetParallel(false);
        connections.Children.Add(parallel);
        connections.Children.Add(single);
        body.Children.Add(connections);

        var why = Ui.Text(session.Parallel
            ? "Several transfers at once, which is what a speed test usually means: how fast is the line."
            : "One transfer, which is what a single download gets. Often well below the line, because one connection is limited by window size and round trip rather than by bandwidth.",
            14, 400, Palette.TextFaintBrush, wrap: true);
        why.Margin = new Thickness(0, 12, 0, 18);
        body.Children.Add(why);

        var cost = Parts.Note($"This run will transfer up to {Format.Bytes(session.Profile.TotalBytes)}",
            "to and from speed.cloudflare.com, and that traffic is real: it counts against a data cap, and the meter will record it as a spike in your history like any other transfer." +
            (session.Parallel ? "" : " A single connection may not reach the budget before the time limit, in which case it spends less."));
        cost.Margin = new Thickness(0);
        body.Children.Add(cost);
        return Ui.Panel("Test size", "Pick what a run may spend before starting it.", null, body, rise: false);
    }

    private Border DialCard(SpeedTestSession session, UnitMode units)
    {
        var (title, sub) = session.Phase switch
        {
            TestPhase.Latency => ("Warming up", "Timing small requests and counting lost pings."),
            TestPhase.Download => ("Downloading", "Pulling from speed.cloudflare.com."),
            TestPhase.Upload => ("Uploading", "Pushing to speed.cloudflare.com."),
            TestPhase.Saving => ("Saving", "Writing the result beside the history."),
            TestPhase.Done => ("Done", "The result is saved below with the other runs."),
            _ => ("Ready", " "),
        };

        var body = new StackPanel();
        _gauge = new Gauge(units);
        body.Children.Add(_gauge);

        // Hidden rather than removed at rest: its space stays reserved, so
        // starting a run does not make the card taller.
        var meter = new StackPanel { Opacity = session.Busy && session.Phase != TestPhase.Saving ? 1 : 0 };
        _progressTrack = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
        _progressTrack.Children.Add(new Border { Background = Palette.InsetBrush, CornerRadius = new CornerRadius(3), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1) });
        _progressFill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = session.Phase == TestPhase.Upload ? Palette.UpBrush : Palette.DownBrush };
        _progressTrack.Children.Add(_progressFill);
        _progressTrack.SizeChanged += (_, _) => OnProgress();
        meter.Children.Add(_progressTrack);
        _progressText = Ui.Text(" ", 14, 400, Palette.TextFaintBrush, numeric: true);
        _progressText.Margin = new Thickness(0, 8, 0, 0);
        meter.Children.Add(_progressText);
        body.Children.Add(meter);

        // The control sits beside the instrument it drives, and doubles as Stop.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 18, 0, 0) };
        var go = Ui.Button(session.Busy ? "Stop" : "Start test", primary: !session.Busy);
        go.IsEnabled = session.Phase != TestPhase.Saving;
        go.Click += (_, _) =>
        {
            if (session.Busy) session.Stop();
            else session.Start();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(go, session.Busy ? "Stop the speed test" : "Start a speed test");
        actions.Children.Add(go);
        if (session.Busy)
        {
            var ring = new ProgressRing { IsActive = true, Width = 18, Height = 18, Foreground = Palette.AccentBrightBrush, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(ring);
        }
        body.Children.Add(actions);
        return Ui.Panel(title, sub, null, body, rise: false);
    }

    /// <summary>The dial, the progress bar and the live charts: in place, never a rebuild.</summary>
    private void OnProgress()
    {
        var s = Session;
        _gauge?.Set(s.Busy ? s.LiveBps : 0, s.Phase == TestPhase.Upload);
        if (_progressFill is not null && _progressTrack is not null)
        {
            var f = s.Target > 0 ? Math.Min(1, s.Transferred / (double)s.Target) : 0;
            _progressFill.Width = Math.Max(0, _progressTrack.ActualWidth * f);
        }
        if (_progressText is not null)
            _progressText.Text = s.Busy && s.Target > 0 ? $"{Format.Bytes(s.Transferred)} of {Format.Bytes(s.Target)} transferred" : " ";
        if (s.Busy)
        {
            if (s.Phase == TestPhase.Download) _downChart?.Set([.. s.DownSeries], null);
            if (s.Phase == TestPhase.Upload) _upChart?.Set([.. s.UpSeries], null);
        }
    }

    /* ------------------------------------------------------------ Notes */

    private static IEnumerable<UIElement> Notes(SpeedTestSession session)
    {
        if (session.Phase == TestPhase.Error)
            yield return Parts.Note("The test could not finish.",
                $"{session.Error} This needs a working internet connection and access to `speed.cloudflare.com`; a VPN, a proxy or a firewall can also refuse it.", warn: true);
        if (session.Phase != TestPhase.Done || session.Result is not { } r) yield break;
        if (r.Bufferbloat is { } bloat && bloat >= 60)
            yield return Parts.Note($"Latency rose {bloat:0} ms while the link was busy.",
                "That is bufferbloat: something between here and Cloudflare - usually the router, sometimes the ISP - queues packets instead of dropping them when it runs out of capacity. It is why a call breaks up when someone starts a download, and it is invisible to a latency test on an idle connection.");
        if (r.TooShort)
            yield return Parts.Note("That run was too short to be reliable.",
                $"The download lasted {r.DownSeconds:0.0}s, not long enough to reach a steady state, so the figure reflects burst and buffering as much as throughput. Try a larger test size for a number worth quoting.");
    }

    /* ---------------------------------------------------------- Results */

    private static UIElement[] LatencyCards(SpeedTestResult? r)
    {
        static string Ms(double v, int digits = 0) => v.ToString("F" + digits, CultureInfo.InvariantCulture) + " ms";
        static string Rise(double v) => $"{(v > 0 ? "+" : "")}{v:0} ms";
        static string Pct(LossCount c) => c.Fraction is not { } f ? "-" : f == 0 ? "0%" : (f * 100).ToString(f * 100 < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";
        static string Line(string label, LossCount c) => $"{label} {Pct(c)} ({c.Lost} of {c.Sent})";

        var loss = r?.Loss;
        return
        [
            Parts.FigureCard("Latency", r is null ? Parts.Figure("-", null) : Parts.Figure(r.LatencyMs.ToString("0", CultureInfo.InvariantCulture), "ms"),
                [r is null ? null : "idle",
                 r?.LoadedLatencyMs is { } d ? $"download {Ms(d)} ({Rise(d - r.LatencyMs)})" : null,
                 r?.LoadedUpLatencyMs is { } u ? $"upload {Ms(u)} ({Rise(u - r.LatencyMs)})" : null], 120, Parts.Icon("")),
            Parts.FigureCard("Jitter", r is null ? Parts.Figure("-", null) : Parts.Figure(r.JitterMs.ToString("0.0", CultureInfo.InvariantCulture), "ms"),
                [r is null ? null : "idle",
                 r?.LoadedJitterMs is { } dj ? $"download {Ms(dj, 1)}" : null,
                 r?.LoadedUpJitterMs is { } uj ? $"upload {Ms(uj, 1)}" : null], 180, Parts.Icon("")),
            Parts.FigureCard("Packet loss", Parts.Figure(loss is not null ? Pct(loss.Total) : r is not null ? "n/a" : "-", null),
                [loss is not null ? Line("idle", loss.Idle) : r is not null ? "pings got no answer at all" : null,
                 loss is not null ? Line("download", loss.Down) : null,
                 loss is not null ? Line("upload", loss.Up) : null], 240, Parts.Icon("")),
        ];
    }

    private static Border ConnectionPanel(ConnectionMeta meta)
    {
        static StackPanel Column(string label, string value, string foot, bool mono = false)
        {
            var stack = new StackPanel();
            stack.Children.Add(Parts.StatLabel(label));
            var v = mono ? Ui.Mono(value, 15, Palette.TextBrush) : Ui.Text(value, 16, 500, Palette.TextBrush, wrap: true);
            v.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(v);
            var f = Ui.Text(foot, 14, 400, Palette.TextFaintBrush, wrap: true);
            f.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(f);
            return stack;
        }
        var body = new StackPanel();
        var grid = Parts.Grid(240,
            Column("Your network", meta.AsOrganization ?? "unknown", $"AS{meta.Asn} · {meta.ClientPlace}"),
            Column("Public address", meta.ClientIp ?? "unknown", "as seen from outside your router", mono: true),
            Column("Serving datacentre", $"{meta.Colo?.City} ({meta.Colo?.Iata})", $"{meta.Colo?.Region} · over {meta.HttpProtocol}"));
        grid.Margin = new Thickness(0);
        body.Children.Add(grid);
        var foot = Ui.Text("The datacentre is whichever one Cloudflare routes you to, so a result says as much about the path to it as about your line. A distant one is itself a finding.",
            14, 400, Palette.TextFaintBrush, wrap: true);
        foot.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(foot);
        return Ui.Panel("Connection", "Both ends of what is being measured, from speed.cloudflare.com/meta.", null, body, rise: false);
    }

    private Border RunsPanel(TestData data)
    {
        var table = new DataTable([
            new("When"), new("Size", Left: true), new("Download"), new("Upload"),
            new("Latency", ("Latency", "Idle / during download / during upload, each in its fixed place; a dash for a figure the run does not have.")),
            new("Loss"), new("Ran for"),
            new("Meter saw", ("Meter saw", "The fastest second the meter logged at the adapter during the run: a completely separate measurement. A dash until every minute the run touched has been written (the meter writes once a minute), or when the meter was not running.")),
            new("Data spent"),
        ]) { MinWidth = 1080, RowPadding = 11 };

        foreach (var (r, seen) in data.Runs)
        {
            var size = r.Profile + (r.Connections is { } c ? $" · {(c == 1 ? "single" : "×" + c)}" : "") + (r.Device is { } d ? $" · {(d == "phone" ? "phone" : "other device")}" : "");
            var latency = r.LoadedLatencyMs is null && r.LoadedUpLatencyMs is null
                ? $"{r.LatencyMs:0} ms"
                : $"{r.LatencyMs:0} / {Dash(r.LoadedLatencyMs)} / {Dash(r.LoadedUpLatencyMs)} ms";
            var loss = r.Loss is { } l ? Percent(l.Total) : "—";
            var ran = DataTable.Cell($"{r.DownSeconds:0.0}s{(r.TooShort ? " ⚠" : "")}", r.TooShort ? Palette.WarnBrush : Palette.TextMutedBrush);
            if (r.TooShort) Ui.SetTip(ran, "Too short to be reliable");
            var lossCell = DataTable.Cell(loss, Palette.TextMutedBrush);
            if (r.Loss is { } ll) Ui.SetTip(lossCell, $"idle {Percent(ll.Idle)} · download {Percent(ll.Down)} · upload {Percent(ll.Up)} · {ll.Total.Lost} of {ll.Total.Sent} pings lost");
            var seenCell = DataTable.Cell(r.Device is not null || seen is null ? "—" : Format.Rate(seen.Value, data.Units), Palette.TextMutedBrush);
            if (r.Device is not null) Ui.SetTip(seenCell, "Run on another device; the meter only sees this PC");
            table.AddRow([
                Parts.MonoCell(Format.Stamp(r.At.ToUniversalTime())),
                DataTable.Cell(size, Palette.TextMutedBrush, numeric: false),
                DataTable.Cell(Format.Rate(r.DownBps, data.Units), Palette.DownBrush, 600),
                DataTable.Cell(Format.Rate(r.UpBps, data.Units), Palette.UpBrush, 600),
                DataTable.Cell(latency, Palette.TextMutedBrush),
                lossCell,
                ran,
                seenCell,
                DataTable.Cell(Format.Bytes(r.TotalBytes), Palette.TextMutedBrush),
            ]);
        }

        var body = new StackPanel();
        body.Children.Add(table.Build());
        if (data.Pages > 1) body.Children.Add(Pager(data.Page, data.Pages));
        var foot = Ui.Paragraph("Results are kept in `speedtests.jsonl` beside the history, one line per run, and survive anything that keeps the history.", 14, Palette.TextFaintBrush);
        foot.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(foot);

        var first = (data.Page - 1) * PageSize + 1;
        var last = Math.Min(data.Page * PageSize, data.Total);
        var aside = Ui.Text($"{first}–{last} of {Format.Count(data.Total)}", 15, 500, Palette.TextMutedBrush, numeric: true);
        return Ui.Panel("Previous runs", "Each run, beside what the meter independently recorded at the adapter.", aside, body, rise: false);
    }

    private static string Dash(double? ms) => ms is { } v ? v.ToString("0", CultureInfo.InvariantCulture) : "–";

    private static string Percent(LossCount c) => c.Fraction is { } f ? (f * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-";

    /// <summary>
    /// Newest and Oldest at the ends, Newer and Older a step each, the page
    /// numbers around this one (Core/View/Pages), and a box that goes straight
    /// to any page. Page 1 is the newest. The sibling apps' pager.
    /// </summary>
    private FrameworkElement Pager(int current, int pages)
    {
        void Go(int page)
        {
            _page = Math.Clamp(page, 1, pages);
            ctx.Redraw();
        }

        var pager = new WrapPanel { HorizontalSpacing = 7, VerticalSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        Button Step(string label, int to, bool enabled, string tip)
        {
            var chip = Ui.Chip(label, false);
            chip.IsEnabled = enabled;
            chip.Click += (_, _) => Go(to);
            Ui.SetTip(chip, tip);
            return chip;
        }
        pager.Children.Add(Step("Newest", 1, current > 1, "First page: the newest runs"));
        pager.Children.Add(Step("← Newer", current - 1, current > 1, "Previous page"));

        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var item in Pages.Items(current, pages))
        {
            if (item is not { } n)
            {
                var gap = Ui.Text("…", 15, 400, Palette.TextFaintBrush, selectable: false);
                gap.VerticalAlignment = VerticalAlignment.Center;
                numbers.Children.Add(gap);
                continue;
            }
            var chip = Ui.Chip(n.ToString(CultureInfo.InvariantCulture), n == current);
            chip.MinWidth = 40;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Page {n}");
            chip.Click += (_, _) => Go(n);
            numbers.Children.Add(chip);
        }
        pager.Children.Add(numbers);
        pager.Children.Add(Step("Older →", current + 1, current < pages, "Next page"));
        pager.Children.Add(Step("Oldest", pages, current < pages, "Last page: the oldest runs"));

        var jump = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 0, 0, 0) };
        var label = Ui.Text("Go to page", 15, 400, Palette.TextMutedBrush, selectable: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        jump.Children.Add(label);
        var box = new NumberBox
        {
            Value = current,
            Minimum = 1,
            Maximum = pages,
            Width = 76,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Page number, 1 to {pages}");
        void Submit()
        {
            if (!double.IsNaN(box.Value)) Go((int)box.Value);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Submit();
        };
        jump.Children.Add(box);
        var of = Ui.Text($"of {pages}", 15, 400, Palette.TextMutedBrush, selectable: false);
        of.VerticalAlignment = VerticalAlignment.Center;
        jump.Children.Add(of);
        var go = Ui.Chip("Go", false, 14);
        go.Click += (_, _) => Submit();
        jump.Children.Add(go);
        pager.Children.Add(jump);
        return pager;
    }
}

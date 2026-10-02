using System.Globalization;
using SpeedMeter.Core;
using SpeedMeter.Core.Demo;
using SpeedMeter.Core.History;
using SpeedMeter.Core.Sampling;
using SpeedMeter.Core.SpeedTest;
using SpeedMeter.Core.View;

// speedmeter: the history from a console, for checking a change without the
// window, scripting an export, or making the demo history the screenshots use.

var command = args.FirstOrDefault()?.ToLowerInvariant();
try
{
    return command switch
    {
        "sample" => Sample(args.Skip(1).ToArray()),
        "stats" => Stats(),
        "export-csv" => Export(args.Skip(1).ToArray()),
        "speedtest" => await SpeedTest(args.Skip(1).ToArray()),
        "demo-data" => Demo(args.Skip(1).ToArray()),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        speedmeter <command>

          sample [seconds] [file]       the adapters, then one line a second of what the meter sees
          stats                         where the history is, and its records
          export-csv <from> <to> [file] [--raw]
                                        history as CSV; dates are local and inclusive (YYYY-MM-DD,
                                        today, yesterday); --raw is one row a second, recent days only
          speedtest [--profile standard|thorough|heavy] [--single] [--save]
                                        a speed test against speed.cloudflare.com; spends real data
          demo-data <folder>            an invented history for screenshots, with its own settings.ini

        The history is wherever settings.ini says (SPEEDMETER_SETTINGS_DIR moves it).
        """);
    return 2;
}

static int Sample(string[] rest)
{
    var seconds = rest.Length > 0 && int.TryParse(rest[0], out var s) && s > 0 ? s : 10;
    var settings = MeterSettings.Load();
    var monitor = new NetMonitor();
    var lines = new List<string> { "adapters:" };
    lines.AddRange(NetMonitor.ListAdapters().Select(a => $"  {a.Label}  [{a.Id}]"));
    lines.Add("selection: " + settings.Adapter);
    lines.Add("");
    foreach (var line in lines) Console.WriteLine(line);
    monitor.Sample(settings.Adapter);
    for (var i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        var r = monitor.Sample(settings.Adapter);
        var line = $"{DateTime.Now:HH:mm:ss}  {Format.Ellipsize(r.SourceName, 22),-22}  down {Format.SpeedLong(r.DownBytesPerSecond, settings.Units),12}  up {Format.SpeedLong(r.UpBytesPerSecond, settings.Units),12}";
        Console.WriteLine(line);
        lines.Add(line);
    }
    if (rest.Length > 1) File.WriteAllLines(rest[1], lines);
    return 0;
}

static int Stats()
{
    var settings = MeterSettings.Load();
    Console.WriteLine($"settings     {AppPaths.SettingsFile}{(File.Exists(AppPaths.SettingsFile) ? "" : " (not written yet: defaults)")}");
    Console.WriteLine($"history      {settings.LogFolder}");
    Console.WriteLine($"raw samples  {(settings.RawFolder.Length > 0 ? settings.RawFolder : "(off)")}, kept {settings.RawRetentionDays} days");
    Console.WriteLine($"recording    {(settings.Record ? "on" : "off")}, units {settings.Units.ToString().ToLowerInvariant()}");
    var days = HistoryStats.AllDays(settings.LogFolder);
    if (days.Count == 0)
    {
        Console.WriteLine("no history yet");
        return 0;
    }
    var (down, up) = HistoryStats.Peaks(days);
    Console.WriteLine($"days         {days.Count}, {Format.DayLong(days[0].Day)} to {Format.DayLong(days[^1].Day)}");
    Console.WriteLine($"recorded     {Format.Duration(TimeSpan.FromSeconds(days.Sum(d => (long)d.Seconds)))}");
    Console.WriteLine($"moved        {Format.Bytes(days.Sum(d => (double)d.DownBytes))} down, {Format.Bytes(days.Sum(d => (double)d.UpBytes))} up");
    Console.WriteLine($"fastest      {Format.Rate(down.BytesPerSecond, settings.Units)} down ({(down.AtUtc is { } a ? Format.Stamp(a) : "-")}), {Format.Rate(up.BytesPerSecond, settings.Units)} up");
    var runs = SpeedTestStore.ReadAll(settings.LogFolder);
    Console.WriteLine($"speed tests  {runs.Count}{(runs.Count > 0 ? $", latest {Format.Stamp(runs[0].At.ToUniversalTime())}: {Format.Rate(runs[0].DownBps, settings.Units)} down" : "")}");
    return 0;
}

static int Export(string[] rest)
{
    var raw = rest.Contains("--raw");
    var positional = rest.Where(a => a != "--raw").ToList();
    if (positional.Count < 2 || !TryDay(positional[0], out var from) || !TryDay(positional[1], out var to)) return Usage();
    if (to < from) (from, to) = (to, from);
    var settings = MeterSettings.Load();
    var source = raw ? LogExport.Source.Raw : LogExport.Source.Minutes;
    if (positional.Count > 2)
    {
        using var writer = new StreamWriter(positional[2], append: false);
        var rows = LogExport.Write(settings, source, from, to, writer);
        Console.WriteLine($"{rows.ToString(CultureInfo.InvariantCulture)} rows written to {positional[2]}");
    }
    else
    {
        LogExport.Write(settings, source, from, to, Console.Out);
    }
    return 0;
}

static bool TryDay(string value, out DateTime day)
{
    if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
    {
        day = DateTime.Now.Date;
        return true;
    }
    if (value.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
    {
        day = DateTime.Now.Date.AddDays(-1);
        return true;
    }
    return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}

static async Task<int> SpeedTest(string[] rest)
{
    var id = rest.SkipWhile(a => a != "--profile").Skip(1).FirstOrDefault() ?? "standard";
    var profile = Profile.All.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"No profile '{id}'.");
    var settings = MeterSettings.Load();
    using var runner = new SpeedTestRunner();
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    var meta = await runner.MetaAsync(cancel.Token);
    Console.WriteLine($"up to {Format.Bytes(profile.TotalBytes)} to and from speed.cloudflare.com{(meta is null ? "" : $" ({meta.ServerPlace}, via {meta.AsOrganization})")}");
    var lastPhase = TestPhase.Idle;
    var result = await runner.RunAsync(profile, !rest.Contains("--single"), meta, p =>
    {
        if (p.Phase == lastPhase) return;
        lastPhase = p.Phase;
        Console.WriteLine($"  {p.Phase.ToString().ToLowerInvariant()}…");
    }, cancel.Token);
    Console.WriteLine($"download  {Format.Rate(result.DownBps, settings.Units)}  (peak {Format.Rate(result.PeakDownBps, settings.Units)}, {Format.Bytes(result.DownBytes)} in {result.DownSeconds:0.0}s)");
    Console.WriteLine($"upload    {Format.Rate(result.UpBps, settings.Units)}  (peak {Format.Rate(result.PeakUpBps ?? 0, settings.Units)}, {Format.Bytes(result.UpBytes)} in {result.UpSeconds:0.0}s)");
    Console.WriteLine($"latency   {result.LatencyMs:0} ms idle, {result.LoadedLatencyMs:0} ms down, {result.LoadedUpLatencyMs:0} ms up; jitter {result.JitterMs:0.0} ms");
    Console.WriteLine($"loss      {(result.Loss is { } l ? $"{l.Total.Lost} of {l.Total.Sent} pings" : "not measured")}");
    if (rest.Contains("--save"))
    {
        SpeedTestStore.Append(settings.LogFolder, result);
        Console.WriteLine("saved to " + SpeedTestStore.PathIn(settings.LogFolder));
    }
    return 0;
}

static int Demo(string[] rest)
{
    if (rest.Length == 0) return Usage();
    var root = DemoData.Create(rest[0]);
    Console.WriteLine(DemoData.Describe(root));
    return 0;
}

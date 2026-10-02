using System.Globalization;
using SpeedMeter.Core.Recording;
using SpeedMeter.Core.SpeedTest;

namespace SpeedMeter.Core.Demo;

/// <summary>
/// An invented history, for screenshots and for trying the app without
/// waiting for one to accumulate. Nothing in it is anyone's traffic.
/// </summary>
/// <remarks>
/// A household line that was upgraded from 50 to 100 Mbps forty days ago, so
/// the daily peak chart shows the kind of step the page exists to reveal; a
/// machine that is on from morning to past midnight; and a few dozen speed
/// tests. Seeded, so two runs produce the same files.
/// </remarks>
public static class DemoData
{
    private const double Mbps = 1_000_000 / 8.0;

    /// <summary>Writes settings.ini, the minute and raw logs and the speed tests under <paramref name="root"/>.</summary>
    public static string Create(string root, int days = 120, int rawDays = 3)
    {
        root = Path.GetFullPath(root);
        var history = Path.Combine(root, "history");
        var raw = Path.Combine(root, "raw");
        if (Directory.Exists(history)) Directory.Delete(history, recursive: true);
        if (Directory.Exists(raw)) Directory.Delete(raw, recursive: true);
        Directory.CreateDirectory(history);
        Directory.CreateDirectory(raw);
        File.WriteAllText(Path.Combine(root, ".speed-meter-demo"), "Invented data for screenshots. Safe to delete.\n");

        // Not recording: a demo run of the app must not add this machine's real traffic to an invented history.
        var settings = new MeterSettings { LogFolder = history, RawFolder = raw, Units = UnitMode.Bits, RawRetentionDays = 0, Record = false, Side = TaskbarSide.Right };
        settings.Save(Path.Combine(root, "settings.ini"));
        File.WriteAllText(Path.Combine(history, LogFormat.AdapterTableName),
            "# index\tid\tname — referenced by adapterIx in the .bin records\n1\t{00000000-0000-4000-8000-000000000001}\tWi-Fi\n2\t{00000000-0000-4000-8000-000000000002}\tEthernet\n");

        var random = new Random(20261002);
        var today = DateTime.Now.Date;
        var nowUtc = DateTime.UtcNow;
        var minuteFiles = new Dictionary<string, FileStream>();
        var rawStart = today.AddDays(-(rawDays - 1));
        var rawRecord = new byte[LogFormat.RawRecordBytes];
        var record = new byte[LogFormat.MinuteRecordBytes];
        // The tests come first, so the minutes they ran in can carry their burst:
        // the Test page puts each run beside what the meter saw, and the two agree.
        var runs = SpeedTests(new Random(20261003), today, days);
        var testMinutes = new Dictionary<long, double>();
        foreach (var run in runs)
        {
            var (first, last) = History.HistoryStats.MinutesOf(run.At, run.DownSeconds + run.UpSeconds);
            for (var m = first; m <= last; m++) testMinutes[m] = run.PeakDownBps;
        }
        try
        {
            for (var d = days - 1; d >= 0; d--)
            {
                var day = today.AddDays(-d);
                var line = (d < 40 ? 100 : 50) * Mbps;
                // Some days the machine stays off: a gap the pages must show as one.
                if (d > 2 && random.NextDouble() < 0.06) continue;
                var start = day.AddHours(7 + random.Next(0, 3));
                var end = day.AddHours(23).AddMinutes(random.Next(0, 120));
                // Two or three big transfers a day, at a fraction of the line.
                var bursts = Enumerable.Range(0, 2 + random.Next(0, 2))
                    .Select(_ => (At: start.AddMinutes(random.Next(0, (int)(end - start).TotalMinutes)), Minutes: 2 + random.Next(0, 12), Share: 0.55 + random.NextDouble() * 0.42))
                    .ToList();

                for (var t = start; t < end; t = t.AddMinutes(1))
                {
                    var utc = t.ToUniversalTime();
                    if (utc >= nowUtc.AddMinutes(-1)) break;
                    var minute = (long)(utc - DateTime.UnixEpoch).TotalMinutes;
                    var burst = bursts.FirstOrDefault(b => t >= b.At && t < b.At.AddMinutes(b.Minutes));
                    var test = testMinutes.GetValueOrDefault(minute);
                    var writeRaw = day >= rawStart;
                    ulong down = 0, up = 0;
                    uint maxDown = 0, maxUp = 0;
                    var active = 0;
                    for (var s = 0; s < 60; s++)
                    {
                        var idle = 2_000 + random.NextDouble() * 30_000;
                        var downRate = test > 0 ? test * (0.9 + random.NextDouble() * 0.1)
                            : burst.Minutes > 0 ? line * burst.Share * (0.85 + random.NextDouble() * 0.15)
                            : idle * (random.NextDouble() < 0.03 ? 40 : 1);
                        var upRate = burst.Minutes > 0 ? downRate * 0.03 : idle * 0.3 * (random.NextDouble() < 0.01 ? 60 : 1);
                        var dn = (uint)downRate;
                        var un = (uint)upRate;
                        down += dn;
                        up += un;
                        maxDown = Math.Max(maxDown, dn);
                        maxUp = Math.Max(maxUp, un);
                        if (dn + (double)un >= settings.ActiveThresholdBps) active++;
                        if (!writeRaw) continue;
                        Array.Clear(rawRecord);
                        LogFormat.WriteU32(rawRecord, 0, (uint)(minute * 60 + s));
                        LogFormat.WriteU32(rawRecord, 4, dn);
                        LogFormat.WriteU32(rawRecord, 8, un);
                        LogFormat.WriteU16(rawRecord, 12, 1000);
                        rawRecord[14] = 1;
                        rawRecord[15] = LogFormat.FlagConnected;
                        Append(minuteFiles, Path.Combine(raw, LogFormat.RawFileName(t)), rawRecord);
                    }

                    Array.Clear(record);
                    LogFormat.WriteU32(record, 0, (uint)minute);
                    LogFormat.WriteU64(record, 4, down);
                    LogFormat.WriteU64(record, 12, up);
                    LogFormat.WriteU32(record, 20, maxDown);
                    LogFormat.WriteU32(record, 24, maxUp);
                    LogFormat.WriteU16(record, 28, 60);
                    LogFormat.WriteU16(record, 30, (ushort)active);
                    record[32] = 1;
                    record[33] = (byte)(LogFormat.FlagConnected | (t == start ? LogFormat.FlagMeterStarted : 0));
                    Append(minuteFiles, Path.Combine(history, LogFormat.MinuteFileName(t)), record);
                }
            }
        }
        finally
        {
            foreach (var stream in minuteFiles.Values) stream.Dispose();
        }

        foreach (var run in runs) SpeedTestStore.Append(history, run);
        return root;
    }

    private static void Append(Dictionary<string, FileStream> open, string path, ReadOnlySpan<byte> bytes)
    {
        if (!open.TryGetValue(path, out var stream))
        {
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            open[path] = stream;
        }
        stream.Write(bytes);
    }

    private static List<SpeedTestResult> SpeedTests(Random random, DateTime today, int days)
    {
        var runs = new List<SpeedTestResult>();
        for (var i = 0; i < 46; i++)
        {
            var d = random.Next(0, days);
            var at = today.AddDays(-d).AddHours(9 + random.Next(0, 13)).AddMinutes(random.Next(0, 60)).ToUniversalTime();
            if (at > DateTime.UtcNow.AddMinutes(-10)) continue;
            var profile = Profile.All[random.Next(0, 3) == 0 ? 1 : 0];
            var single = random.NextDouble() < 0.15;
            var line = (d < 40 ? 100 : 50) * Mbps;
            var downBps = line * (single ? 0.45 + random.NextDouble() * 0.2 : 0.86 + random.NextDouble() * 0.1);
            var upBps = 20 * Mbps * (0.8 + random.NextDouble() * 0.15);
            var downSeconds = Math.Min(20, profile.DownBytes / downBps + 1);
            var upSeconds = Math.Min(20, profile.UpBytes / upBps + 1);
            var latency = 18 + random.NextDouble() * 8;
            runs.Add(new SpeedTestResult
            {
                At = at,
                Profile = profile.Id,
                Connections = single ? 1 : profile.Streams,
                DownBps = downBps,
                UpBps = upBps,
                PeakDownBps = downBps * 1.08,
                PeakUpBps = upBps * 1.1,
                LatencyMs = latency,
                JitterMs = 1 + random.NextDouble() * 3,
                LoadedLatencyMs = latency + 20 + random.NextDouble() * 70,
                LoadedUpLatencyMs = latency + 40 + random.NextDouble() * 110,
                LoadedJitterMs = 3 + random.NextDouble() * 9,
                LoadedUpJitterMs = 5 + random.NextDouble() * 20,
                Loss = new LossResult(new LossCount(30, 0), new LossCount(190, random.Next(0, 4)), new LossCount(110, random.Next(0, 2))),
                DownBytes = Math.Min(profile.DownBytes, downBps * (downSeconds - 0.4)),
                UpBytes = Math.Min(profile.UpBytes, upBps * (upSeconds - 0.4)),
                DownSeconds = downSeconds,
                UpSeconds = upSeconds,
                ClientIsp = "Example Broadband",
                ClientAsn = 64500,
                ClientPlace = "Springfield, Example Region, XX",
                ServerPlace = "Example City (EXA)",
                Protocol = "HTTP/1.1",
            });
        }
        runs.Sort((a, b) => a.At.CompareTo(b.At));
        return runs;
    }

    /// <summary>The settings.ini a demo root carries.</summary>
    public static string SettingsDir(string root) => Path.GetFullPath(root);

    public static string Describe(string root) =>
        string.Format(CultureInfo.InvariantCulture, "Demo history in {0}. Run the app with {1}={0} and {2}={0}\\local.", root, AppPaths.SettingsDirVariable, AppPaths.LocalDirVariable);
}

using SpeedMeter.Core.Recording;

namespace SpeedMeter.Core.History;

/// <summary>A fastest second, and the minute it happened in. <see cref="AtUtc"/> is null when nothing was recorded.</summary>
public readonly record struct Peak(double BytesPerSecond, DateTime? AtUtc)
{
    public static readonly Peak None = new(0, null);
}

/// <summary>
/// One local day of the history: its fastest second each way, what moved, and
/// how many seconds the meter was running for.
/// </summary>
public sealed record DayStats(DateTime Day, Peak Down, Peak Up, ulong DownBytes, ulong UpBytes, int Seconds);

/// <summary>Everything a month file says, summarised. Small enough to keep for every month.</summary>
public sealed record MonthSummary(DateTime Month, IReadOnlyList<DayStats> Days, int Records);

/// <summary>The best rolling average over a window of consecutive seconds.</summary>
public sealed record Sustained(int WindowSeconds, double BytesPerSecond, DateTime? StartUtc);

/// <summary>
/// The day-shaped views over the minute log, and the statistics that need the
/// raw seconds.
/// </summary>
/// <remarks>
/// <para>Everything buckets by LOCAL time, from the stored UTC stamp at read
/// time. Bucketing off UTC would shift every early-morning hour onto the
/// previous day at UTC+6.</para>
/// <para>Past months never change, so a month's summary is kept and the file
/// read again only when its size or time changes - in practice, the current
/// month. The tray process holds a few dozen numbers per month, not the
/// minutes themselves.</para>
/// </remarks>
public static class HistoryStats
{
    private static readonly Dictionary<string, (long Length, DateTime Written, MonthSummary Summary)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();

    public static MonthSummary Summarise(string path, DateTime month)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return new MonthSummary(month, [], 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MonthSummary(month, [], 0);
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc) return hit.Summary;
        }

        var records = LogReader.ReadMonth(path);
        var summary = new MonthSummary(month, ByDay(records), records.Length);
        lock (CacheLock) Cache[path] = (info.Length, info.LastWriteTimeUtc, summary);
        return summary;
    }

    /// <summary>Minute records grouped into local days, oldest first.</summary>
    public static List<DayStats> ByDay(IEnumerable<MinuteRecord> records)
    {
        var days = new SortedDictionary<DateTime, (Peak Down, Peak Up, ulong DownBytes, ulong UpBytes, int Seconds)>();
        foreach (var r in records)
        {
            var key = r.Local.Date;
            days.TryGetValue(key, out var d);
            if (r.MaxDownBps > d.Down.BytesPerSecond || d.Down.AtUtc is null && r.MaxDownBps > 0) d.Down = new Peak(r.MaxDownBps, r.Utc);
            if (r.MaxUpBps > d.Up.BytesPerSecond || d.Up.AtUtc is null && r.MaxUpBps > 0) d.Up = new Peak(r.MaxUpBps, r.Utc);
            d.DownBytes += r.DownBytes;
            d.UpBytes += r.UpBytes;
            d.Seconds += r.Samples;
            days[key] = d;
        }
        return days.Select(p => new DayStats(p.Key, p.Value.Down, p.Value.Up, p.Value.DownBytes, p.Value.UpBytes, p.Value.Seconds)).ToList();
    }

    /// <summary>Every recorded day, oldest first, from the month summaries.</summary>
    public static List<DayStats> AllDays(string folder)
    {
        var days = new List<DayStats>();
        foreach (var (path, month) in LogReader.MonthFiles(folder)) days.AddRange(Summarise(path, month).Days);
        // A clock step can put one local date into two month files; merge, never double-list.
        return days.GroupBy(d => d.Day).Select(Merge).OrderBy(d => d.Day).ToList();
    }

    private static DayStats Merge(IGrouping<DateTime, DayStats> group)
    {
        if (group.Count() == 1) return group.First();
        var down = group.Select(d => d.Down).MaxBy(p => p.BytesPerSecond);
        var up = group.Select(d => d.Up).MaxBy(p => p.BytesPerSecond);
        return new DayStats(group.Key, down, up,
            (ulong)group.Sum(d => (decimal)d.DownBytes), (ulong)group.Sum(d => (decimal)d.UpBytes), group.Sum(d => d.Seconds));
    }

    /// <summary>The fastest second each way over the given days.</summary>
    public static (Peak Down, Peak Up) Peaks(IEnumerable<DayStats> days)
    {
        var down = Peak.None;
        var up = Peak.None;
        foreach (var d in days)
        {
            if (d.Down.BytesPerSecond > down.BytesPerSecond) down = d.Down;
            if (d.Up.BytesPerSecond > up.BytesPerSecond) up = d.Up;
        }
        return (down, up);
    }

    /// <summary>Best sustained download windows: 10 s, 1 min and 5 min.</summary>
    public static readonly int[] SustainedWindows = [10, 60, 300];

    /// <summary>
    /// Best rolling averages over consecutive seconds, in ONE streaming pass.
    /// </summary>
    /// <remarks>
    /// Needs the raw log, so it covers only the retained days. A window resets
    /// at every discontinuity - a break in the second-by-second sequence, or a
    /// flagged sleep gap - because averaging across one would divide real bytes
    /// by a span that was never metered. Returns null when there were no raw
    /// samples at all.
    /// </remarks>
    public static List<Sustained>? SustainedDown(string rawFolder, DateTime fromDay, DateTime toDay)
    {
        if (string.IsNullOrEmpty(rawFolder)) return null;
        var rolling = SustainedWindows.Select(w => new Rolling(w)).ToArray();
        long previous = 0;
        var seen = LogReader.ForEachRaw(rawFolder, fromDay, toDay, (seconds, down, _, elapsedMs, _, flags) =>
        {
            var broken = previous != 0 && seconds - previous > 1;
            previous = seconds;
            if ((flags & LogFormat.FlagGap) != 0 || broken)
            {
                foreach (var r in rolling) r.Reset();
                if ((flags & LogFormat.FlagGap) != 0) return;
            }
            // Rate, not the byte delta: a window is nominally but not exactly a second.
            var span = elapsedMs > 0 ? elapsedMs / 1000.0 : 1;
            foreach (var r in rolling) r.Push(down / span, seconds);
        });
        if (seen == 0) return null;
        return rolling.Select(r => new Sustained(r.Window, r.Best, r.BestStart is { } s ? DateTime.UnixEpoch.AddSeconds(s) : null)).ToList();
    }

    /// <summary>A fixed-length rolling mean. Ring buffers, so every window advances in the one pass.</summary>
    private sealed class Rolling(int window)
    {
        private readonly double[] _values = new double[window];
        private readonly long[] _times = new long[window];
        private double _sum;
        private int _filled;
        private int _head;

        public int Window { get; } = window;
        public double Best { get; private set; }
        public long? BestStart { get; private set; }

        public void Reset()
        {
            _sum = 0;
            _filled = 0;
            _head = 0;
        }

        public void Push(double value, long time)
        {
            if (_filled == Window) _sum -= _values[_head];
            else _filled++;
            _values[_head] = value;
            _times[_head] = time;
            _sum += value;
            _head = (_head + 1) % Window;
            if (_filled != Window) return;
            var average = _sum / Window;
            if (average <= Best) return;
            Best = average;
            // The head now points at the oldest entry: where this window began.
            BestStart = _times[_head];
        }
    }

    /// <summary>
    /// The fastest second the meter recorded at the adapter during a speed test
    /// that ended at <paramref name="endUtc"/> and ran for <paramref name="seconds"/>.
    /// </summary>
    /// <remarks>
    /// Null unless EVERY minute the run touched has been written. The recorder
    /// flushes once a minute, so the minute a test just ran in does not exist
    /// yet, and a window that clipped the tail of the previous minute would
    /// report that minute's unrelated traffic as what the meter saw - it once
    /// read 656 Kbps against a measured 234 Mbps.
    /// </remarks>
    public static double? MeterPeakFor(IReadOnlyDictionary<long, MinuteRecord> minutes, DateTime endUtc, double seconds)
    {
        var (first, last) = MinutesOf(endUtc, seconds);
        double peak = 0;
        for (var m = first; m <= last; m++)
        {
            if (!minutes.TryGetValue(m, out var r)) return null;
            peak = Math.Max(peak, r.MaxDownBps);
        }
        return peak > 0 ? peak : null;
    }

    /// <summary>The UTC minutes a run spanned.</summary>
    public static (long First, long Last) MinutesOf(DateTime endUtc, double seconds)
    {
        var end = (long)Math.Floor((endUtc - DateTime.UnixEpoch).TotalSeconds);
        var start = end - (long)Math.Ceiling(Math.Max(1, seconds));
        return (start / 60, end / 60);
    }
}

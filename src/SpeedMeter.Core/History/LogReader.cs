using System.Globalization;
using System.Text.RegularExpressions;
using SpeedMeter.Core.Recording;

namespace SpeedMeter.Core.History;

/// <summary>One minute of the history.</summary>
public readonly record struct MinuteRecord(
    long UnixMinute, ulong DownBytes, ulong UpBytes, uint MaxDownBps, uint MaxUpBps,
    ushort Samples, ushort ActiveSamples, byte Adapter, byte Flags)
{
    public DateTime Utc => DateTime.UnixEpoch.AddMinutes(UnixMinute);
    public DateTime Local => Utc.ToLocalTime();

    public static MinuteRecord Parse(ReadOnlySpan<byte> b, int at) => new(
        LogFormat.ReadU32(b, at),
        LogFormat.ReadU64(b, at + 4),
        LogFormat.ReadU64(b, at + 12),
        LogFormat.ReadU32(b, at + 20),
        LogFormat.ReadU32(b, at + 24),
        LogFormat.ReadU16(b, at + 28),
        LogFormat.ReadU16(b, at + 30),
        b[at + 32],
        b[at + 33]);
}

/// <summary>
/// Reads the binary logs back. The minute files are the authority for every
/// figure the app shows; the raw files add what minutes cannot answer (a best
/// ten-second average) for as long as they are kept.
/// </summary>
/// <remarks>
/// Records are sorted rather than trusted to be in order: the writer appends
/// in real time, so they normally are, but a backwards clock step breaks that
/// and every chart downstream assumes a monotonic time axis.
/// </remarks>
public static partial class LogReader
{
    [GeneratedRegex(@"^(\d{4})-(\d{2})\.bin$")]
    private static partial Regex MonthFile();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})\.bin$")]
    private static partial Regex DayFile();

    /// <summary>Month files, oldest first, with the local month each covers.</summary>
    public static List<(string Path, DateTime Month)> MonthFiles(string folder)
    {
        var list = new List<(string, DateTime)>();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.bin"))
            {
                var m = MonthFile().Match(Path.GetFileName(path));
                if (!m.Success) continue;
                var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (month is < 1 or > 12) continue;
                list.Add((path, new DateTime(year, month, 1)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder reads as an empty history: the pages say so.
        }
        list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return list;
    }

    /// <summary>Day files of the raw log, oldest first.</summary>
    public static List<(string Path, DateTime Day)> DayFiles(string folder)
    {
        var list = new List<(string, DateTime)>();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.bin"))
            {
                var m = DayFile().Match(Path.GetFileName(path));
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                list.Add((path, day));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return list;
    }

    /// <summary>Every record of one month file, sorted by time.</summary>
    public static MinuteRecord[] ReadMonth(string path)
    {
        var bytes = LogFormat.ReadShared(path);
        if (bytes is null) return [];
        var count = bytes.Length / LogFormat.MinuteRecordBytes;
        var records = new MinuteRecord[count];
        for (var i = 0; i < count; i++) records[i] = MinuteRecord.Parse(bytes, i * LogFormat.MinuteRecordBytes);
        Array.Sort(records, (a, b) => a.UnixMinute.CompareTo(b.UnixMinute));
        return records;
    }

    /// <summary>
    /// Minute records whose LOCAL date falls in <paramref name="fromDay"/>..<paramref name="toDay"/>
    /// inclusive. Month files are picked by name, then trimmed per record: a
    /// file is named for local time, so its first and last records can belong
    /// to the neighbouring month in UTC terms.
    /// </summary>
    public static List<MinuteRecord> ReadMinutes(string folder, DateTime fromDay, DateTime toDay)
    {
        var from = fromDay.Date;
        var until = toDay.Date.AddDays(1);
        var result = new List<MinuteRecord>();
        foreach (var (path, month) in MonthFiles(folder))
        {
            if (month.AddMonths(1) <= from || month > toDay.Date) continue;
            foreach (var r in ReadMonth(path))
            {
                var local = r.Local;
                if (local >= from && local < until) result.Add(r);
            }
        }
        result.Sort((a, b) => a.UnixMinute.CompareTo(b.UnixMinute));
        return result;
    }

    /// <summary>
    /// The minute records at exactly these UTC minutes, where present. Reads
    /// only the month files those minutes can sit in.
    /// </summary>
    public static Dictionary<long, MinuteRecord> MinutesAt(string folder, IEnumerable<long> unixMinutes)
    {
        var wanted = unixMinutes.ToHashSet();
        var found = new Dictionary<long, MinuteRecord>();
        if (wanted.Count == 0) return found;
        // A month file is named for LOCAL time; a minute near a month boundary
        // can sit in the file either side, so both neighbours are candidates.
        var months = new HashSet<DateTime>();
        foreach (var m in wanted)
        {
            var local = DateTime.UnixEpoch.AddMinutes(m).ToLocalTime();
            var first = new DateTime(local.Year, local.Month, 1);
            months.Add(first);
            months.Add(first.AddMonths(-1));
            months.Add(first.AddMonths(1));
        }
        foreach (var (path, month) in MonthFiles(folder))
        {
            if (!months.Contains(month)) continue;
            foreach (var r in ReadMonth(path))
                if (wanted.Contains(r.UnixMinute)) found[r.UnixMinute] = r;
        }
        return found;
    }

    /// <summary>One raw sample, passed by value so a scan allocates nothing per second.</summary>
    public delegate void RawVisitor(long unixSeconds, uint downBytes, uint upBytes, ushort elapsedMs, byte adapter, byte flags);

    /// <summary>
    /// Visits raw samples in time order without holding them all: one day's
    /// file at a time (1.38 MB), so the cost is flat in the size of the window.
    /// A fortnight is 1.2 million samples; turning them into objects first would
    /// cost a hundred megabytes for a handful of aggregates.
    /// </summary>
    public static int ForEachRaw(string folder, DateTime fromDay, DateTime toDay, RawVisitor visit)
    {
        var from = fromDay.Date;
        var until = toDay.Date.AddDays(1);
        var seen = 0;
        foreach (var (path, day) in DayFiles(folder))
        {
            if (day < from.AddDays(-1) || day >= until.AddDays(1)) continue;
            var bytes = LogFormat.ReadShared(path);
            if (bytes is null) continue;
            var count = bytes.Length / LogFormat.RawRecordBytes;
            if (count == 0) continue;
            // Only the order is held - one index array for the day - and files are
            // walked oldest first, so the whole window comes out in order.
            var keys = new long[count];
            var order = new int[count];
            for (var i = 0; i < count; i++)
            {
                keys[i] = LogFormat.ReadU32(bytes, i * LogFormat.RawRecordBytes);
                order[i] = i;
            }
            Array.Sort(keys, order);
            for (var i = 0; i < count; i++)
            {
                var at = order[i] * LogFormat.RawRecordBytes;
                var seconds = keys[i];
                var local = DateTime.UnixEpoch.AddSeconds(seconds).ToLocalTime();
                if (local < from || local >= until) continue;
                seen++;
                visit(seconds,
                    LogFormat.ReadU32(bytes, at + 4),
                    LogFormat.ReadU32(bytes, at + 8),
                    LogFormat.ReadU16(bytes, at + 12),
                    bytes[at + 14],
                    bytes[at + 15]);
            }
        }
        return seen;
    }
}

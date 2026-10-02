using System.Globalization;
using SpeedMeter.Core.Recording;

namespace SpeedMeter.Core.History;

/// <summary>
/// The binary logs as CSV, so the recording is useful without this app: a
/// spreadsheet can open it. Rows are streamed, never collected - a year of
/// minutes is over half a million of them.
/// </summary>
public static class LogExport
{
    public enum Source
    {
        Minutes,
        Raw,
    }

    /// <summary>Writes the records in <paramref name="from"/>..<paramref name="to"/> (local dates, inclusive). Returns the data rows written.</summary>
    public static int Write(MeterSettings settings, Source source, DateTime from, DateTime to, TextWriter writer)
    {
        var folder = source == Source.Raw ? settings.RawFolder : settings.LogFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"{(source == Source.Raw ? "Raw folder" : "Log folder")} not found: {(string.IsNullOrEmpty(folder) ? "(not set)" : folder)}");

        var names = AdapterTable.Names(settings.LogFolder);
        writer.WriteLine(source == Source.Raw
            ? "local_time,utc_time,down_bytes,up_bytes,elapsed_ms,adapter,connected,gap,adapter_changed"
            : "local_time,utc_time,down_bytes,up_bytes,max_down_bps,max_up_bps,samples,active_samples,adapter,connected,gap,adapter_changed,meter_started");

        var rows = 0;
        if (source == Source.Raw)
        {
            LogReader.ForEachRaw(folder, from, to, (seconds, down, up, elapsedMs, adapter, flags) =>
            {
                var utc = DateTime.UnixEpoch.AddSeconds(seconds);
                writer.WriteLine(string.Join(',',
                    Stamp(utc.ToLocalTime()), Stamp(utc),
                    Num(down), Num(up), Num(elapsedMs),
                    Csv(Name(names, adapter)), Flags(flags)));
                rows++;
            });
            return rows;
        }

        foreach (var r in LogReader.ReadMinutes(folder, from, to))
        {
            writer.WriteLine(string.Join(',',
                Stamp(r.Local), Stamp(r.Utc),
                Num(r.DownBytes), Num(r.UpBytes), Num(r.MaxDownBps), Num(r.MaxUpBps),
                Num(r.Samples), Num(r.ActiveSamples),
                Csv(Name(names, r.Adapter)), Flags(r.Flags),
                (r.Flags & LogFormat.FlagMeterStarted) != 0 ? "1" : "0"));
            rows++;
        }
        return rows;
    }

    /// <summary>Flags as three columns, not a bitfield: nothing in a spreadsheet wants to mask bits.</summary>
    private static string Flags(int flags) =>
        $"{((flags & LogFormat.FlagConnected) != 0 ? 1 : 0)},{((flags & LogFormat.FlagGap) != 0 ? 1 : 0)},{((flags & LogFormat.FlagAdapterChanged) != 0 ? 1 : 0)}";

    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Num<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);

    private static string Name(Dictionary<byte, string> names, byte index) =>
        index == LogFormat.AdapterNone ? "none" : names.TryGetValue(index, out var name) ? name : "#" + index.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}

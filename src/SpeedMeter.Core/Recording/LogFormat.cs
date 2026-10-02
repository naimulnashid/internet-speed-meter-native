using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SpeedMeter.Core.Recording;

/// <summary>
/// The binary history format: written by <see cref="Recorder"/>, read by
/// <see cref="History.LogReader"/>. It is the C# meter's format byte for byte,
/// which is what lets this app carry on that meter's history:
/// </summary>
/// <remarks>
/// <code>
/// raw     raw/YYYY-MM-DD.bin   16 bytes   u32 unixSeconds, u32 down, u32 up, u16 elapsedMs, u8 adapter, u8 flags
/// minute  history/YYYY-MM.bin  40 bytes   u32 unixMinute, u64 down, u64 up, u32 maxDownBps, u32 maxUpBps,
///                                         u16 samples, u16 activeSamples, u8 adapter, u8 flags, 6 reserved
/// </code>
/// <para>Little-endian, stated rather than inherited from whatever runs the
/// writer. Records are fixed-width and append-only: readers seek, they do not
/// parse, and the CSV export and every query depend on that.</para>
/// <para>File names are LOCAL dates; the stamps inside are UTC.</para>
/// </remarks>
public static class LogFormat
{
    public const int RawRecordBytes = 16;
    public const int MinuteRecordBytes = 40;

    public const int FlagConnected = 1;
    public const int FlagGap = 2;
    public const int FlagAdapterChanged = 4;

    /// <summary>
    /// Minute records only: the meter started partway through this minute, so
    /// it holds fewer than 60 samples for a reason that says nothing about how
    /// well it is sampling. Without it a restart inside one minute is
    /// indistinguishable from dropped ticks.
    /// </summary>
    public const int FlagMeterStarted = 8;

    /// <summary>Adapter index 0 is reserved for "nothing was being metered".</summary>
    public const byte AdapterNone = 0;

    public const string AdapterTableName = "adapters.tsv";

    /// <summary>Speed-test results, one JSON object per line, beside the minute files.</summary>
    public const string SpeedTestsName = "speedtests.jsonl";

    public static string MinuteFileName(DateTime local) => local.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".bin";

    public static string RawFileName(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".bin";

    public static void WriteU16(Span<byte> b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b[at..], v);
    public static void WriteU32(Span<byte> b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[at..], v);
    public static void WriteU64(Span<byte> b, int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[at..], v);
    public static ushort ReadU16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
    public static uint ReadU32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    public static ulong ReadU64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);

    /// <summary>
    /// Reads a whole file while its writer may still be appending to it. A
    /// record half-written at the moment of the read is cut off by the caller
    /// (only whole records are read), never mis-parsed.
    /// </summary>
    public static byte[]? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[stream.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One row of adapters.tsv: the byte stored in every record, and what it stands for.</summary>
public sealed record AdapterRow(byte Index, string Id, string Name)
{
    public string Label => string.IsNullOrEmpty(Name) ? Id : Name;
}

/// <summary>
/// adapters.tsv, beside the minute files. It lives with the minutes rather than
/// the raw files: both record kinds reference it, and the raw folder's prune
/// must never be able to orphan the names of a history that goes back years.
/// </summary>
public static class AdapterTable
{
    /// <summary>Parses the table. Throws on I/O failure; what to do about that differs by caller.</summary>
    public static List<AdapterRow> Read(string folder)
    {
        var rows = new List<AdapterRow>();
        var path = Path.Combine(folder, LogFormat.AdapterTableName);
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == LogFormat.AdapterNone) continue;
            rows.Add(new AdapterRow(index, parts[1], parts.Length > 2 ? parts[2] : ""));
        }
        return rows;
    }

    /// <summary>Index to a display name, or an empty map when the table cannot be read.</summary>
    public static Dictionary<byte, string> Names(string folder)
    {
        try
        {
            return Read(folder).GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.Last().Label);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the table every number still reads; only labels degrade.
            return [];
        }
    }

    public static void Append(string folder, byte index, string id, string name)
    {
        var path = Path.Combine(folder, LogFormat.AdapterTableName);
        var fresh = !File.Exists(path);
        // UTF-8 without a BOM, stated rather than defaulted: adapter names carry
        // whatever the user renamed them to.
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
        if (fresh) writer.WriteLine("# index\tid\tname — referenced by adapterIx in the .bin records");
        writer.WriteLine($"{index.ToString(CultureInfo.InvariantCulture)}\t{id}\t{name.Replace('\t', ' ')}");
    }
}

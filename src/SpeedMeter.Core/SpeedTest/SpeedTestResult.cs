using System.Text.Json;
using System.Text.Json.Serialization;
using SpeedMeter.Core.Recording;

namespace SpeedMeter.Core.SpeedTest;

/// <summary>Echoes sent and lost in one phase. Counts rather than a percentage, so a reader can see how much a figure rests on.</summary>
public sealed record LossCount(int Sent, int Lost)
{
    [JsonIgnore]
    public double? Fraction => Sent == 0 ? null : (double)Lost / Sent;
}

public sealed record LossResult(LossCount Idle, LossCount Down, LossCount Up)
{
    [JsonIgnore]
    public LossCount Total => new(Idle.Sent + Down.Sent + Up.Sent, Idle.Lost + Down.Lost + Up.Lost);
}

/// <summary>
/// One saved speed test. Rates are bytes per second, like every other rate here.
/// </summary>
/// <remarks>
/// The web dashboard's format, field for field, so its results and this app's
/// sit in one file and read the same. Optional fields are absent from runs
/// recorded before they existed, and a reader that cannot name an old field
/// value (the "light" profile, a run from a phone) still keeps the run.
/// </remarks>
public sealed record SpeedTestResult
{
    /// <summary>When the run finished, UTC.</summary>
    public required DateTime At { get; init; }

    /// <summary>standard, thorough or heavy; "light" in runs from before it was retired.</summary>
    public required string Profile { get; init; }

    /// <summary>Parallel transfers used. 1 is the single-connection measurement.</summary>
    public int? Connections { get; init; }

    public string Server { get; init; } = "speed.cloudflare.com";

    public required double DownBps { get; init; }
    public required double UpBps { get; init; }

    /// <summary>The best one-second window of each leg.</summary>
    public required double PeakDownBps { get; init; }
    public double? PeakUpBps { get; init; }

    /// <summary>Median round trip on an idle link, and the median step between round trips.</summary>
    public required double LatencyMs { get; init; }
    public required double JitterMs { get; init; }

    /// <summary>
    /// Median round trip while each leg saturated the link. The rise over idle
    /// is bufferbloat: what predicts a call breaking up while something
    /// downloads, which an idle ping cannot show.
    /// </summary>
    public double? LoadedLatencyMs { get; init; }
    public double? LoadedUpLatencyMs { get; init; }
    public double? LoadedJitterMs { get; init; }
    public double? LoadedUpJitterMs { get; init; }

    /// <summary>ICMP echoes per phase. Absent when the pinger could not run or ICMP was blocked outright.</summary>
    public LossResult? Loss { get; init; }

    /// <summary>"phone" or "other" for runs the web dashboard took on another device; absent for this PC.</summary>
    public string? Device { get; init; }

    /// <summary>What the run actually cost, which is the point of showing it.</summary>
    public required double DownBytes { get; init; }
    public required double UpBytes { get; init; }
    public required double DownSeconds { get; init; }
    public required double UpSeconds { get; init; }

    /// <summary>Who was at each end: an ISP change or a different datacentre explains a different week.</summary>
    public string? ClientIsp { get; init; }
    public long? ClientAsn { get; init; }
    public string? ClientPlace { get; init; }
    public string? ServerPlace { get; init; }
    public string? Protocol { get; init; }

    [JsonIgnore]
    public double TotalBytes => DownBytes + UpBytes;

    /// <summary>The download lasted too short a time to reach a steady state.</summary>
    [JsonIgnore]
    public bool TooShort => DownSeconds < 3;

    /// <summary>The largest rise of loaded latency over idle, when the run has it.</summary>
    [JsonIgnore]
    public double? Bufferbloat => LoadedLatencyMs is null && LoadedUpLatencyMs is null
        ? null
        : Math.Max(LoadedLatencyMs ?? LatencyMs, LoadedUpLatencyMs ?? LatencyMs) - LatencyMs;
}

/// <summary>
/// speedtests.jsonl, beside the minute log.
/// </summary>
/// <remarks>
/// <para>ORIGINAL data: unlike anything derived from the .bin files, a test
/// that ran at 21:40 cannot be run again at 21:40. So it lives with the
/// history, in whatever folder that is kept in, and is as worth surviving a
/// reset as the history is.</para>
/// <para>Append-only JSON lines rather than a database: that folder may well
/// be cloud-synced, where a database in WAL mode is several files that must
/// agree to restore. A line per test has no such problem and reads in a text
/// editor.</para>
/// </remarks>
public static class SpeedTestStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string PathIn(string logFolder) => Path.Combine(logFolder, LogFormat.SpeedTestsName);

    /// <summary>Every saved run, newest first. One corrupt line - a half-written append, a hand edit - costs that line only.</summary>
    public static List<SpeedTestResult> ReadAll(string logFolder)
    {
        var results = new List<SpeedTestResult>();
        if (string.IsNullOrEmpty(logFolder)) return results;
        string text;
        try
        {
            using var stream = new FileStream(PathIn(logFolder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No file yet is the normal state before the first test.
            return results;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<SpeedTestResult>(trimmed, Json) is { } r) results.Add(r);
            }
            catch (JsonException)
            {
            }
        }
        results.Sort((a, b) => b.At.CompareTo(a.At));
        return results;
    }

    /// <summary>Appends one run. Throws on failure, so the caller can say the test ran but did not save.</summary>
    public static void Append(string logFolder, SpeedTestResult result)
    {
        if (string.IsNullOrEmpty(logFolder)) throw new InvalidOperationException("No log folder is configured.");
        Directory.CreateDirectory(logFolder);
        var line = JsonSerializer.Serialize(result, Json) + "\n";
        using var stream = new FileStream(PathIn(logFolder), FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
    }
}

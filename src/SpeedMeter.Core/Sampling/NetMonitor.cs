using System.Diagnostics;
using System.Net.NetworkInformation;
using SpeedMeter.Core.View;

namespace SpeedMeter.Core.Sampling;

public sealed record AdapterInfo(string Id, string Name, string Description)
{
    public string Label => string.IsNullOrEmpty(Description) || Description == Name
        ? Name
        : Name + "  —  " + Format.Ellipsize(Description, 34);
}

/// <summary>One polling result: rates plus the raw byte deltas for the sampled window.</summary>
public sealed class Reading
{
    public double DownBytesPerSecond { get; init; }
    public double UpBytesPerSecond { get; init; }
    public long DownBytes { get; init; }
    public long UpBytes { get; init; }
    public string SourceName { get; init; } = "No active adapter";
    public bool Connected { get; init; }

    /// <summary>
    /// How long the window actually was. Nominally one second, but a late tick
    /// or a resume from sleep stretches it, and the recorder needs to know: the
    /// rate stays honest because it is divided by this, while the byte delta
    /// covers the whole stretched window and must not be filed against a
    /// single second.
    /// </summary>
    public double ElapsedSeconds { get; init; } = 1.0;

    /// <summary>
    /// Stable identity of whatever was metered: an adapter id,
    /// <see cref="MeterSettings.AdapterAll"/> for the summed mode, or null when
    /// nothing was. <see cref="SourceName"/> is a label and changes when an
    /// adapter is renamed; this does not, so history keys on it.
    /// </summary>
    public string? SourceId { get; init; }

    public static readonly Reading Idle = new();
}

/// <summary>
/// Polls per-interface byte counters through System.Net.NetworkInformation,
/// which sits on the IP Helper API. Deltas are divided by the measured elapsed
/// time rather than the nominal interval, so a late tick (or a machine waking
/// from sleep) cannot inflate the reported speed.
/// </summary>
public sealed class NetMonitor
{
    private sealed class Counter
    {
        public long Received;
        public long Sent;
        public double Activity;
    }

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastSampleSeconds;
    private bool _primed;
    private string? _autoPick;

    /// <summary>Physical, connected interfaces worth metering.</summary>
    public static List<AdapterInfo> ListAdapters()
    {
        var list = new List<AdapterInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (IsCandidate(nic)) list.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description));
        }
        catch (NetworkInformationException)
        {
            // A transient IP Helper failure: report whatever was collected.
        }
        return list;
    }

    private static bool IsCandidate(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        var description = nic.Description ?? "";
        return description.IndexOf("Pseudo-Interface", StringComparison.OrdinalIgnoreCase) < 0
            && description.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>
    /// Samples every candidate adapter and returns the traffic for the selection
    /// in <paramref name="adapterMode"/>: "auto", "all", or an adapter id.
    /// </summary>
    public Reading Sample(string adapterMode)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var elapsed = Math.Max(0.05, now - _lastSampleSeconds);
        _lastSampleSeconds = now;

        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return Reading.Idle;
        }

        var deltas = new Dictionary<string, (long Down, long Up)>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string? bestId = null;
        var bestActivity = -1.0;

        foreach (var nic in nics)
        {
            if (!IsCandidate(nic)) continue;

            long received, sent;
            try
            {
                var stats = nic.GetIPStatistics();
                received = stats.BytesReceived;
                sent = stats.BytesSent;
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                continue;
            }

            var isNew = !_counters.TryGetValue(nic.Id, out var counter);
            if (counter is null)
            {
                counter = new Counter();
                _counters[nic.Id] = counter;
            }

            // A counter going backwards means the adapter or its driver reset;
            // skip that window instead of reporting a nonsense spike.
            var usable = _primed && !isNew;
            var down = usable && received >= counter.Received ? received - counter.Received : 0;
            var up = usable && sent >= counter.Sent ? sent - counter.Sent : 0;
            counter.Received = received;
            counter.Sent = sent;

            // Smoothed activity drives "auto": the busiest adapter wins, which keeps
            // idle VPN and virtual adapters from stealing the display.
            counter.Activity = counter.Activity * 0.7 + (down + up) * 0.3;
            if (counter.Activity > bestActivity)
            {
                bestActivity = counter.Activity;
                bestId = nic.Id;
            }

            deltas[nic.Id] = (down, up);
            names[nic.Id] = nic.Name;
        }

        foreach (var gone in _counters.Keys.Where(k => !deltas.ContainsKey(k)).ToList()) _counters.Remove(gone);
        _primed = true;

        long selectedDown = 0, selectedUp = 0;
        string? selectedName = null, selectedId = null;

        if (adapterMode == MeterSettings.AdapterAll)
        {
            foreach (var (d, u) in deltas.Values)
            {
                selectedDown += d;
                selectedUp += u;
            }
            if (deltas.Count > 0)
            {
                selectedName = $"All adapters ({deltas.Count})";
                selectedId = MeterSettings.AdapterAll;
            }
        }
        else
        {
            string? wanted;
            if (adapterMode == MeterSettings.AdapterAuto)
            {
                // Hold the previous pick while every adapter is idle, so the label stops flickering.
                if (bestActivity > 0 && bestId is not null) _autoPick = bestId;
                else if (_autoPick is null || !deltas.ContainsKey(_autoPick)) _autoPick = bestId;
                wanted = _autoPick;
            }
            else
            {
                wanted = adapterMode;
            }

            if (wanted is not null && deltas.TryGetValue(wanted, out var delta))
            {
                (selectedDown, selectedUp) = delta;
                selectedName = names[wanted];
                selectedId = wanted;
            }
        }

        return new Reading
        {
            DownBytes = selectedDown,
            UpBytes = selectedUp,
            DownBytesPerSecond = selectedDown / elapsed,
            UpBytesPerSecond = selectedUp / elapsed,
            ElapsedSeconds = elapsed,
            Connected = selectedName is not null,
            SourceName = selectedName ?? "No active adapter",
            SourceId = selectedId,
        };
    }
}

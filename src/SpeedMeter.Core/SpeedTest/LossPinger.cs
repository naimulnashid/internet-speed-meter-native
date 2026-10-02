using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SpeedMeter.Core.SpeedTest;

public enum LossPhase
{
    Idle,
    Down,
    Up,
}

/// <summary>
/// Packet loss, measured with ICMP while a test runs.
/// </summary>
/// <remarks>
/// <para>The transfers cannot show it. Everything they send is TCP, and TCP
/// repairs a lost packet before anything above it hears of it: loss only
/// surfaces as a slower transfer. A ping is not repaired, so a ping that never
/// comes back is a packet that was lost.</para>
/// <para>Ten echoes a second, each in flight on its own so a lost one does not
/// stall the ones behind it: Windows' ping.exe sends one a second, and over a
/// ten-second leg one loss would read as 10%. Each echo counts against the
/// phase that was active when it was SENT.</para>
/// <para>The web dashboard drove the same .NET Ping class from a PowerShell
/// child; here it runs in-process.</para>
/// </remarks>
public sealed class LossPinger : IAsyncDisposable
{
    public const string Target = "speed.cloudflare.com";
    private const int IntervalMs = 100;
    private const int TimeoutMs = 1000;

    private readonly IPAddress _address;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(LossPhase Phase, Task<bool> Reply)> _sent = [];
    private readonly Lock _lock = new();
    private LossPhase? _phase = LossPhase.Idle;
    private Task? _loop;

    private LossPinger(IPAddress address) => _address = address;

    /// <summary>Resolves the target and starts counting as idle. Null when it cannot run at all.</summary>
    public static async Task<LossPinger?> StartAsync(CancellationToken cancel)
    {
        try
        {
            var all = await Dns.GetHostAddressesAsync(Target, cancel).ConfigureAwait(false);
            var address = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
            if (address is null) return null;
            var pinger = new LossPinger(address);
            pinger._loop = Task.Run(pinger.LoopAsync, CancellationToken.None);
            return pinger;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Moves the count to the phase the test has just entered.</summary>
    public void Mark(LossPhase phase)
    {
        lock (_lock) _phase = phase;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            LossPhase? phase;
            lock (_lock) phase = _phase;
            if (phase is { } p)
            {
                var reply = SendOne();
                lock (_lock) _sent.Add((p, reply));
            }
            try
            {
                await Task.Delay(IntervalMs, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> SendOne()
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(_address, TimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stops counting, waits for the echoes still in flight, and tallies. The
    /// wait matters: a reply arrives in milliseconds, but a loss is only known
    /// once the timeout runs out, so cutting off at once would bias the sample
    /// towards the echoes that came back.
    /// </summary>
    public async Task<LossResult> StopAsync()
    {
        lock (_lock) _phase = null;
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        List<(LossPhase Phase, Task<bool> Reply)> sent;
        lock (_lock) sent = [.. _sent];
        await Task.WhenAny(Task.WhenAll(sent.Select(s => s.Reply)), Task.Delay(TimeoutMs + 500)).ConfigureAwait(false);

        var counts = new Dictionary<LossPhase, (int Sent, int Lost)>();
        foreach (var (phase, reply) in sent)
        {
            counts.TryGetValue(phase, out var c);
            c.Sent++;
            // Never answered at all counts as lost: it outlived its own timeout.
            if (!(reply.IsCompletedSuccessfully && reply.Result)) c.Lost++;
            counts[phase] = c;
        }
        LossCount Of(LossPhase p) => counts.TryGetValue(p, out var c) ? new LossCount(c.Sent, c.Lost) : new LossCount(0, 0);
        return new LossResult(Of(LossPhase.Idle), Of(LossPhase.Down), Of(LossPhase.Up));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }
}

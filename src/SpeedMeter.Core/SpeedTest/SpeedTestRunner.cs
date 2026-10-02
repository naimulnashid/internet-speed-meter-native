using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SpeedMeter.Core.SpeedTest;

/// <summary>A test size: what a run may spend, and over how many connections.</summary>
public sealed record Profile(string Id, string Label, long DownBytes, long UpBytes, int Streams, int UpStreams)
{
    private const long MB = 1024 * 1024;

    /// <summary>
    /// Upload budgets are far smaller than download on purpose: domestic
    /// upstream is a fraction of downstream, and matching the two would make
    /// the upload leg dominate the time and the data bill for no extra accuracy.
    /// Never more than 6 download streams: speed.cloudflare.com speaks HTTP/1.1
    /// only, where a browser opens at most 6 connections to one server, and
    /// keeping that limit keeps results comparable with the web dashboard's.
    /// </summary>
    public static readonly Profile[] All =
    [
        new("standard", "Standard", 100 * MB, 25 * MB, 6, 3),
        new("thorough", "Thorough", 250 * MB, 60 * MB, 6, 4),
        new("heavy", "Heavy", 500 * MB, 120 * MB, 6, 4),
    ];

    public long TotalBytes => DownBytes + UpBytes;
}

public enum TestPhase
{
    Idle,
    Latency,
    Download,
    Upload,
    Saving,
    Done,
    Error,
}

/// <summary>What the dial and the live charts show while a run goes.</summary>
public sealed record TestProgress(TestPhase Phase, double BytesPerSecond, long Transferred, long Target, double ElapsedSeconds, bool ChartPoint);

/// <summary>What speed.cloudflare.com/meta reports about both ends of the connection.</summary>
public sealed record ConnectionMeta
{
    public string? ClientIp { get; init; }
    public long Asn { get; init; }
    public string? AsOrganization { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
    public string? Country { get; init; }
    public string? HttpProtocol { get; init; }
    public Colo? Colo { get; init; }

    [JsonIgnore]
    public string ClientPlace => string.Join(", ", new[] { City, Region, Country }.Where(s => !string.IsNullOrEmpty(s)));

    [JsonIgnore]
    public string ServerPlace => $"{Colo?.City ?? ""} ({Colo?.Iata ?? "?"})";
}

public sealed record Colo
{
    public string? Iata { get; init; }
    public string? City { get; init; }
    public string? Region { get; init; }
}

/// <summary>
/// An active speed test against speed.cloudflare.com: the ONE thing in this
/// app that generates traffic of its own.
/// </summary>
/// <remarks>
/// <para>Everything else observes what was already happening; this makes a
/// connection busy to find out how busy it can get. It costs real data, which
/// is why the budget is chosen by the reader and every screen states what a
/// run will spend before it spends it.</para>
/// <para>The method is the web dashboard's, carried over with its findings:
/// latency probed on its own connection pool so it never queues behind the
/// transfers; the first three idle probes dropped (they pay for DNS, TCP and
/// TLS); jitter as the MEDIAN step between round trips, which one stalled
/// probe cannot wreck; the first second of each leg left out of both the
/// average and the peak; and each leg cut off after 20 seconds, keeping what
/// moved as a valid measurement of the time it covers.</para>
/// </remarks>
public sealed class SpeedTestRunner : IDisposable
{
    public const string Host = "https://speed.cloudflare.com";

    /// <summary>Neither leg may run longer than this, however slow the link.</summary>
    private static readonly TimeSpan LegTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The shortest idle window the pinger counts, so the idle loss rests on ~30 echoes.</summary>
    private static readonly TimeSpan IdlePing = TimeSpan.FromSeconds(3);

    private readonly HttpClient _transfers;
    private readonly HttpClient _probes;

    public SpeedTestRunner()
    {
        // Two handlers, two connection pools. A probe that shares a pool with six
        // saturating downloads queues for a socket for the whole leg, and the
        // median of one quick probe and one 20-second wait is 10 s: the browser
        // version measured exactly that before its probes got their own pool.
        _transfers = new HttpClient(Handler(16)) { Timeout = Timeout.InfiniteTimeSpan };
        _probes = new HttpClient(Handler(2)) { Timeout = TimeSpan.FromSeconds(10) };
        foreach (var client in new[] { _transfers, _probes })
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InternetSpeedMeter", typeof(SpeedTestRunner).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
            // Cloudflare refuses a large __down (403) without the page's own
            // Referer, which a browser on speed.cloudflare.com always sends.
            client.DefaultRequestHeaders.Referrer = new Uri(Host + "/");
        }
    }

    private static SocketsHttpHandler Handler(int connections) => new()
    {
        MaxConnectionsPerServer = connections,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Who is at each end, fetched before anything is spent. Null when it cannot be reached.</summary>
    public async Task<ConnectionMeta?> MetaAsync(CancellationToken cancel)
    {
        try
        {
            return await _probes.GetFromJsonAsync<ConnectionMeta>($"{Host}/meta", SpeedTestStore.Json, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a test. Progress is reported on whatever thread produced it; the
    /// caller marshals. Throws <see cref="OperationCanceledException"/> when
    /// stopped, and <see cref="SpeedTestException"/> when it could not finish.
    /// </summary>
    public async Task<SpeedTestResult> RunAsync(Profile profile, bool parallel, ConnectionMeta? meta, Action<TestProgress> progress, CancellationToken cancel)
    {
        LossPinger? pinger = null;
        try
        {
            // ---- latency, and the idle window for packet loss ---------------
            progress(new TestProgress(TestPhase.Latency, 0, 0, 0, 0, false));
            var pingerStarted = Stopwatch.StartNew();
            var pingerTask = LossPinger.StartAsync(cancel);

            var rtts = new List<double>();
            for (var i = 0; i < 12; i++) rtts.Add(await ProbeAsync(cancel).ConfigureAwait(false));
            var settled = rtts.Skip(3).ToList();
            var latencyMs = Median(settled);
            var jitterMs = Jitter(settled);

            pinger = await pingerTask.ConfigureAwait(false);
            if (pinger is not null)
            {
                var wait = IdlePing - pingerStarted.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel).ConfigureAwait(false);
            }

            // ---- download, with latency probed under load --------------------
            pinger?.Mark(LossPhase.Down);
            var downStreams = parallel ? profile.Streams : 1;
            var (down, downRtts) = await LegAsync(TestPhase.Download, profile.DownBytes, downStreams, progress, cancel).ConfigureAwait(false);

            // ---- upload, probed again: the probe's request queues behind the
            // upload on the way out, so this sees the upstream buffer ----------
            pinger?.Mark(LossPhase.Up);
            var (up, upRtts) = await LegAsync(TestPhase.Upload, profile.UpBytes, parallel ? profile.UpStreams : 1, progress, cancel).ConfigureAwait(false);

            LossResult? loss = null;
            if (pinger is not null)
            {
                var counts = await pinger.StopAsync().ConfigureAwait(false);
                // Not one idle echo answered means ICMP is blocked on the way (a
                // firewall, some VPNs), not that the link drops everything: the
                // probes just crossed it fine. That is no measurement at all.
                if (counts.Idle.Sent > counts.Idle.Lost) loss = counts;
            }

            return new SpeedTestResult
            {
                At = DateTime.UtcNow,
                Profile = profile.Id,
                Connections = downStreams,
                Server = "speed.cloudflare.com",
                DownBps = down.Average,
                UpBps = up.Average,
                PeakDownBps = down.Peak,
                PeakUpBps = up.Peak,
                LatencyMs = latencyMs,
                JitterMs = jitterMs,
                LoadedLatencyMs = downRtts.Count > 0 ? Median(downRtts) : latencyMs,
                LoadedUpLatencyMs = upRtts.Count > 0 ? Median(upRtts) : latencyMs,
                LoadedJitterMs = downRtts.Count > 1 ? Jitter(downRtts) : jitterMs,
                LoadedUpJitterMs = upRtts.Count > 1 ? Jitter(upRtts) : jitterMs,
                Loss = loss,
                DownBytes = down.Transferred,
                UpBytes = up.Transferred,
                DownSeconds = down.Seconds,
                UpSeconds = up.Seconds,
                ClientIsp = meta?.AsOrganization,
                ClientAsn = meta?.Asn,
                ClientPlace = meta?.ClientPlace,
                ServerPlace = meta is null ? null : meta.ServerPlace,
                Protocol = meta?.HttpProtocol,
            };
        }
        catch (HttpRequestException ex)
        {
            throw new SpeedTestException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new SpeedTestException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new SpeedTestException("The server stopped answering.", ex);
        }
        finally
        {
            if (pinger is not null) await pinger.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One round trip: an empty download, timed end to end.</summary>
    private async Task<double> ProbeAsync(CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        using var response = await _probes.GetAsync($"{Host}/__down?bytes=0&r={Random.Shared.NextDouble()}", HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SpeedTestException($"Server answered {(int)response.StatusCode}.");
        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>A transfer leg, with latency probed alongside it until it ends.</summary>
    private async Task<(LegResult Leg, List<double> Rtts)> LegAsync(TestPhase phase, long target, int streams, Action<TestProgress> progress, CancellationToken cancel)
    {
        using var probing = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var rtts = new List<double>();
        var probeLoop = Task.Run(async () =>
        {
            while (!probing.IsCancellationRequested)
            {
                try
                {
                    var rtt = await ProbeAsync(probing.Token).ConfigureAwait(false);
                    lock (rtts) rtts.Add(rtt);
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or SpeedTestException)
                {
                    return;
                }
            }
        }, CancellationToken.None);

        try
        {
            var leg = await TransferAsync(phase, target, streams, progress, cancel).ConfigureAwait(false);
            return (leg, rtts);
        }
        finally
        {
            probing.Cancel();
            await probeLoop.ConfigureAwait(false);
        }
    }

    private sealed record LegResult(double Average, double Peak, double Transferred, double Seconds);

    /// <summary>
    /// Moves <paramref name="target"/> bytes across <paramref name="streams"/>
    /// parallel connections, reporting the rate as it goes.
    /// </summary>
    /// <remarks>
    /// Two figures come back. The average is bytes over wall time with the
    /// first second (TCP opening its window) taken out: the honest headline.
    /// The peak is the best one-second window, also skipping the first second,
    /// which for an upload is not just slow but wrong - progress counts bytes
    /// handed to send buffers, which fill far faster than the line drains them.
    /// </remarks>
    private async Task<LegResult> TransferAsync(TestPhase phase, long target, int streams, Action<TestProgress> progress, CancellationToken cancel)
    {
        var perStream = (long)Math.Ceiling(target / (double)streams);
        long transferred = 0;
        var clock = Stopwatch.StartNew();
        var gate = new Lock();
        var marks = new Queue<(double T, long Bytes)>();
        marks.Enqueue((0, 0));
        double peak = 0;
        long rampBytes = 0;
        double lastReport = -1, lastPoint = -1;

        void Note(long delta)
        {
            lock (gate)
            {
                transferred += delta;
                var elapsed = clock.Elapsed.TotalMilliseconds;
                if (elapsed >= 1000 && rampBytes == 0) rampBytes = transferred;
                marks.Enqueue((elapsed, transferred));
                while (marks.Count > 1 && elapsed - marks.Peek().T > 1000) marks.Dequeue();
                var (t0, b0) = marks.Peek();
                var span = elapsed - t0;
                if (span <= 250) return;
                var rate = (transferred - b0) * 1000.0 / span;
                if (t0 >= 1000 && rate > peak) peak = rate;
                // The dial takes ten updates a second; the chart a point every quarter second.
                if (elapsed - lastReport < 100) return;
                lastReport = elapsed;
                var point = elapsed - lastPoint >= 250;
                if (point) lastPoint = elapsed;
                progress(new TestProgress(phase, rate, transferred, target, elapsed / 1000, point));
            }
        }

        using var leg = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        leg.CancelAfter(LegTimeout);
        progress(new TestProgress(phase, 0, 0, target, 0, false));
        try
        {
            await Task.WhenAll(Enumerable.Range(0, streams).Select(_ => phase == TestPhase.Download
                ? DownloadAsync(perStream, Note, leg.Token)
                : UploadAsync(perStream, Note, leg.Token))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException && leg.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            // The leg timed out: an expected end, not a failure. Whatever moved is
            // a valid measurement of the time it covers.
        }

        cancel.ThrowIfCancellationRequested();
        var seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
        long total;
        lock (gate) total = transferred;
        var average = rampBytes > 0 && seconds > 2 ? (total - rampBytes) / (seconds - 1) : total / seconds;
        return new LegResult(average, peak > 0 ? peak : total / seconds, total, seconds);
    }

    private async Task DownloadAsync(long bytes, Action<long> note, CancellationToken cancel)
    {
        using var response = await _transfers.GetAsync($"{Host}/__down?bytes={bytes}&r={Random.Shared.NextDouble()}", HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SpeedTestException($"Server answered {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0) note(n);
    }

    private async Task UploadAsync(long bytes, Action<long> note, CancellationToken cancel)
    {
        using var content = new NoiseContent(bytes, note);
        using var response = await _transfers.PostAsync($"{Host}/__up?r={Random.Shared.NextDouble()}", content, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SpeedTestException($"Server answered {(int)response.StatusCode}.");
    }

    /// <summary>
    /// An upload body of noise, reporting each block as it is handed to the
    /// connection. Incompressible, so nothing on the way can deflate it and
    /// flatter the result.
    /// </summary>
    private sealed class NoiseContent : HttpContent
    {
        private static readonly byte[] Block = RandomNumberGenerator.GetBytes(64 * 1024);
        private readonly long _length;
        private readonly Action<long> _note;

        public NoiseContent(long length, Action<long> note)
        {
            _length = length;
            _note = note;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
        {
            var left = _length;
            while (left > 0)
            {
                var n = (int)Math.Min(Block.Length, left);
                await stream.WriteAsync(Block.AsMemory(0, n), cancel).ConfigureAwait(false);
                left -= n;
                _note(n);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// The MEDIAN of the steps between consecutive round trips. A mean is
    /// destroyed by one outlier: a single stalled probe among nine once put the
    /// figure at 158 ms on a link whose round trips were 27-36 ms apart.
    /// </summary>
    public static double Jitter(IReadOnlyList<double> rtts) =>
        rtts.Count > 1 ? Median(rtts.Skip(1).Select((v, i) => Math.Abs(v - rtts[i])).ToList()) : 0;

    public void Dispose()
    {
        _transfers.Dispose();
        _probes.Dispose();
    }
}

public sealed class SpeedTestException(string message, Exception? inner = null) : Exception(message, inner);

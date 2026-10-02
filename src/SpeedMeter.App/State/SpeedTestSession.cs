using Microsoft.UI.Dispatching;
using SpeedMeter.App.Charts;
using SpeedMeter.Core.SpeedTest;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.State;

/// <summary>
/// The speed test, held by the app rather than the page: navigating away and
/// back keeps a run's live state, and closing the window does not stop a run -
/// it finishes, saves, and says so in a notification.
/// </summary>
/// <remarks>Everything here is read and raised on the window's thread.</remarks>
public sealed class SpeedTestSession
{
    private readonly DispatcherQueue _ui;
    private readonly AppState _state;
    private SpeedTestRunner? _runner;
    private CancellationTokenSource? _cancel;
    private bool _metaRequested;

    internal SpeedTestSession(DispatcherQueue ui, AppState state)
    {
        _ui = ui;
        _state = state;
        Profile = Core.SpeedTest.Profile.All.FirstOrDefault(p => p.Id == state.Ui.TestProfile) ?? Core.SpeedTest.Profile.All[0];
        Parallel = state.Ui.TestParallel;
    }

    public Profile Profile { get; private set; }

    public bool Parallel { get; private set; }

    public TestPhase Phase { get; private set; } = TestPhase.Idle;

    public bool Busy => Phase is TestPhase.Latency or TestPhase.Download or TestPhase.Upload or TestPhase.Saving;

    /// <summary>The dial: the live rate, and how much of the leg's budget has moved.</summary>
    public double LiveBps { get; private set; }

    public long Transferred { get; private set; }

    public long Target { get; private set; }

    public List<SpeedPoint> DownSeries { get; } = [];

    public List<SpeedPoint> UpSeries { get; } = [];

    /// <summary>The finished run, shown until the next one starts.</summary>
    public SpeedTestResult? Result { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Both ends of the path, fetched once before anything is spent.</summary>
    public ConnectionMeta? Meta { get; private set; }

    /// <summary>Phase changes and finished runs: the page rebuilds its result cards.</summary>
    public event Action? Changed;

    /// <summary>Live samples, ten a second: the page updates the dial and charts in place.</summary>
    public event Action? Progress;

    public void SetProfile(Profile profile)
    {
        if (Busy) return;
        Profile = profile;
        _state.Ui.TestProfile = profile.Id;
        _state.Ui.Save();
        Changed?.Invoke();
    }

    public void SetParallel(bool parallel)
    {
        if (Busy) return;
        Parallel = parallel;
        _state.Ui.TestParallel = parallel;
        _state.Ui.Save();
        Changed?.Invoke();
    }

    /// <summary>Fetches who is at each end, once, without spending anything to find out.</summary>
    public async void LoadMeta()
    {
        if (_metaRequested) return;
        _metaRequested = true;
        // Development only: screenshots must never show this machine's real address.
        if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_FAKE_META") == "1")
        {
            Meta = new ConnectionMeta
            {
                ClientIp = "203.0.113.24",
                Asn = 64500,
                AsOrganization = "Example Broadband",
                City = "Springfield",
                Region = "Example Region",
                Country = "XX",
                HttpProtocol = "HTTP/1.1",
                Colo = new Colo { Iata = "EXA", City = "Example City", Region = "Example Region" },
            };
            return;
        }
        _runner ??= new SpeedTestRunner();
        var meta = await _runner.MetaAsync(CancellationToken.None);
        _ui.TryEnqueue(() =>
        {
            Meta = meta;
            if (meta is null) _metaRequested = false;
            Changed?.Invoke();
        });
    }

    public void Stop()
    {
        _cancel?.Cancel();
    }

    public async void Start()
    {
        if (Busy) return;
        _runner ??= new SpeedTestRunner();
        _cancel = new CancellationTokenSource();
        var cancel = _cancel.Token;
        Error = null;
        Result = null;
        DownSeries.Clear();
        UpSeries.Clear();
        LiveBps = 0;
        Transferred = Target = 0;
        Enter(TestPhase.Latency);

        var logFolder = _state.Meter.LogFolder;
        var profile = Profile;
        // Development only: a tiny budget, to exercise a whole run without spending a real one.
        if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_TEST_MB") is { Length: > 0 } mb && int.TryParse(mb, out var budget))
            profile = profile with { DownBytes = budget * 1024L * 1024, UpBytes = budget * 1024L * 1024 / 4 };
        try
        {
            var result = await Task.Run(() => _runner.RunAsync(profile, Parallel, Meta, OnProgress, cancel), cancel);
            Result = result;
            LiveBps = 0;
            Enter(TestPhase.Saving);
            await Task.Run(() => SpeedTestStore.Append(logFolder, result), CancellationToken.None);
            Enter(TestPhase.Done);
            _state.RaiseHistoryChanged();
            if (!_state.WindowVisible && _state.Ui.NotifySpeedTests)
                _state.Notify("Speed test finished",
                    $"↓ {Format.Rate(result.DownBps, _state.Units)}   ↑ {Format.Rate(result.UpBps, _state.Units)}   {result.LatencyMs:0} ms");
        }
        catch (OperationCanceledException)
        {
            LiveBps = 0;
            Enter(TestPhase.Idle);
        }
        catch (Exception ex) when (ex is SpeedTestException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            // A run that measured but could not save says which, so it does not read as a failed test.
            Error = Phase == TestPhase.Saving ? "The test ran, but the result could not be saved: " + ex.Message : ex.Message;
            LiveBps = 0;
            Enter(TestPhase.Error);
            if (!_state.WindowVisible && _state.Ui.NotifySpeedTests) _state.Notify("Speed test could not finish", Error, warning: true);
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    private void Enter(TestPhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    /// <summary>From the runner's threads: copied across to the window's.</summary>
    private void OnProgress(TestProgress p) => _ui.TryEnqueue(() =>
    {
        if (!Busy) return;
        if (p.Phase != Phase)
        {
            Phase = p.Phase;
            LiveBps = 0;
            Changed?.Invoke();
        }
        LiveBps = p.BytesPerSecond;
        Transferred = p.Transferred;
        Target = p.Target;
        if (p.ChartPoint) (p.Phase == TestPhase.Upload ? UpSeries : DownSeries).Add(new SpeedPoint(p.ElapsedSeconds, p.BytesPerSecond));
        Progress?.Invoke();
    });
}

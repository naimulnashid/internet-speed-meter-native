using Microsoft.UI.Dispatching;
using SpeedMeter.App.Meter;
using SpeedMeter.Core;

namespace SpeedMeter.App.State;

/// <summary>
/// What the dashboard window shares, on its thread: the meter's settings,
/// its live reading, the history's heartbeat and the speed test.
/// </summary>
/// <remarks>
/// The meter is another process. Once a second this reads what it publishes
/// (<see cref="MeterLink"/>): the live reading, a count of minutes written
/// (a new one refreshes the page) and a settings version (a new one re-reads
/// settings.ini, after a change made from the tray menu).
/// </remarks>
public sealed class AppState
{
    private readonly DispatcherQueueTimer _poll;
    private int? _minutes;
    private int? _settingsVersion;

    internal AppState(DispatcherQueue ui, UiSettings settings)
    {
        Ui = settings;
        Meter = MeterSettings.Load();
        Test = new SpeedTestSession(ui, this);
        _poll = ui.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(1);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Poll();
    }

    public UiSettings Ui { get; }

    /// <summary>settings.ini as last read: the units, the folders.</summary>
    public MeterSettings Meter { get; private set; }

    public UnitMode Units => Meter.Units;

    public SpeedTestSession Test { get; }

    /// <summary>The meter's latest reading; null while no meter is running.</summary>
    public LiveState? Live { get; private set; }

    /// <summary>Why recording stopped, when it has.</summary>
    public string? RecordingProblem => Live?.Problem;

    /// <summary>A minute was written to the history, or a speed test saved.</summary>
    public event Action? HistoryChanged;

    public event Action? SettingsChanged;

    /// <summary>Every second, with the meter's reading (null when it is not running).</summary>
    public event Action<LiveState?>? LiveReading;

    /// <summary>Whether the window is showing, so notifications are for when it is not.</summary>
    public bool WindowVisible { get; set; }

    private void Poll()
    {
        var live = MeterLink.Read();
        // Exit from the tray menu ends the whole app: the window follows the meter out.
        if (live is null && Live is not null && !Test.Busy) Environment.Exit(0);
        Live = live;
        if (live is not null)
        {
            if (_settingsVersion is { } v && v != live.SettingsVersion) ReloadSettings();
            _settingsVersion = live.SettingsVersion;
            if (_minutes is { } m && m != live.MinutesWritten) HistoryChanged?.Invoke();
            _minutes = live.MinutesWritten;
        }
        LiveReading?.Invoke(live);
    }

    private void ReloadSettings()
    {
        Meter = MeterSettings.Load();
        SettingsChanged?.Invoke();
    }

    /// <summary>Changes settings.ini, and tells the meter to take it up.</summary>
    public void ChangeMeter(Action<MeterSettings> change)
    {
        MeterLink.ChangeSettings(change);
        ReloadSettings();
    }

    /// <summary>A Windows notification, shown by the meter's tray icon.</summary>
    public void Notify(string title, string text, bool warning = false) => MeterLink.Send("notify", title, text, warning ? "warn" : "");

    internal void RaiseHistoryChanged() => HistoryChanged?.Invoke();
}

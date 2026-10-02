using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using SpeedMeter.App.State;
using SpeedMeter.Core;
using SpeedMeter.Core.Recording;
using SpeedMeter.Core.Sampling;
using SpeedMeter.Core.View;
using Timer = System.Windows.Forms.Timer;

namespace SpeedMeter.App.Meter;

/// <summary>
/// The meter: the once-a-second sampling loop, the recorder, the tray icon,
/// the taskbar text, the details flyout and the menu. It runs on its OWN
/// thread with a WinForms message loop, apart from the dashboard window.
/// </summary>
/// <remarks>
/// <para>This process never loads WinUI: it is the part of the app that runs
/// all day, and it runs no differently from the C# meter it replaces - the
/// same windows, the same measured fixes, the same file format. The dashboard
/// is a process of its own, reached through <see cref="MeterLink"/>.</para>
/// <para>Everything here is touched on the meter's thread only; the command
/// pipe reaches it through <see cref="Post"/>.</para>
/// </remarks>
internal sealed class MeterHost : ApplicationContext
{
    private const int HistoryLength = 60;
    private const int SampleIntervalMs = 1000;
    private const int HeartbeatIntervalMs = 150;

    private readonly MeterSettings _settings;
    private readonly SynchronizationContext _context;
    private readonly MeterLink.Publisher _link = new();
    private readonly NetMonitor _monitor = new();
    private readonly IconRenderer _renderer = new();
    private readonly NotifyIcon _tray;
    private readonly Timer _timer;
    private readonly Timer _heartbeat;
    private readonly FlyoutForm _flyout;
    private readonly ContextMenuStrip _menu;
    private readonly TaskbarWidget _widget;
    private readonly List<double> _historyDown = [];
    private readonly List<double> _historyUp = [];

    private Recorder _recorder;
    private MeterPalette _palette;
    private int _iconSize;
    private double _totalDown;
    private double _totalUp;
    private DateTime _sessionStart = DateTime.Now;
    private Reading _last = Reading.Idle;
    private Icon? _liveIcon;
    private Icon? _logoIcon;
    private Font? _menuBoldFont;
    private bool _exiting;
    private int _minutesWritten;
    private int _settingsVersion;

    public MeterHost(UiSettings ui)
    {
        _context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = MeterSettings.Load();
        _palette = MeterPalette.For(_settings.Theme);
        _iconSize = Native.TrayIconSize();
        _recorder = StartRecorder();

        _flyout = new FlyoutForm();
        _flyout.SetPalette(_palette);
        _flyout.OpenDashboard += () => Program.OpenDashboard(null);
        _flyout.OpenSpeedTest += () => Program.OpenDashboard("test");

        _widget = new TaskbarWidget();
        _widget.SetPalette(_palette);
        _widget.SetUnits(_settings.Units);
        _widget.SetSide(_settings.Side);
        _widget.LeftClicked += (_, _) => ToggleFlyout();
        _widget.RightClicked += (_, _) => _menu!.Show(Cursor.Position);
        _widget.DoubleClicked += (_, _) => OpenFromDoubleClick();
        // The handle exists from the start: the tray's right-click asks for the
        // foreground through it (see OnTrayMouseDown).
        _ = _widget.Handle;

        _menu = new ContextMenuStrip();
        _menu.Opening += OnMenuOpening;

        // No ContextMenuStrip on the icon: the menu is opened by hand, across the
        // press and the release. See OnTrayMouseDown.
        _tray = new NotifyIcon { Visible = true, Text = "Internet Speed Meter" };
        _tray.MouseDown += OnTrayMouseDown;
        _tray.MouseUp += OnTrayMouseUp;
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenFromDoubleClick();
        };

        _timer = new Timer { Interval = SampleIntervalMs };
        _timer.Tick += (_, _) => Tick();

        // The widget follows the taskbar from a compositor-paced watcher thread;
        // this is the slow heartbeat behind that, catching anything that changes
        // the placement without moving the taskbar.
        _heartbeat = new Timer { Interval = HeartbeatIntervalMs };
        _heartbeat.Tick += (_, _) =>
        {
            if (_settings.TaskbarText) _widget.Sync();
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // A clean exit is the only chance the partial minute has; a logoff is one.
        SystemEvents.SessionEnded += OnSessionEnded;

        // Prime the counters so the first visible reading is a real one-second window.
        _monitor.Sample(_settings.Adapter);
        Redraw();
        _timer.Start();
        _heartbeat.Start();
    }

    /* ---------------------------------------------------- Cross-thread */

    /// <summary>Why recording stopped, when it has.</summary>
    public string? RecordingProblem => _recorder.Enabled || !_settings.Record ? null : _recorder.FaultMessage ?? "Recording is off.";

    /// <summary>Runs <paramref name="action"/> on the meter thread.</summary>
    public void Post(Action action) => _context.Post(_ => action(), null);

    /// <summary>A line from the dashboard (see <see cref="MeterLink"/>). Meter thread.</summary>
    public void Command(string[] command)
    {
        switch (command[0])
        {
            case "reload":
                // The dashboard changed settings.ini: take it as it now is.
                var folders = (_settings.LogFolder, _settings.RawFolder, _settings.Record, _settings.ActiveThresholdBps, _settings.RawRetentionDays);
                _settings.CopyFrom(MeterSettings.Load());
                Apply(folders, save: false);
                break;
            case "notify" when command.Length >= 3:
                Notify(command[1], command[2], command.Length > 3 && command[3] == "warn");
                break;
            case "exit":
                Program.Exit();
                break;
        }
    }

    /// <summary>A Windows notification (a tray balloon, which Windows shows as a toast). Any thread.</summary>
    public void Notify(string title, string text, bool warning = false) => Post(() =>
    {
        if (_exiting) return;
        _tray.ShowBalloonTip(8000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    });

    /* ------------------------------------------------------------- Loop */

    private Recorder StartRecorder()
    {
        var recorder = new Recorder(_settings);
        recorder.Faulted += what => _context.Post(_ => OnRecorderFault(what), null);
        recorder.MinuteWritten += () => _minutesWritten++;
        if (_settings.Record && !recorder.Enabled && recorder.FaultMessage is { } message)
            _context.Post(_ => OnRecorderFault(message), null);
        return recorder;
    }

    private void OnRecorderFault(string what)
    {
        if (UiSettings.Load().NotifyRecording) Notify("Speed history is not being recorded", Format.Ellipsize(what, 200) + "\nDetails are in error.log beside settings.ini.", warning: true);
        Redraw();
    }

    private void Tick()
    {
        var reading = _monitor.Sample(_settings.Adapter);
        _last = reading;
        _recorder.Add(reading);
        _totalDown += reading.DownBytes;
        _totalUp += reading.UpBytes;
        Push(_historyDown, reading.DownBytesPerSecond);
        Push(_historyUp, reading.UpBytesPerSecond);
        Redraw();
        Publish();
    }

    /// <summary>The live state, for the dashboard to read (see <see cref="MeterLink"/>).</summary>
    private void Publish() => _link.Write(new LiveState(
        _last.DownBytesPerSecond, _last.UpBytesPerSecond, _last.Connected, _last.SourceName, _minutesWritten, _settingsVersion, RecordingProblem));

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        while (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private void Redraw()
    {
        // The taskbar text is the primary readout when it can be placed. If the
        // taskbar is vertical or out of room, the tray icon takes the numbers so
        // the meter is never silently blank.
        var placement = WidgetPlacement.Unavailable;
        if (_settings.TaskbarText)
        {
            _widget.SetPalette(_palette);
            _widget.SetUnits(_settings.Units);
            _widget.SetSide(_settings.Side);
            _widget.SetFollowing(true);
            placement = _widget.Update(_last.DownBytesPerSecond, _last.UpBytesPerSecond);
        }
        else
        {
            _widget.SetFollowing(false);
        }

        // An auto-hidden taskbar hides the tray too, so that is no reason to
        // duplicate the numbers there; only a taskbar that can never host them is.
        if (!_settings.TrayNumbers && placement != WidgetPlacement.Unavailable)
        {
            ShowLogoIcon();
            UpdateTooltip();
            return;
        }

        var (downValue, downUnit) = Format.SpeedShort(_last.DownBytesPerSecond, _settings.Units);
        var (upValue, upUnit) = Format.SpeedShort(_last.UpBytesPerSecond, _settings.Units);
        var downText = _settings.ShowUnitOnIcon ? downValue + downUnit : downValue;
        var upText = _settings.ShowUnitOnIcon ? upValue + upUnit : upValue;

        var icon = _settings.Layout switch
        {
            IconLayout.DownloadOnly => _renderer.RenderSingle(downText, _palette.Download, _iconSize),
            IconLayout.UploadOnly => _renderer.RenderSingle(upText, _palette.Upload, _iconSize),
            _ => _settings.UploadOnTop
                ? _renderer.Render(upText, downText, _palette.Upload, _palette.Download, _iconSize)
                : _renderer.Render(downText, upText, _palette.Download, _palette.Upload, _iconSize),
        };
        if (icon is not null)
        {
            _tray.Icon = icon;
            _liveIcon?.Dispose();
            _liveIcon = icon;
        }
        UpdateTooltip();
    }

    /// <summary>The app's mark, while the numbers live on the taskbar instead.</summary>
    private void ShowLogoIcon()
    {
        if (_logoIcon is null)
        {
            try
            {
                _logoIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"), _iconSize, _iconSize);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                _logoIcon = SystemIcons.Application;
            }
        }
        if (ReferenceEquals(_tray.Icon, _logoIcon)) return;
        _tray.Icon = _logoIcon;
        _liveIcon?.Dispose();
        _liveIcon = null;
        // The next numbers icon must be drawn even if its content has not changed.
        _renderer.Invalidate();
    }

    private void UpdateTooltip()
    {
        // The shell caps a tray tooltip at 127 characters.
        var tip = $"↓ {Format.SpeedLong(_last.DownBytesPerSecond, _settings.Units)}   ↑ {Format.SpeedLong(_last.UpBytesPerSecond, _settings.Units)}\n{Format.Ellipsize(_last.SourceName, 40)}";
        if (RecordingProblem is not null) tip += "\nNot recording";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        if (_flyout.Visible) PushFlyoutData();
    }

    private void PushFlyoutData() => _flyout.UpdateData(
        _last.SourceName, _last.DownBytesPerSecond, _last.UpBytesPerSecond, _totalDown, _totalUp, _sessionStart,
        _settings.Units, [.. _historyDown], [.. _historyUp], RecordingProblem is null ? null : "Not recording: " + RecordingProblem);

    /* ------------------------------------------------------------ Input */

    /// <summary>
    /// Takes the foreground on the PRESS, so the menu the release opens is a
    /// foreground window's menu - the same fault, from the same cause, as the
    /// taskbar readout had (see <see cref="TaskbarWidget"/>'s OnMouseDown).
    /// NotifyIcon would show its own menu in the one order that costs a click.
    /// </summary>
    private void OnTrayMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) Native.SetForegroundWindow(_widget.Handle);
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        else if (e.Button == MouseButtons.Left) ToggleFlyout();
    }

    private void ToggleFlyout()
    {
        if (_flyout.Visible)
        {
            _flyout.Hide();
            return;
        }
        // The click reaching the tray may be the one that just dismissed the panel.
        if ((DateTime.UtcNow - _flyout.HiddenAt).TotalMilliseconds < 300) return;
        PushFlyoutData();
        _flyout.ShowNear(Cursor.Position);
    }

    private void OpenFromDoubleClick()
    {
        _flyout.Hide();
        Program.OpenDashboard(null);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle) ApplyTheme();
    }

    private void ApplyTheme()
    {
        _palette = MeterPalette.For(_settings.Theme);
        _flyout.SetPalette(_palette);
        _renderer.Invalidate();
        Redraw();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var size = Native.TrayIconSize();
        if (size == _iconSize) return;
        _iconSize = size;
        _logoIcon = null;
        Redraw();
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => Post(() => _recorder.Flush());

    /* ------------------------------------------------------------- Menu */

    /// <summary>Rebuilt on every open, so a hot-plugged adapter shows up.</summary>
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Items.Clear() detaches but does not dispose.
        var previous = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var item in previous) item.Dispose();

        _menuBoldFont ??= new Font(_menu.Font, FontStyle.Bold);
        var open = new ToolStripMenuItem("Open dashboard", null, (_, _) => Program.OpenDashboard(null)) { Font = _menuBoldFont };
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripMenuItem("Run a speed test…", null, (_, _) => Program.OpenDashboard("test")));
        _menu.Items.Add(new ToolStripMenuItem("Details…", null, (_, _) => ToggleFlyout()));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(BuildDisplayMenu());
        _menu.Items.Add(BuildAdapterMenu());
        _menu.Items.Add(BuildUnitsMenu());
        _menu.Items.Add(BuildIconMenu());
        _menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            if (!StartupRegistration.Set(!StartupRegistration.IsEnabled))
                MessageBox.Show("Could not update the Windows startup entry. The registry key may be managed by your organisation.",
                    "Internet Speed Meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        })
        { Checked = StartupRegistration.IsEnabled };
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripMenuItem("Reset session counters", null, (_, _) =>
        {
            _totalDown = 0;
            _totalUp = 0;
            _sessionStart = DateTime.Now;
            _historyDown.Clear();
            _historyUp.Clear();
            Redraw();
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Program.Exit()));
    }

    private ToolStripMenuItem Check(string label, bool on, Action<MeterSettings> change) =>
        new(label, null, (_, _) => Change(change)) { Checked = on };

    private ToolStripMenuItem BuildDisplayMenu()
    {
        var root = new ToolStripMenuItem("Show speeds on");
        root.DropDownItems.Add(Check("Taskbar text  (clock size)", _settings.TaskbarText, s => s.TaskbarText = !s.TaskbarText));
        root.DropDownItems.Add(Check("Tray icon numbers", _settings.TrayNumbers, s => s.TrayNumbers = !s.TrayNumbers));
        root.DropDownItems.Add(new ToolStripSeparator());
        var position = new ToolStripMenuItem("Taskbar text position");
        position.DropDownItems.Add(Check("Left end", _settings.Side == TaskbarSide.Left, s => s.Side = TaskbarSide.Left));
        position.DropDownItems.Add(Check("Right, beside the clock", _settings.Side == TaskbarSide.Right, s => s.Side = TaskbarSide.Right));
        root.DropDownItems.Add(position);
        return root;
    }

    private ToolStripMenuItem BuildAdapterMenu()
    {
        var root = new ToolStripMenuItem("Adapter");
        root.DropDownItems.Add(AdapterItem("Automatic (busiest adapter)", MeterSettings.AdapterAuto));
        root.DropDownItems.Add(AdapterItem("All adapters (combined)", MeterSettings.AdapterAll));
        root.DropDownItems.Add(new ToolStripSeparator());
        var adapters = NetMonitor.ListAdapters();
        if (adapters.Count == 0) root.DropDownItems.Add(new ToolStripMenuItem("No connected adapters") { Enabled = false });
        foreach (var adapter in adapters) root.DropDownItems.Add(AdapterItem(adapter.Label, adapter.Id));
        return root;
    }

    private ToolStripMenuItem AdapterItem(string label, string id) => new(label, null, (_, _) =>
    {
        _historyDown.Clear();
        _historyUp.Clear();
        Change(s => s.Adapter = id);
    })
    { Checked = _settings.Adapter == id };

    private ToolStripMenuItem BuildUnitsMenu()
    {
        var root = new ToolStripMenuItem("Units");
        root.DropDownItems.Add(Check("Bytes  (KB/s, MB/s)", _settings.Units == UnitMode.Bytes, s => s.Units = UnitMode.Bytes));
        root.DropDownItems.Add(Check("Bits  (Kbps, Mbps)", _settings.Units == UnitMode.Bits, s => s.Units = UnitMode.Bits));
        return root;
    }

    private ToolStripMenuItem BuildIconMenu()
    {
        var root = new ToolStripMenuItem("Icon");
        var layout = new ToolStripMenuItem("Layout");
        layout.DropDownItems.Add(Check("Download and upload", _settings.Layout == IconLayout.Both, s => s.Layout = IconLayout.Both));
        layout.DropDownItems.Add(Check("Download only  (bigger text)", _settings.Layout == IconLayout.DownloadOnly, s => s.Layout = IconLayout.DownloadOnly));
        layout.DropDownItems.Add(Check("Upload only  (bigger text)", _settings.Layout == IconLayout.UploadOnly, s => s.Layout = IconLayout.UploadOnly));
        root.DropDownItems.Add(layout);
        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add(Check("Show unit letters", _settings.ShowUnitOnIcon, s => s.ShowUnitOnIcon = !s.ShowUnitOnIcon));
        root.DropDownItems.Add(Check("Upload on top", _settings.UploadOnTop, s => s.UploadOnTop = !s.UploadOnTop));
        root.DropDownItems.Add(new ToolStripSeparator());
        var theme = new ToolStripMenuItem("Text colour");
        theme.DropDownItems.Add(Check("Match taskbar", _settings.Theme == TextTheme.Auto, s => s.Theme = TextTheme.Auto));
        theme.DropDownItems.Add(Check("Light text (dark taskbar)", _settings.Theme == TextTheme.Dark, s => s.Theme = TextTheme.Dark));
        theme.DropDownItems.Add(Check("Dark text (light taskbar)", _settings.Theme == TextTheme.Light, s => s.Theme = TextTheme.Light));
        root.DropDownItems.Add(theme);
        return root;
    }

    /// <summary>A change made on this thread: applied at once.</summary>
    private void Change(Action<MeterSettings> change)
    {
        var folders = (_settings.LogFolder, _settings.RawFolder, _settings.Record, _settings.ActiveThresholdBps, _settings.RawRetentionDays);
        change(_settings);
        Apply(folders);
    }

    private void Apply((string, string, bool, long, int) before, bool save = true)
    {
        if (save) _settings.Save();
        // Anything the recorder was built from restarts it, flushing the minute in hand first.
        if (before != (_settings.LogFolder, _settings.RawFolder, _settings.Record, _settings.ActiveThresholdBps, _settings.RawRetentionDays))
        {
            _recorder.Dispose();
            _recorder = StartRecorder();
        }
        ApplyTheme();
        _settingsVersion++;
        Publish();
    }

    /* ------------------------------------------------------------- Exit */

    /// <summary>Stops sampling and writes the minute in hand. Meter thread only.</summary>
    public void Shutdown()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        _heartbeat.Stop();
        _widget.SetFollowing(false);
        // After the timer, so nothing arrives mid-flush, and before anything that
        // can throw: a clean exit is the partial minute's only chance.
        _recorder.Dispose();
        _tray.Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionEnded -= OnSessionEnded;
            _timer.Dispose();
            _heartbeat.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _menuBoldFont?.Dispose();
            _flyout.Dispose();
            _widget.Dispose();
            _liveIcon?.Dispose();
            if (_logoIcon is not null && !ReferenceEquals(_logoIcon, SystemIcons.Application)) _logoIcon.Dispose();
            _renderer.Dispose();
            _link.Dispose();
        }
        base.Dispose(disposing);
    }
}

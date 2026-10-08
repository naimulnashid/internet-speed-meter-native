using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SpeedMeter.App.Controls;
using SpeedMeter.App.State;
using SpeedMeter.App.Theme;
using SpeedMeter.App.Views;
using SpeedMeter.Core;
using SpeedMeter.Core.History;
using SpeedMeter.App.Meter;
using SpeedMeter.Core.View;
using Windows.Graphics;

namespace SpeedMeter.App;

/// <summary>
/// The dashboard: title bar, the top bar, and the page area. Two pages, as the
/// web dashboard had: Speed (the history) and Test (the speed test).
/// </summary>
/// <remarks>
/// <para>The top bar is the web's: page tabs on the left; on the right, the
/// meter's live reading and the settings menu.</para>
/// <para>Closing the window never stops the meter, which is another process:
/// it ends this one, and WinUI's memory with it. A speed test still running
/// keeps the process alive, out of sight, until the result is saved.</para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly AppState _state;
    private readonly PageContext _ctx;
    private readonly Dictionary<PageKind, Button> _tabs = [];
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ContentControl _pageHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
    private readonly TextBlock _liveDown = Ui.Text("", 14.5, 600, Palette.DownBrush, numeric: true, selectable: false);
    private readonly TextBlock _liveUp = Ui.Text("", 14.5, 600, Palette.UpBrush, numeric: true, selectable: false);
    private readonly TextBlock _liveSource = Ui.Text("", 13, 400, Palette.TextFaintBrush, selectable: false);
    private Grid _frame = null!;
    private ZoomBox _zoomBox = null!;

    private PageKind _route;
    private IPage? _page;
    private int _buildToken;
    private bool _inTray = true;

    public MainWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        _route = state.Ui.Page == "test" ? PageKind.Test : PageKind.Speed;
        _ctx = new PageContext { State = state, Navigate = Navigate, Redraw = () => Rebuild(keepScroll: true, quiet: true), Window = this };

        Zoom.Set(state.Ui.Zoom);
        Zoom.Changed += OnZoomChanged;
        // Before anything is built, so the first frame is already the right theme.
        Palette.Apply(ResolveTheme(state.Ui.Theme));
        _systemColors.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_state.Ui.Theme == "system") Palette.Apply(ResolveTheme("system"));
        });
        Palette.Changed += OnThemeChanged;
        ConfigureWindow();
        BuildShell();
        Root.RequestedTheme = Palette.IsLight ? ElementTheme.Light : ElementTheme.Dark;

        state.HistoryChanged += () =>
        {
            // A minute landing refreshes the page in place; never mid-run on the
            // Test page, whose live parts belong to the run.
            if (_inTray || (_route == PageKind.Test && _state.Test.Busy)) return;
            Rebuild(keepScroll: true, quiet: true);
        };
        state.SettingsChanged += () =>
        {
            if (!_inTray) Rebuild(keepScroll: true, quiet: true);
        };
        state.Test.Changed += () =>
        {
            // Closed mid-run: the run was what kept this process; it is done now.
            if (_inTray && !_state.Test.Busy) Environment.Exit(0);
            if (!_inTray && _route == PageKind.Test) Rebuild(keepScroll: true, quiet: true);
        };
        state.LiveReading += ShowLive;
    }

    /* ------------------------------------------------------------ Window */

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        PaintCaptionButtons();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Title = "Internet Speed Meter";

        // A restored size for when the user un-maximises: 1440 x 960 at the
        // display's scale, never larger than its work area.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1440 * scale), area.Width - 40);
        var height = Math.Min((int)(960 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }

        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
            _inTray = true;
            _state.WindowVisible = false;
            if (!_state.Ui.TrayNoteShown)
            {
                _state.Ui.TrayNoteShown = true;
                _state.Ui.Save();
                _state.Notify("Internet Speed Meter is still running",
                    "The meter keeps recording, and the dashboard opens again from the taskbar text or the tray icon. Right-click either to exit.");
            }
            // The meter is another process and carries on; this one is only the window.
            if (!_state.Test.Busy) Environment.Exit(0);
            _pageHost.Content = null;
            _page = null;
        };
    }

    /// <summary>Shows the window, maximised, on <paramref name="page"/> when given.</summary>
    public void Open(string? page)
    {
        var target = page switch { "test" => PageKind.Test, "speed" => PageKind.Speed, _ => _route };
        var changed = target != _route;
        _route = target;
        if (changed) _page = null;
        if (_inTray || changed)
        {
            _inTray = false;
            _state.WindowVisible = true;
            UpdateChrome();
            Rebuild(keepScroll: false, quiet: false);
        }
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Maximized) presenter.Maximize();
        Activate();
    }

    /* ------------------------------------------------------------- Theme */

    private readonly Windows.UI.ViewManagement.UISettings _systemColors = new();

    private bool ResolveTheme(string choice) => choice switch
    {
        "light" => true,
        "system" => _systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background) is { R: > 128 },
        _ => false,
    };

    private void SetTheme(string choice)
    {
        _state.Ui.Theme = choice;
        _state.Ui.Save();
        Palette.Apply(ResolveTheme(choice));
    }

    /// <summary>
    /// The shared brushes are already retinted (Palette.Apply); what is left is
    /// what baked a colour in - the caption buttons, the controls' own theme,
    /// the page's charts - so the page is rebuilt.
    /// </summary>
    private void OnThemeChanged()
    {
        Root.RequestedTheme = Palette.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        PaintCaptionButtons();
        UpdateChrome();
        Rebuild(keepScroll: true, quiet: true);
    }

    private void PaintCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = Palette.TextMuted;
        bar.ButtonInactiveForegroundColor = Palette.TextFaint;
        bar.ButtonHoverBackgroundColor = Palette.SurfaceHover;
        bar.ButtonHoverForegroundColor = Palette.Text;
        bar.ButtonPressedBackgroundColor = Palette.BorderBright;
        bar.ButtonPressedForegroundColor = Palette.Text;
    }

    /* ------------------------------------------------------------- Shell */

    private void BuildShell()
    {
        Root.Background = Palette.BgBrush;
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Title bar: draggable, the app's mark and name, room for the caption buttons.
        var titleBar = new Grid { Background = Palette.BgBrush, Padding = new Thickness(16, 0, 150, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new Image { Width = 16, Height = 16, Source = new SvgImageSource(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.svg"))) { RasterizePixelWidth = 32, RasterizePixelHeight = 32 } });
        title.Children.Add(Ui.Text("Internet Speed Meter", 12.5, 500, Palette.TextFaintBrush, selectable: false));
        titleBar.Children.Add(title);
        Root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // Everything below the title bar zooms, as a browser zooms the page and not its chrome.
        _zoomBox = new ZoomBox(main) { Zoom = Zoom.Level };
        Grid.SetRow(_zoomBox, 1);
        Root.Children.Add(_zoomBox);
        Root.Children.Add(ZoomIndicator());

        main.Children.Add(BuildTopBar());

        var column = new StackPanel { MaxWidth = 1320 + 80, Padding = new Thickness(40, 44, 40, 40) };
        column.Children.Add(_pageHost);
        column.Children.Add(Footer());
        // The frame is pinned to the viewport's width and the column centres in
        // it: a ScrollViewer centres a MaxWidth column by its DESIRED width, so a
        // narrow page slid sideways away from the top bar.
        _frame = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _frame.Children.Add(column);
        _scroller.SizeChanged += (_, e) => _frame.Width = e.NewSize.Width;
        _scroller.Content = _frame;
        // Behind the page, a surface for the light theme's card shadows to fall
        // on: a ThemeShadow draws only onto its receivers, never an ancestor.
        var shadowReceiver = new Grid { Background = Palette.BgBrush };
        Grid.SetRow(shadowReceiver, 1);
        main.Children.Add(shadowReceiver);
        Ui.ShadowReceiver = shadowReceiver;
        Grid.SetRow(_scroller, 1);
        main.Children.Add(_scroller);

        AddKeys();
    }

    private static Border Footer()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.Text("Local only · no telemetry · the speed test is the only traffic it sends", 13, 400, Palette.TextFaintBrush, wrap: true));
        var right = Ui.Text($"© 2026 Naimul Nashid · v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", 13, 400, Palette.TextFaintBrush);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return new Border
        {
            Margin = new Thickness(0, 40, 0, 0),
            Padding = new Thickness(0, 22, 0, 0),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = grid,
        };
    }

    private void AddKeys()
    {
        // F5 re-reads the history, as a browser reloads.
        var f5 = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F5 };
        f5.Invoked += (_, e) =>
        {
            e.Handled = true;
            Rebuild(keepScroll: true, quiet: true);
        };
        Root.KeyboardAccelerators.Add(f5);

        // Page zoom, with a browser's keys: Ctrl with =/+ (and Shift, which "+"
        // needs on most layouts), the numpad's + and -, and 0 to reset.
        const Windows.System.VirtualKey Plus = (Windows.System.VirtualKey)0xBB, Minus = (Windows.System.VirtualKey)0xBD;
        void ZoomKey(Windows.System.VirtualKey key, Func<bool> action, bool shift = false)
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = Windows.System.VirtualKeyModifiers.Control | (shift ? Windows.System.VirtualKeyModifiers.Shift : 0),
            };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                action();
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
        ZoomKey(Plus, Zoom.In);
        ZoomKey(Plus, Zoom.In, shift: true);
        ZoomKey(Windows.System.VirtualKey.Add, Zoom.In);
        ZoomKey(Minus, Zoom.Out);
        ZoomKey(Windows.System.VirtualKey.Subtract, Zoom.Out);
        ZoomKey(Windows.System.VirtualKey.Number0, Zoom.Reset);
        ZoomKey(Windows.System.VirtualKey.NumberPad0, Zoom.Reset);
        // Ctrl+wheel. handledEventsToo: the scroller marks wheel events handled.
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            // KeyModifiers can arrive empty with Ctrl held, so the key's own state is checked too.
            var ctrl = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)
                || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!ctrl) return;
            var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
            if (delta > 0) Zoom.In();
            else if (delta < 0) Zoom.Out();
            e.Handled = true;
        }), handledEventsToo: true);
        // WinUI shows a root accelerator's key as a tooltip over any spot without one of its own.
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /* ----------------------------------------------------------- Top bar */

    private Border BuildTopBar()
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (kind, label) in new[] { (PageKind.Speed, "Speed"), (PageKind.Test, "Test") })
        {
            var tab = new Button
            {
                Content = Ui.Text(label, 15, 500, Palette.TextMutedBrush, selectable: false),
                Padding = new Thickness(14.4, 7.2, 14.4, 7.2),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                BorderThickness = new Thickness(0),
            };
            tab.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
            tab.Resources["ButtonBackgroundPressed"] = Palette.SurfaceHoverBrush;
            var target = kind;
            tab.Click += (_, _) => Navigate(target);
            _tabs[kind] = tab;
            nav.Children.Add(tab);
        }
        grid.Children.Add(nav);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(LiveReadout());
        right.Children.Add(SettingsButton());
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return new Border
        {
            Padding = new Thickness(40, 12, 40, 12),
            MinHeight = 61,
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Background = Palette.BgBrush,
            Child = grid,
        };
    }

    /// <summary>The meter's live reading, as on the taskbar: down, up, and the adapter.</summary>
    private StackPanel LiveReadout()
    {
        var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(_liveSource);
        _liveSource.VerticalAlignment = VerticalAlignment.Center;
        _liveDown.MinWidth = 96;
        _liveUp.MinWidth = 96;
        box.Children.Add(_liveDown);
        box.Children.Add(_liveUp);
        Ui.SetTip(box, "Right now, at the adapter: the meter's own reading, once a second.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(_liveDown, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Off);
        ShowLive(_state.Live);
        return box;
    }

    private void ShowLive(LiveState? live)
    {
        if (_inTray) return;
        if (live is null)
        {
            // Only reachable when the dashboard was started on its own.
            _liveDown.Text = "";
            _liveUp.Text = "";
            _liveSource.Text = "The meter is not running";
            return;
        }
        _liveDown.Text = "↓ " + Format.SpeedLong(live.Down, _state.Units);
        _liveUp.Text = "↑ " + Format.SpeedLong(live.Up, _state.Units);
        _liveSource.Text = live.Connected ? Format.Ellipsize(live.Source, 28) : "";
    }

    private readonly TextBlock _zoomText = Ui.Text("", 14, 600, Palette.TextBrush, numeric: true, selectable: false);
    private Border? _zoomPill;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomTimer;

    /// <summary>The level, shown for a moment after it changes, as a browser's address bar does.</summary>
    private Border ZoomIndicator()
    {
        _zoomPill = new Border
        {
            Child = _zoomText,
            Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(Ui.RadiusSmall),
            Background = Palette.TooltipBgBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            IsHitTestVisible = false,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) },
        };
        Grid.SetRow(_zoomPill, 1);
        return _zoomPill;
    }

    private void OnZoomChanged()
    {
        _zoomBox.Zoom = Zoom.Level;
        _state.Ui.Zoom = Zoom.Level;
        _state.Ui.Save();
        if (_zoomPill is null) return;
        _zoomText.Text = Zoom.Label;
        _zoomPill.Opacity = 1;
        if (_zoomTimer is null)
        {
            _zoomTimer = DispatcherQueue.CreateTimer();
            _zoomTimer.Interval = TimeSpan.FromMilliseconds(1300);
            _zoomTimer.IsRepeating = false;
            _zoomTimer.Tick += (_, _) => _zoomPill.Opacity = 0;
        }
        _zoomTimer.Stop();
        _zoomTimer.Start();
    }

    private Button SettingsButton()
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 15, Foreground = Palette.TextMutedBrush },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(1),
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.BorderBrightBrush,
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.TextFaintBrush;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Settings");
        ToolTipService.SetToolTip(button, "Settings");
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };

        // Theme: three choices, one checked. Dark by default; System follows Windows' app mode.
        var theme = new MenuFlyoutSubItem { Text = "Theme", Icon = new FontIcon { Glyph = "" } };
        var themeItems = new List<(RadioMenuFlyoutItem Item, string Value)>();
        foreach (var (label, value) in new[] { ("Dark", "dark"), ("Light", "light"), ("Use Windows setting", "system") })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "theme" };
            item.Click += (_, _) => SetTheme(value);
            theme.Items.Add(item);
            themeItems.Add((item, value));
        }
        flyout.Items.Add(theme);

        // Units are the meter's: the taskbar text changes with them.
        var units = new MenuFlyoutSubItem { Text = "Units", Icon = new FontIcon { Glyph = "" } };
        var bits = new RadioMenuFlyoutItem { Text = "Bits  (Kbps, Mbps)", GroupName = "units" };
        bits.Click += (_, _) => _state.ChangeMeter(s => s.Units = UnitMode.Bits);
        var bytes = new RadioMenuFlyoutItem { Text = "Bytes  (KB/s, MB/s)", GroupName = "units" };
        bytes.Click += (_, _) => _state.ChangeMeter(s => s.Units = UnitMode.Bytes);
        units.Items.Add(bits);
        units.Items.Add(bytes);
        flyout.Items.Add(units);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var login = new ToggleMenuFlyoutItem { Text = "Start with Windows" };
        login.Click += (_, _) =>
        {
            if (!StartupRegistration.Set(login.IsChecked)) _ = Shell.ShowMessage(this, "Could not change start with Windows", "The registry key may be managed by your organisation.");
            login.IsChecked = StartupRegistration.IsEnabled;
        };
        var notifyRecording = new ToggleMenuFlyoutItem { Text = "Notify when recording stops" };
        notifyRecording.Click += (_, _) =>
        {
            _state.Ui.NotifyRecording = notifyRecording.IsChecked;
            _state.Ui.Save();
        };
        var notifyTests = new ToggleMenuFlyoutItem { Text = "Notify when a test ends while closed" };
        notifyTests.Click += (_, _) =>
        {
            _state.Ui.NotifySpeedTests = notifyTests.IsChecked;
            _state.Ui.Save();
        };
        flyout.Items.Add(login);
        flyout.Items.Add(notifyRecording);
        flyout.Items.Add(notifyTests);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // The zoom keys, findable. The shortcut text is shown, not bound: the real accelerators live on the window.
        var zoomIn = new MenuFlyoutItem { Text = "Zoom in", KeyboardAcceleratorTextOverride = "Ctrl+Plus", Icon = new FontIcon { Glyph = "" } };
        zoomIn.Click += (_, _) => Zoom.In();
        var zoomOut = new MenuFlyoutItem { Text = "Zoom out", KeyboardAcceleratorTextOverride = "Ctrl+Minus", Icon = new FontIcon { Glyph = "" } };
        zoomOut.Click += (_, _) => Zoom.Out();
        var zoomReset = new MenuFlyoutItem { KeyboardAcceleratorTextOverride = "Ctrl+0" };
        zoomReset.Click += (_, _) => Zoom.Reset();
        flyout.Items.Add(zoomIn);
        flyout.Items.Add(zoomOut);
        flyout.Items.Add(zoomReset);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var export = new MenuFlyoutItem { Text = "Export history as CSV…", Icon = new FontIcon { Glyph = "" } };
        export.Click += async (_, _) => await ExportCsv();
        flyout.Items.Add(export);
        var history = new MenuFlyoutItem { Text = "Open history folder", Icon = new FontIcon { Glyph = "" } };
        history.Click += (_, _) => Shell.OpenFolder(_state.Meter.LogFolder);
        flyout.Items.Add(history);
        var raw = new MenuFlyoutItem { Text = "Open raw samples folder", Icon = new FontIcon { Glyph = "" } };
        raw.Click += (_, _) => Shell.OpenFolder(_state.Meter.RawFolder);
        flyout.Items.Add(raw);
        var settings = new MenuFlyoutItem { Text = "Open settings folder", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => Shell.OpenFolder(AppPaths.SettingsDir);
        flyout.Items.Add(settings);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => Program.Exit();
        flyout.Items.Add(exit);

        flyout.Opening += (_, _) =>
        {
            foreach (var (item, value) in themeItems) item.IsChecked = _state.Ui.Theme == value;
            bits.IsChecked = _state.Units == UnitMode.Bits;
            bytes.IsChecked = _state.Units == UnitMode.Bytes;
            login.IsChecked = StartupRegistration.IsEnabled;
            notifyRecording.IsChecked = _state.Ui.NotifyRecording;
            notifyTests.IsChecked = _state.Ui.NotifySpeedTests;
            zoomReset.Text = $"Reset zoom ({Zoom.Label})";
            raw.IsEnabled = _state.Meter.RawFolder.Length > 0;
        };
        button.Flyout = flyout;
        return button;
    }

    /// <summary>The whole minute history as one CSV a spreadsheet can open.</summary>
    private async Task ExportCsv()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"speed-history-{DateTime.Now:yyyy-MM-dd}" };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is not { } file) return;
        var settings = _state.Meter;
        try
        {
            var rows = await Task.Run(() =>
            {
                var days = HistoryStats.AllDays(settings.LogFolder);
                if (days.Count == 0) return 0;
                using var writer = new StreamWriter(file.Path, append: false);
                return LogExport.Write(settings, LogExport.Source.Minutes, days[0].Day, DateTime.Now.Date, writer);
            });
            await Shell.ShowMessage(this, "History exported", $"{Format.Count(rows)} minutes written to {file.Path}. For one row per second over the retained days, use `speedmeter export-csv --raw`.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Shell.ShowMessage(this, "Could not export", ex.Message);
        }
    }

    /* -------------------------------------------------------- Navigation */

    private void Navigate(PageKind route)
    {
        var same = route == _route;
        _route = route;
        _state.Ui.Page = route == PageKind.Test ? "test" : "speed";
        _state.Ui.Save();
        if (!same) _page = null;
        UpdateChrome();
        Rebuild(keepScroll: same, quiet: same);
    }

    private IPage Create(PageKind route) => route == PageKind.Test ? new TestPage(_ctx) : new SpeedPage(_ctx);

    /// <summary>
    /// Loads the page's data off the UI thread, then builds it. A new page
    /// shows its skeleton meanwhile; a refresh of the page already up keeps it
    /// on screen and, when quiet, does not replay its entrance.
    /// </summary>
    private async void Rebuild(bool keepScroll, bool quiet)
    {
        if (_inTray) return;
        var token = ++_buildToken;
        var offset = _scroller.VerticalOffset;
        var fresh = _page is null;
        var page = _page ??= Create(_route);

        if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_SKELETON") == "1")
        {
            // Development only: the skeleton alone, never replaced, so it can be
            // captured and its height compared with the real page's.
            _pageHost.Content = SkeletonFor(_route);
            ApplyDebugView();
            return;
        }
        if (fresh || _pageHost.Content is null) _pageHost.Content = SkeletonFor(_route);
        try
        {
            await Task.Run(page.Load);
            if (token != _buildToken || _inTray) return;
            Motion.Quiet = quiet && !fresh;
            try
            {
                _pageHost.Content = page.Build();
            }
            finally
            {
                Motion.Quiet = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            if (token != _buildToken) return;
            _pageHost.Content = Parts.Alert("This page could not be drawn", "Press F5 to try again.", string.Join("\n", ex.ToString().Split('\n').Take(12)));
        }

        _scroller.UpdateLayout();
        _scroller.ChangeView(null, keepScroll ? offset : 0, null, disableAnimation: true);
        ApplyDebugView();
    }

    /// <summary>The route's own page, built from stand-in data and turned into a skeleton.</summary>
    private UIElement SkeletonFor(PageKind route)
    {
        var page = Create(route);
        page.Placeholder();
        Motion.Quiet = true;
        try
        {
            return page.Build() is FrameworkElement built ? Skeleton.Apply(built) : new Grid();
        }
        finally
        {
            Motion.Quiet = false;
        }
    }

    private void UpdateChrome()
    {
        Title = _route == PageKind.Test ? "Speed test · Internet Speed Meter" : "Speed · Internet Speed Meter";
        foreach (var (kind, tab) in _tabs)
        {
            var active = kind == _route;
            tab.Background = active ? Palette.AccentDimBrush : Palette.TransparentBrush;
            if (tab.Content is TextBlock text) text.Foreground = active ? Palette.AccentBrightBrush : Palette.TextMutedBrush;
        }
        ShowLive(_state.Live);
    }

    /* ------------------------------------------------ Development captures */

    private bool _debugApplied;

    /// <summary>
    /// Development only: <c>SPEEDMETER_DEBUG_VIEW=page,scroll</c> opens a page
    /// at a scroll offset once its data is in, so tools\Capture-Window.ps1 can
    /// photograph any section; <c>SPEEDMETER_DEBUG_FULLPAGE=width</c> then grows
    /// the window to the page's whole height; <c>SPEEDMETER_DEBUG_SWITCH_THEME</c>
    /// switches theme after the first build, through the menu's own path.
    /// </summary>
    private void ApplyDebugView()
    {
        if (_debugApplied || Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_VIEW") is not { Length: > 0 } spec) return;
        var parts = spec.Split(',');
        var kind = Enum.TryParse<PageKind>(parts[0], true, out var k) ? k : PageKind.Speed;
        if (_route != kind)
        {
            Navigate(kind);
            return;
        }
        _debugApplied = true;
        if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_START_TEST") == "1") _state.Test.Start();
        if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_SWITCH_THEME") is { Length: > 0 } theme)
        {
            SetTheme(theme);
            return;
        }
        var scroll = parts.Length > 1 && double.TryParse(parts[^1], out var s) ? s : 0;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scroller.UpdateLayout();
            _scroller.ChangeView(null, scroll, null, disableAnimation: true);
            if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_FULLPAGE") is { Length: > 0 } width && double.TryParse(width, out var w)) FitWindowToPage(w);
            if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_SNAPSHOT") is { Length: > 0 } path) _ = SnapshotAsync(path);
        });
    }

    /// <summary>
    /// Development only: <c>SPEEDMETER_DEBUG_SNAPSHOT=file.png</c> renders the
    /// window's own tree to a PNG once the page is in (and the page alone, top
    /// to bottom, beside it as file.page.png), then exits. Unlike a screen grab
    /// it works with the screen locked or the window covered.
    /// </summary>
    private async Task SnapshotAsync(string path)
    {
        await Task.Delay(TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_SNAPSHOT_DELAY"), out var d) ? d : 2.5));
        try
        {
            await Render(Root, path);
            // A render covers only what it can see drawn, and the light theme's
            // lifted cards do not count: with nothing else at the top, the page
            // came back as its footer. Painted for the capture, the frame is all
            // of it - not otherwise, as the shadows fall on a surface behind it.
            _frame.Background = Palette.BgBrush;
            await Task.Delay(100);
            await Render(_frame, Path.ChangeExtension(path, ".page.png"));
            _frame.Background = null;
        }
        catch (Exception ex)
        {
            Program.Crash("snapshot", ex);
        }
        if (Environment.GetEnvironmentVariable("SPEEDMETER_DEBUG_SNAPSHOT_STAY") != "1") Program.Exit();
    }

    private static async Task Render(UIElement element, string path)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(element);
        var pixels = await target.GetPixelsAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, file);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
        file.Seek(0);
        using var output = File.Create(path);
        await file.AsStreamForRead().CopyToAsync(output);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallWindowProcW(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private WndProc? _unboundedProc;
    private IntPtr _previousProc;
    private int _fitPasses;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _fitTimer;

    /// <summary>
    /// Sizes the window to a width and the page's whole height. Windows caps a
    /// window at about the screen's size through WM_GETMINMAXINFO, so the cap is
    /// lifted first; re-measured until the page stops growing.
    /// </summary>
    private void FitWindowToPage(double widthDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (_unboundedProc is null)
        {
            _unboundedProc = (h, msg, w, l) =>
            {
                var result = CallWindowProcW(_previousProc, h, msg, w, l);
                if (msg == 0x0024) // WM_GETMINMAXINFO: ptMaxTrackSize is the fifth POINT.
                {
                    Marshal.WriteInt32(l, 32, 30000);
                    Marshal.WriteInt32(l, 36, 30000);
                }
                return result;
            };
            _previousProc = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_unboundedProc));
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Restored) presenter.Restore();

        var scale = GetDpiForWindow(hwnd) / 96.0;
        _scroller.UpdateLayout();
        var height = Root.ActualHeight - _scroller.ViewportHeight + _scroller.ExtentHeight;
        AppWindow.Move(new PointInt32(0, 0));
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(widthDip * scale), (int)Math.Ceiling(height * scale)));
        if (++_fitPasses >= 6) return;
        var timer = _fitTimer ??= DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        if (_fitPasses == 1)
        {
            timer.Tick += (_, _) =>
            {
                _scroller.UpdateLayout();
                if (_scroller.ExtentHeight > _scroller.ViewportHeight + 0.5 || Math.Abs(Root.ActualWidth - widthDip) > 0.5) FitWindowToPage(widthDip);
            };
        }
        timer.Start();
    }
}

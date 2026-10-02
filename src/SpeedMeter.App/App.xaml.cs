using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SpeedMeter.App.State;

namespace SpeedMeter.App;

/// <summary>
/// The dashboard process: one window, opened by the meter and gone when it
/// closes (see <see cref="Program"/>). Nothing here runs while the window is
/// closed, unless a speed test is still going.
/// </summary>
public partial class App : Application
{
    private readonly string? _page;
    private DispatcherQueue? _ui;
    private MainWindow? _window;

    public App(string? page)
    {
        _page = page;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Program.Crash("dashboard", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        var state = new AppState(_ui, Program.Ui);
        _window = new MainWindow(state);
        _window.Open(_page);
    }

    /// <summary>Brings the window forward, on <paramref name="page"/> when given. Any thread.</summary>
    internal void Show(string? page) => _ui?.TryEnqueue(() => _window?.Open(page));
}

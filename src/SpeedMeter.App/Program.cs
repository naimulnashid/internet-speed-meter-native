using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using SpeedMeter.App.Meter;
using SpeedMeter.App.State;
using SpeedMeter.Core;
using Forms = System.Windows.Forms;

namespace SpeedMeter.App;

/// <summary>
/// The entry point of one exe that runs as two processes.
/// </summary>
/// <remarks>
/// <para><b>The meter</b> (the default): sampling, recording, the taskbar
/// text, the tray icon and the details flyout, on a WinForms message loop -
/// the C# meter's windows, carried over with their measured fixes. It runs all
/// day, so it never loads WinUI and stays the size of that meter.</para>
/// <para><b>The dashboard</b> (<c>--dashboard</c>): the WinUI window, started by
/// the meter when it is asked for and gone when it closes, taking WinUI's
/// hundred-plus megabytes with it (see <see cref="MeterLink"/>). A speed test
/// still running when it closes keeps it alive, out of sight, until done.</para>
/// <para>Each is a single instance. A second launch of the meter (the Start
/// menu, a double-click) hands over to the running one, which opens the
/// dashboard; a second dashboard hands its page to the open one.</para>
/// <para>The app replaces the C# meter, so the two must never record at once:
/// both would write every second into the same history. The meter holds the
/// old meter's single-instance mutex, so the old one, started later, exits
/// silently; an old meter already running is offered a stop.</para>
/// </remarks>
public static class Program
{
    /// <summary>The C# meter's single-instance mutex.</summary>
    private const string LegacyMutexName = @"Local\InternetSpeedMeter.SingleInstance";

    private static Mutex? _legacyMutex;
    private static readonly CancellationTokenSource Stopping = new();

    /// <summary>"Main", or a hash of the settings folder for a demo copy running beside the real one.</summary>
    public static string InstanceSuffix { get; } = ComputeSuffix();

    /// <summary>The app's preferences: read by both processes, written by either.</summary>
    public static UiSettings Ui { get; private set; } = new();

    /// <summary>The meter, in the meter process.</summary>
    internal static MeterHost? Meter { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("unhandled", e.ExceptionObject as Exception);
        if (Preview(args)) return 0;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        return args.Contains("--dashboard", StringComparer.OrdinalIgnoreCase) ? DashboardMain(args) : MeterMain(args);
    }

    private static string ComputeSuffix()
    {
        if (Environment.GetEnvironmentVariable(AppPaths.SettingsDirVariable) is not { Length: > 0 }) return "Main";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.SettingsDir.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>The page asked for on a command line: "--page test", or "--test" for short.</summary>
    internal static string? PageFrom(IEnumerable<string> args)
    {
        var list = args.ToList();
        if (list.Contains("--test", StringComparer.OrdinalIgnoreCase)) return "test";
        var at = list.FindIndex(a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < list.Count ? list[at + 1].ToLowerInvariant() : null;
    }

    /// <summary>Splits a command line the way the process received it (no quoting is ever needed here).</summary>
    internal static string[] SplitArguments(string? line) => (line ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /* ------------------------------------------------------------- Meter */

    private static int MeterMain(string[] args)
    {
        var instance = AppInstance.FindOrRegisterForKey("InternetSpeedMeterNative.Meter." + InstanceSuffix);
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }
        // A second launch of the meter is someone asking for the window.
        instance.Activated += (_, e) => OpenDashboard(e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? PageFrom(SplitArguments(launch.Arguments)) : null);

        if (!AppPaths.IsRedirected && !TakeOverFromOldMeter()) return 0;

        Ui = UiSettings.Load();
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
        Forms.Application.ThreadException += (_, e) => Crash("meter", e.Exception);
        SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());

        using var host = new MeterHost(Ui);
        Meter = host;
        MeterLink.Serve(command => host.Post(() => host.Command(command)), Stopping.Token);

        // Started at login: the meter only, until the dashboard is asked for.
        if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) OpenDashboard(PageFrom(args));

        Forms.Application.Run(host);
        Stopping.Cancel();
        return 0;
    }

    /// <summary>
    /// Takes the C# meter's mutex, stopping that meter first if it is running
    /// and the user agrees. False means do not start.
    /// </summary>
    private static bool TakeOverFromOldMeter()
    {
        _legacyMutex = new Mutex(true, LegacyMutexName, out var created);
        if (created) return true;

        var old = Process.GetProcessesByName("InternetSpeedMeter");
        if (old.Length > 0)
        {
            Forms.Application.EnableVisualStyles();
            var answer = Forms.MessageBox.Show(
                "The older Internet Speed Meter is running. Only one meter can record at a time, or every second would be recorded twice.\n\n" +
                "Stop the older meter and start this one? Its history carries on here: at most the minute it is in the middle of is lost.",
                "Internet Speed Meter", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Information, Forms.MessageBoxDefaultButton.Button1);
            if (answer != Forms.DialogResult.Yes) return false;
            foreach (var process in old)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        try
        {
            if (_legacyMutex.WaitOne(TimeSpan.FromSeconds(5))) return true;
        }
        catch (AbandonedMutexException)
        {
            // The old meter was stopped holding it: ours now.
            return true;
        }
        Forms.MessageBox.Show("Another copy of the meter is still recording. Exit it from its tray icon, then start this again.",
            "Internet Speed Meter", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        return false;
    }

    /// <summary>
    /// Opens the dashboard on <paramref name="page"/> ("speed", "test") when
    /// given: a new dashboard process, which hands itself to one already open.
    /// Any thread.
    /// </summary>
    public static void OpenDashboard(string? page)
    {
        try
        {
            var arguments = page is null ? "--dashboard" : $"--dashboard --page {page}";
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Crash("open dashboard", ex);
        }
    }

    /* --------------------------------------------------------- Dashboard */

    private static App? _app;

    private static int DashboardMain(string[] args)
    {
        var instance = AppInstance.FindOrRegisterForKey("InternetSpeedMeterNative.Dashboard." + InstanceSuffix);
        if (!instance.IsCurrent)
        {
            instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }
        instance.Activated += (_, e) => _app?.Show(e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? PageFrom(SplitArguments(launch.Arguments)) : null);

        Ui = UiSettings.Load();
        // The window without a meter would show a history that is not being
        // recorded: start one, in the tray, beside it.
        if (!MeterLink.IsRunning && !AppPaths.IsRedirected)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--tray") { UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Crash("start meter", ex);
            }
        }

        var page = PageFrom(args);
        Microsoft.UI.Xaml.Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _app = new App(page);
        });
        return 0;
    }

    /* -------------------------------------------------------------- Exit */

    /// <summary>
    /// Exits the whole app. From the meter: write the minute in hand and end.
    /// From the dashboard: tell the meter to, then close the window's process.
    /// </summary>
    public static void Exit()
    {
        if (Meter is { } meter)
        {
            meter.Shutdown();
            Environment.Exit(0);
        }
        MeterLink.Send("exit");
        Environment.Exit(0);
    }

    /// <summary>Writes an exception to the logs folder. Never throws.</summary>
    public static void Crash(string where, Exception? ex) => AppPaths.Log("crash.log", $"{where}: {ex}");

    /* ----------------------------------------------------------- Preview */

    private static bool Preview(string[] args)
    {
        var mode = args.FirstOrDefault();
        if (mode is not ("--preview-icon" or "--preview-flyout")) return false;
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        var path = Path.GetFullPath(args.Length > 1 ? args[1] : mode == "--preview-icon" ? "icon-preview.png" : "flyout-preview.png");
        if (mode == "--preview-icon") IconPreview.Icons(path);
        else IconPreview.Flyout(path);
        if (Native.AttachParentConsole()) Console.WriteLine("wrote " + path);
        return true;
    }
}

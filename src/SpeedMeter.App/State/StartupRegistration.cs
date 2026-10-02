using Microsoft.Win32;

namespace SpeedMeter.App.State;

/// <summary>
/// Start with Windows, through the per-user Run key: no admin rights, and it
/// shows in Task Manager's Startup apps where it can be turned off like any
/// other. It starts straight to the tray; the dashboard opens when asked.
/// </summary>
/// <remarks>
/// The value's NAME is the one the C# meter used, on purpose: turning this on
/// replaces that meter's entry rather than adding a second meter beside it.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "InternetSpeedMeter";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    /// <summary>On, and pointing at THIS exe.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value
                       && value.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>False when the registry refused, so the menus can stay honest.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

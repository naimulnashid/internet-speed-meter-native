using System.Runtime.CompilerServices;

namespace SpeedMeter.Core.Tests;

/// <summary>
/// Points both of the app's folders into TEMP before any test runs, so a test
/// that trips the recorder's error log can never write beside the real
/// settings.ini.
/// </summary>
internal static class Isolation
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "speedmeter-tests", "app-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable(AppPaths.SettingsDirVariable, Path.Combine(root, "settings"));
        Environment.SetEnvironmentVariable(AppPaths.LocalDirVariable, Path.Combine(root, "local"));
    }
}

namespace SpeedMeter.Core;

/// <summary>
/// Where things live. Two folders, chosen for what losing them costs.
/// </summary>
/// <remarks>
/// <para><b>The meter's own folder</b>, <c>%AppData%\InternetSpeedMeter</c>,
/// holds <c>settings.ini</c>: the same file, in the same format, that the
/// C# meter this app replaces wrote. Keeping it is what makes the switch a
/// continuation. The folders the history is recorded in are named there, so
/// this app records into the history the old one was writing and reads back
/// every minute of it.</para>
/// <para><b>The app's local folder</b>, <c>%LocalAppData%\Internet Speed Meter
/// Native</c>, holds what is cheap to lose: window preferences and logs.</para>
/// <para>The history itself lives wherever settings.ini says: by default under
/// <c>%LocalAppData%\InternetSpeedMeter</c>, as before.</para>
/// <para><c>SPEEDMETER_SETTINGS_DIR</c> and <c>SPEEDMETER_LOCAL_DIR</c> move
/// both folders. <b>Anything run on demo data must set both</b>, or it reads
/// or writes the real ones.</para>
/// </remarks>
public static class AppPaths
{
    public const string SettingsDirVariable = "SPEEDMETER_SETTINGS_DIR";
    public const string LocalDirVariable = "SPEEDMETER_LOCAL_DIR";

    /// <summary>The folder holding settings.ini, and error.log beside it.</summary>
    public static string SettingsDir =>
        Override(SettingsDirVariable)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetSpeedMeter");

    public static string SettingsFile => Path.Combine(SettingsDir, "settings.ini");

    /// <summary>Recorder failures, beside settings.ini as the old meter kept them.</summary>
    public static string ErrorLog => Path.Combine(SettingsDir, "error.log");

    /// <summary>Window preferences and logs: nothing worth backing up.</summary>
    public static string LocalDir =>
        Override(LocalDirVariable)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Internet Speed Meter Native");

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>True when either folder has been moved away from the real ones (a demo or a test run).</summary>
    public static bool IsRedirected => Override(SettingsDirVariable) is not null || Override(LocalDirVariable) is not null;

    private static string? Override(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(value))
            : null;

    /// <summary>Appends one line to a log in <see cref="LogsDir"/>. Never throws.</summary>
    public static void Log(string file, string line)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            File.AppendAllText(Path.Combine(LogsDir, file), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

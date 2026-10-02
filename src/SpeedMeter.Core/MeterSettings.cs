using System.Globalization;

namespace SpeedMeter.Core;

public enum UnitMode
{
    Bytes,
    Bits,
}

/// <summary>What the tray icon shows: both rates stacked, or one rate at double the size.</summary>
public enum IconLayout
{
    Both,
    DownloadOnly,
    UploadOnly,
}

/// <summary>Which end of the taskbar the text sits at.</summary>
public enum TaskbarSide
{
    Left,
    Right,
}

/// <summary>The taskbar text's colour: matched to the taskbar, or forced.</summary>
public enum TextTheme
{
    Auto,
    Light,
    Dark,
}

/// <summary>
/// settings.ini: plain key=value lines in <see cref="AppPaths.SettingsDir"/>.
/// </summary>
/// <remarks>
/// <para>The format and every default are the C# meter's, unchanged, so this
/// app picks up exactly where it left off. Keys this app does not know are
/// kept and written back rather than dropped.</para>
/// <para>A damaged file never stops the app: whatever cannot be parsed falls
/// back to its default.</para>
/// </remarks>
public sealed class MeterSettings
{
    public const string AdapterAuto = "auto";
    public const string AdapterAll = "all";

    public string Adapter { get; set; } = AdapterAuto;
    public UnitMode Units { get; set; } = UnitMode.Bytes;
    public TextTheme Theme { get; set; } = TextTheme.Auto;
    public bool ShowUnitOnIcon { get; set; } = true;
    public bool UploadOnTop { get; set; }
    public IconLayout Layout { get; set; } = IconLayout.Both;

    /// <summary>
    /// Taskbar text is the readable default; the tray icon then carries the
    /// app's mark rather than duplicating the numbers.
    /// </summary>
    public bool TaskbarText { get; set; } = true;
    public bool TrayNumbers { get; set; }
    public TaskbarSide Side { get; set; } = TaskbarSide.Left;

    /// <summary>
    /// Recording. The default lives under %LocalAppData%, the one folder every
    /// Windows machine has. It is on the system drive, which a Windows reset
    /// destroys, so a history meant to outlive a reset points
    /// <see cref="LogFolder"/> at another drive. A configured folder that
    /// cannot be written stops recording and says so; it is never quietly
    /// relocated to somewhere the reader is not looking.
    /// </summary>
    public bool Record { get; set; } = true;
    public string LogFolder { get; set; } = DefaultFolder("history");

    /// <summary>
    /// One-second samples, kept apart from the minute rollups because they
    /// differ in retention and worth: rewritten every second and dropped after
    /// <see cref="RawRetentionDays"/>, so they should not follow the history
    /// into a synced folder. Empty means minute rollups only.
    /// </summary>
    public string RawFolder { get; set; } = DefaultFolder("raw");
    public int RawRetentionDays { get; set; } = 14;

    /// <summary>
    /// Combined down+up bytes/s at or above which a second counts as active.
    /// Recorded per minute rather than decided at read time, so changing it
    /// never rewrites what the past looked like.
    /// </summary>
    public long ActiveThresholdBps { get; set; } = 50 * 1024;

    /// <summary>Lines of the file this app does not understand, written back unchanged.</summary>
    private List<string> _unknown = [];

    private static string DefaultFolder(string leaf)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // A profile with no LocalAppData is not a machine to start writing guesses on.
        return local.Length > 0 ? Path.Combine(local, "InternetSpeedMeter", leaf) : "";
    }

    public static MeterSettings Load() => Load(AppPaths.SettingsFile);

    public static MeterSettings Load(string path)
    {
        var s = new MeterSettings();
        string[] lines;
        try
        {
            if (!File.Exists(path)) return s;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return s;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "adapter":
                    s.Adapter = value.Length == 0 ? AdapterAuto : value;
                    break;
                case "units":
                    s.Units = value.Equals("bits", StringComparison.OrdinalIgnoreCase) ? UnitMode.Bits : UnitMode.Bytes;
                    break;
                case "theme":
                    s.Theme = value.ToLowerInvariant() switch { "light" => TextTheme.Light, "dark" => TextTheme.Dark, _ => TextTheme.Auto };
                    break;
                case "showuniticon":
                    s.ShowUnitOnIcon = ParseBool(value, true);
                    break;
                case "uploadontop":
                    s.UploadOnTop = ParseBool(value, false);
                    break;
                case "taskbartext":
                    s.TaskbarText = ParseBool(value, true);
                    break;
                case "traynumbers":
                    s.TrayNumbers = ParseBool(value, false);
                    break;
                case "side":
                    s.Side = value.Equals("right", StringComparison.OrdinalIgnoreCase) ? TaskbarSide.Right : TaskbarSide.Left;
                    break;
                case "record":
                    s.Record = ParseBool(value, true);
                    break;
                case "logfolder":
                    // Blanking the folder the history lives in is far more likely to be an
                    // accident than a request to stop recording; record=0 says that.
                    if (value.Length > 0) s.LogFolder = Environment.ExpandEnvironmentVariables(value);
                    break;
                case "rawfolder":
                    // Empty is meaningful here: it is how minutes-only is asked for.
                    s.RawFolder = Environment.ExpandEnvironmentVariables(value);
                    break;
                case "rawretentiondays":
                    s.RawRetentionDays = ParseInt(value, s.RawRetentionDays, 0, 3650);
                    break;
                case "activethresholdbps":
                    s.ActiveThresholdBps = ParseInt(value, (int)s.ActiveThresholdBps, 0, int.MaxValue);
                    break;
                case "layout":
                    s.Layout = value.ToLowerInvariant() switch { "down" => IconLayout.DownloadOnly, "up" => IconLayout.UploadOnly, _ => IconLayout.Both };
                    break;
                default:
                    s._unknown.Add(raw);
                    break;
            }
        }
        return s;
    }

    /// <summary>Writes the file. False when it could not be written; the app carries on with what it holds.</summary>
    public bool Save() => Save(AppPaths.SettingsFile);

    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>
            {
                "# Internet Speed Meter settings",
                "adapter=" + Adapter,
                "units=" + (Units == UnitMode.Bits ? "bits" : "bytes"),
                "theme=" + Theme.ToString().ToLowerInvariant(),
                "showuniticon=" + (ShowUnitOnIcon ? "1" : "0"),
                "uploadontop=" + (UploadOnTop ? "1" : "0"),
                "taskbartext=" + (TaskbarText ? "1" : "0"),
                "traynumbers=" + (TrayNumbers ? "1" : "0"),
                "side=" + (Side == TaskbarSide.Right ? "right" : "left"),
                "layout=" + (Layout switch { IconLayout.DownloadOnly => "down", IconLayout.UploadOnly => "up", _ => "both" }),
                "record=" + (Record ? "1" : "0"),
                "logfolder=" + LogFolder,
                "rawfolder=" + RawFolder,
                "rawretentiondays=" + RawRetentionDays.ToString(CultureInfo.InvariantCulture),
                "activethresholdbps=" + ActiveThresholdBps.ToString(CultureInfo.InvariantCulture),
            };
            lines.AddRange(_unknown);
            // Written whole and swapped in, so a crash mid-write cannot leave half a file.
            var temp = path + ".tmp";
            File.WriteAllLines(temp, lines);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A copy that can be handed to another thread and read there without locking.</summary>
    public MeterSettings Clone()
    {
        var copy = (MeterSettings)MemberwiseClone();
        copy._unknown = [.. _unknown];
        return copy;
    }

    /// <summary>Takes every value of <paramref name="other"/>: settings.ini as another process just wrote it.</summary>
    public void CopyFrom(MeterSettings other)
    {
        Adapter = other.Adapter;
        Units = other.Units;
        Theme = other.Theme;
        ShowUnitOnIcon = other.ShowUnitOnIcon;
        UploadOnTop = other.UploadOnTop;
        Layout = other.Layout;
        TaskbarText = other.TaskbarText;
        TrayNumbers = other.TrayNumbers;
        Side = other.Side;
        Record = other.Record;
        LogFolder = other.LogFolder;
        RawFolder = other.RawFolder;
        RawRetentionDays = other.RawRetentionDays;
        ActiveThresholdBps = other.ActiveThresholdBps;
        _unknown = [.. other._unknown];
    }

    private static bool ParseBool(string value, bool fallback) => value.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" => true,
        "0" or "false" or "no" => false,
        _ => fallback,
    };

    private static int ParseInt(string value, int fallback, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
}

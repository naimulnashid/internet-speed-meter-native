using System.Text.Json;
using SpeedMeter.Core;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.State;

/// <summary>
/// The app's own preferences, in the local folder: cheap to lose, so they do
/// not belong with the history. An unreadable file means the defaults. The
/// meter's settings (units, the taskbar text, the folders) stay in settings.ini.
/// </summary>
/// <remarks>Read from both the meter thread and the window's; writes are serialised.</remarks>
public sealed class UiSettings
{
    private static readonly Lock SaveLock = new();

    /// <summary>"dark", "light" or "system". Dark by default, as the dashboard always was.</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Page zoom, 0.5 to 2: Ctrl+Plus / Ctrl+Minus / Ctrl+0, as in a browser.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>The page the window opens on: "speed" or "test".</summary>
    public string Page { get; set; } = "speed";

    /// <summary>The daily peak chart's range, in days.</summary>
    public int RangeDays { get; set; } = Ranges.Default;

    /// <summary>The speed test's size and connection choice, remembered between runs.</summary>
    public string TestProfile { get; set; } = "standard";

    public bool TestParallel { get; set; } = true;

    /// <summary>A notification when recording stops: the one failure that silently costs history.</summary>
    public bool NotifyRecording { get; set; } = true;

    /// <summary>A notification when a speed test finishes or fails while the window is closed.</summary>
    public bool NotifySpeedTests { get; set; } = true;

    /// <summary>Whether the "still running in the tray" note has been shown once.</summary>
    public bool TrayNoteShown { get; set; }

    private static string FilePath => Path.Combine(AppPaths.LocalDir, "app-settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new UiSettings();
    }

    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LocalDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A preference that did not stick is not worth an error.
            }
        }
    }
}

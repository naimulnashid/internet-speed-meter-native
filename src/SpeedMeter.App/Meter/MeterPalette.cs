using System.Drawing;
using Microsoft.Win32;
using SpeedMeter.Core;

namespace SpeedMeter.App.Meter;

/// <summary>
/// Colours for the taskbar text, the tray icon and the flyout. They follow the
/// TASKBAR's theme, not the app's: Windows themes the taskbar separately
/// (SystemUsesLightTheme), and the numbers have to read on it.
/// </summary>
internal sealed class MeterPalette
{
    public bool Dark { get; private init; }
    public Color Download { get; private init; }
    public Color Upload { get; private init; }
    public Color CardBack { get; private init; }
    public Color CardBorder { get; private init; }
    public Color Text { get; private init; }
    public Color TextDim { get; private init; }
    public Color GraphGrid { get; private init; }
    public Color Link { get; private init; }

    public static MeterPalette For(TextTheme mode)
    {
        // A light TASKBAR wants dark text.
        var dark = mode switch
        {
            TextTheme.Light => false,
            TextTheme.Dark => true,
            _ => !SystemUsesLightTaskbar(),
        };

        return dark
            ? new MeterPalette
            {
                Dark = true,
                Download = Color.FromArgb(0x6E, 0xE7, 0xA8),
                Upload = Color.FromArgb(0xFF, 0xC4, 0x6B),
                CardBack = Color.FromArgb(0x1F, 0x1F, 0x23),
                CardBorder = Color.FromArgb(0x3A, 0x3A, 0x42),
                Text = Color.FromArgb(0xF2, 0xF2, 0xF5),
                TextDim = Color.FromArgb(0x9A, 0x9A, 0xA6),
                GraphGrid = Color.FromArgb(0x2E, 0x2E, 0x36),
                Link = Color.FromArgb(0x8D, 0xF3, 0xBD),
            }
            : new MeterPalette
            {
                Dark = false,
                Download = Color.FromArgb(0x0B, 0x6B, 0x35),
                Upload = Color.FromArgb(0x9A, 0x54, 0x00),
                CardBack = Color.FromArgb(0xFA, 0xFA, 0xFC),
                CardBorder = Color.FromArgb(0xD8, 0xD8, 0xE0),
                Text = Color.FromArgb(0x1A, 0x1A, 0x1F),
                TextDim = Color.FromArgb(0x66, 0x66, 0x70),
                GraphGrid = Color.FromArgb(0xE6, 0xE6, 0xEC),
                Link = Color.FromArgb(0x0B, 0x6B, 0x35),
            };
    }

    /// <summary>
    /// Windows exposes taskbar theming separately from app theming; the tray
    /// follows SystemUsesLightTheme. A missing value means the classic dark taskbar.
    /// </summary>
    public static bool SystemUsesLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

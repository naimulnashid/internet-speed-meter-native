using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SpeedMeter.App.Theme;

/// <summary>
/// The colour tokens, per theme. Dark is the web dashboard's true-black OLED
/// ground; light is its own design, not the dark one inverted.
/// </summary>
/// <remarks>
/// <para><b>Two quantities, two colours.</b> Download is green and upload amber -
/// the same two the meter draws on the taskbar and in the tray - so a reader
/// never learns a second colour language moving from the taskbar into the
/// window. Download is the primary quantity, so it doubles as the accent.</para>
/// <para><b>This is the only place a colour exists.</b> Never write a hex in a
/// page or a chart.</para>
/// <para><b>How a theme switch reaches the screen.</b> Every <c>...Brush</c>
/// here is ONE shared object, retinted in place by <see cref="Apply"/>, so all
/// that is painted with it repaints at once. A <see cref="Color"/> read while
/// building is baked in, which is why the window also rebuilds the page.</para>
/// <para><b>Red is reserved for genuine anomalies</b> - recording stopped, a
/// test that failed - so it reads as signal, not decoration.</para>
/// <para><b>On white, a shade within ~1.1:1 of the card does not show</b>
/// (found on the sibling dashboards 2026-10-01): row hover, the chart's hover
/// band and skeleton bars are all measured to stay visible on both themes.</para>
/// </remarks>
public static class Palette
{
    public static Color Hex(string hex, double alpha = 1)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb((byte)Math.Round(alpha * 255), Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(a * 255), c.R, c.G, c.B);

    private sealed record Tokens(
        Color Bg, Color Surface, Color SurfaceHover, Color Inset, Color Skeleton, Color Border, Color BorderBright,
        Color Grid, Color Text, Color TextMuted, Color TextFaint, Color TooltipBg, Color Warn, Color Good,
        Color RowBorder, Color RowHover, Color Accent, Color AccentBright, Color AccentFill, Color OnAccentFill,
        Color Down, Color Up, Color HoverWash, double AccentDim, double AccentBorder, double AccentBorderStrong);

    private static readonly Tokens Dark = new(
        Bg: Hex("#000000"), Surface: Hex("#0a0a0b"), SurfaceHover: Hex("#101012"), Inset: Hex("#08080a"), Skeleton: Hex("#18181c"),
        Border: Hex("#1e1e22"), BorderBright: Hex("#2c2c33"), Grid: Hex("#1a1a1e"),
        Text: Hex("#f5f5f7"), TextMuted: Hex("#a1a1aa"), TextFaint: Hex("#7c7c88"), TooltipBg: Hex("#0c0c0e"),
        Warn: Hex("#ff4d4f"), Good: Hex("#22c55e"), RowBorder: Hex("#1e1e22", 0.6), RowHover: Hex("#101012"),
        // The meter's own dark palette: green down, amber up.
        Accent: Hex("#6ee7a8"), AccentBright: Hex("#8df3bd"), AccentFill: Hex("#6ee7a8"), OnAccentFill: Hex("#03140a"),
        Down: Hex("#6ee7a8"), Up: Hex("#ffc46b"), HoverWash: Hex("#ffffff", 0.04),
        AccentDim: 0.14, AccentBorder: 0.32, AccentBorderStrong: 0.46);

    /// <summary>
    /// The light theme: a pale grey canvas under white cards, a border plus a
    /// soft shadow for separation, hover as elevation, measured greys
    /// (text-muted 7.7:1, text-faint 5.3:1 on white), and the two quantities
    /// deepened until they read as text on white - the meter's light palette,
    /// green 5.6:1 and amber 5.2:1.
    /// </summary>
    private static readonly Tokens Light = new(
        Bg: Hex("#f4f5f7"), Surface: Hex("#ffffff"), SurfaceHover: Hex("#eceff4"), Inset: Hex("#eef0f3"), Skeleton: Hex("#e3e7ec"),
        Border: Hex("#e3e5ea"), BorderBright: Hex("#cfd2d9"), Grid: Hex("#eceef2"),
        Text: Hex("#16171a"), TextMuted: Hex("#52525b"), TextFaint: Hex("#6b6b76"), TooltipBg: Hex("#ffffff"),
        Warn: Hex("#b42318"), Good: Hex("#15803d"), RowBorder: Hex("#e3e5ea"), RowHover: Hex("#eceff4"),
        Accent: Hex("#0f7a3d"), AccentBright: Hex("#0b6b35"), AccentFill: Hex("#0f7a3d"), OnAccentFill: Hex("#ffffff"),
        Down: Hex("#0f7a3d"), Up: Hex("#a35a00"), HoverWash: Hex("#101828", 0.09),
        AccentDim: 0.10, AccentBorder: 0.30, AccentBorderStrong: 0.42);

    private static Tokens _t = Dark;

    /// <summary>Whether the light theme is applied.</summary>
    public static bool IsLight { get; private set; }

    public static Color Bg => _t.Bg;
    public static Color Surface => _t.Surface;
    public static Color SurfaceHover => _t.SurfaceHover;
    public static Color Border => _t.Border;
    public static Color BorderBright => _t.BorderBright;
    public static Color Text => _t.Text;
    public static Color TextMuted => _t.TextMuted;
    public static Color TextFaint => _t.TextFaint;
    public static Color Accent => _t.Accent;
    public static Color Down => _t.Down;
    public static Color Up => _t.Up;
    public static Color Warn => _t.Warn;

    public static readonly SolidColorBrush BgBrush = new();
    public static readonly SolidColorBrush SurfaceBrush = new();
    public static readonly SolidColorBrush SurfaceHoverBrush = new();
    public static readonly SolidColorBrush InsetBrush = new();
    public static readonly SolidColorBrush SkeletonBrush = new();
    public static readonly SolidColorBrush BorderBrush = new();
    public static readonly SolidColorBrush BorderBrightBrush = new();
    public static readonly SolidColorBrush GridBrush = new();
    public static readonly SolidColorBrush TextBrush = new();
    public static readonly SolidColorBrush TextMutedBrush = new();
    public static readonly SolidColorBrush TextFaintBrush = new();
    public static readonly SolidColorBrush TooltipBgBrush = new();
    public static readonly SolidColorBrush WarnBrush = new();
    public static readonly SolidColorBrush WarnDimBrush = new();
    public static readonly SolidColorBrush GoodBrush = new();
    public static readonly SolidColorBrush GoodDimBrush = new();
    public static readonly SolidColorBrush RowBorderBrush = new();
    public static readonly SolidColorBrush RowHoverBrush = new();
    public static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    public static readonly SolidColorBrush HoverWashBrush = new();

    public static readonly SolidColorBrush AccentBrush = new();
    public static readonly SolidColorBrush AccentBrightBrush = new();
    public static readonly SolidColorBrush AccentDimBrush = new();
    public static readonly SolidColorBrush AccentBorderBrush = new();
    public static readonly SolidColorBrush AccentBorderStrongBrush = new();
    public static readonly SolidColorBrush AccentFillBrush = new();
    public static readonly SolidColorBrush OnAccentFillBrush = new();
    public static readonly SolidColorBrush DownBrush = new();
    public static readonly SolidColorBrush UpBrush = new();
    public static readonly SolidColorBrush DownDimBrush = new();
    public static readonly SolidColorBrush UpDimBrush = new();

    /// <summary>Raised after <see cref="Apply"/> changed the theme.</summary>
    public static event Action? Changed;

    static Palette() => Paint();

    /// <summary>
    /// Switches theme: retints every shared brush in place, then raises
    /// <see cref="Changed"/> so the window can rebuild what baked a colour in.
    /// UI thread only.
    /// </summary>
    public static void Apply(bool light)
    {
        if (light == IsLight) return;
        IsLight = light;
        _t = light ? Light : Dark;
        Paint();
        Changed?.Invoke();
    }

    private static void Paint()
    {
        BgBrush.Color = _t.Bg;
        SurfaceBrush.Color = _t.Surface;
        SurfaceHoverBrush.Color = _t.SurfaceHover;
        InsetBrush.Color = _t.Inset;
        SkeletonBrush.Color = _t.Skeleton;
        BorderBrush.Color = _t.Border;
        BorderBrightBrush.Color = _t.BorderBright;
        GridBrush.Color = _t.Grid;
        TextBrush.Color = _t.Text;
        TextMutedBrush.Color = _t.TextMuted;
        TextFaintBrush.Color = _t.TextFaint;
        TooltipBgBrush.Color = _t.TooltipBg;
        WarnBrush.Color = _t.Warn;
        WarnDimBrush.Color = WithAlpha(_t.Warn, IsLight ? 0.10 : 0.14);
        GoodBrush.Color = _t.Good;
        GoodDimBrush.Color = WithAlpha(_t.Good, IsLight ? 0.10 : 0.14);
        RowBorderBrush.Color = _t.RowBorder;
        RowHoverBrush.Color = _t.RowHover;
        HoverWashBrush.Color = _t.HoverWash;
        AccentBrush.Color = _t.Accent;
        AccentBrightBrush.Color = _t.AccentBright;
        AccentDimBrush.Color = WithAlpha(_t.Accent, _t.AccentDim);
        AccentBorderBrush.Color = WithAlpha(_t.Accent, _t.AccentBorder);
        AccentBorderStrongBrush.Color = WithAlpha(_t.Accent, _t.AccentBorderStrong);
        AccentFillBrush.Color = _t.AccentFill;
        OnAccentFillBrush.Color = _t.OnAccentFill;
        DownBrush.Color = _t.Down;
        UpBrush.Color = _t.Up;
        DownDimBrush.Color = WithAlpha(_t.Down, _t.AccentDim);
        UpDimBrush.Color = WithAlpha(_t.Up, _t.AccentDim);
    }
}

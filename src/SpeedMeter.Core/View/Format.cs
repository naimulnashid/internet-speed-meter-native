using System.Globalization;

namespace SpeedMeter.Core.View;

/// <summary>
/// Formatting for an app about rates. Bits use powers of 1000 and bytes
/// powers of 1024, everywhere, so the taskbar readout and the pages agree to
/// the digit.
/// </summary>
/// <remarks>
/// Two decimal rules, kept from the two programs this app merges. The live
/// readouts (taskbar, tray, flyout) follow the meter: whole bytes per second
/// have no decimals, and the digits follow the user's culture. The pages
/// follow the dashboard: 2, 1 or 0 decimals by magnitude, always a point.
/// </remarks>
public static class Format
{
    private static readonly string[] ByteRate = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
    private static readonly string[] BitRate = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
    private static readonly string[] ByteRateShort = ["B", "K", "M", "G", "T"];
    private static readonly string[] BitRateShort = ["b", "K", "M", "G", "T"];
    private static readonly string[] Volume = ["B", "KB", "MB", "GB", "TB", "PB"];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Scale(double value, double step, int units, out double scaled)
    {
        var index = 0;
        scaled = value;
        while (Math.Abs(scaled) >= step && index < units - 1)
        {
            scaled /= step;
            index++;
        }
        return index;
    }

    /* ------------------------------------------------- Live (the meter) */

    /// <summary>Compact value for the tray icon: "9.4" and "M".</summary>
    public static (string Value, string Unit) SpeedShort(double bytesPerSecond, UnitMode units)
    {
        var bits = units == UnitMode.Bits;
        var table = bits ? BitRateShort : ByteRateShort;
        var index = Scale(bits ? bytesPerSecond * 8 : bytesPerSecond, bits ? 1000 : 1024, table.Length, out var scaled);
        var value = scaled < 0.05 ? "0"
            : scaled < 10 && index > 0 ? Num(scaled, 1)
            : Num(scaled, 0);
        return (value, table[index]);
    }

    /// <summary>Full value for the taskbar text, flyout and tooltip: "9.42 MB/s".</summary>
    public static string SpeedLong(double bytesPerSecond, UnitMode units)
    {
        var bits = units == UnitMode.Bits;
        var table = bits ? BitRate : ByteRate;
        var index = Scale(bits ? bytesPerSecond * 8 : bytesPerSecond, bits ? 1000 : 1024, table.Length, out var scaled);
        var decimals = index == 0 ? 0 : scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
        return Num(scaled, decimals) + " " + table[index];
    }

    /// <summary>A session total, always in bytes: "1.24 GB".</summary>
    public static string Total(double bytes)
    {
        var index = Scale(bytes, 1024, Volume.Length, out var scaled);
        var decimals = index == 0 ? 0 : scaled < 10 ? 2 : scaled < 100 ? 1 : 0;
        return Num(scaled, decimals) + " " + Volume[index];
    }

    private static string Num(double value, int decimals) => value.ToString("F" + decimals.ToString(Inv), CultureInfo.CurrentCulture);

    /* ------------------------------------------------- Pages (the dashboard) */

    private static int Decimals(double value)
    {
        var abs = Math.Abs(value);
        return abs >= 100 ? 0 : abs >= 10 ? 1 : 2;
    }

    /// <summary>A rate, split so the unit can be set smaller than the number.</summary>
    public static (string Value, string Unit) SplitRate(double bytesPerSecond, UnitMode units)
    {
        var bits = units == UnitMode.Bits;
        var table = bits ? BitRate : ByteRate;
        var index = Scale(bits ? bytesPerSecond * 8 : bytesPerSecond, bits ? 1000 : 1024, table.Length, out var scaled);
        return (scaled.ToString("F" + Decimals(scaled), Inv), table[index]);
    }

    public static string Rate(double bytesPerSecond, UnitMode units)
    {
        var (value, unit) = SplitRate(bytesPerSecond, units);
        return value + " " + unit;
    }

    /// <summary>An axis tick: whole numbers where they suffice ("100 Mbps", "2.5 MB/s").</summary>
    public static string RateTick(double bytesPerSecond, UnitMode units)
    {
        if (bytesPerSecond <= 0) return "0";
        var bits = units == UnitMode.Bits;
        var table = bits ? BitRate : ByteRate;
        var index = Scale(bits ? bytesPerSecond * 8 : bytesPerSecond, bits ? 1000 : 1024, table.Length, out var scaled);
        var text = Math.Abs(scaled - Math.Round(scaled)) < 0.05 ? Math.Round(scaled).ToString(Inv) : scaled.ToString("0.#", Inv);
        return text + " " + table[index];
    }

    /// <summary>A volume of traffic, always binary units, as Windows reports them.</summary>
    public static string Bytes(double bytes)
    {
        var index = Scale(bytes, 1024, Volume.Length, out var scaled);
        return scaled.ToString("F" + Decimals(scaled), Inv) + " " + Volume[index];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

    /// <summary>"2 Oct, 15:10", local time.</summary>
    public static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("d MMM, HH:mm", Inv);

    /// <summary>"2 Oct".</summary>
    public static string DayShort(DateTime localDay) => localDay.ToString("d MMM", Inv);

    /// <summary>"Thu 2 Oct 2026".</summary>
    public static string DayLong(DateTime localDay) => localDay.ToString("ddd d MMM yyyy", Inv);

    /// <summary>"23h 10m" / "4m 12s" / "9s".</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
        : $"{span.Seconds}s";

    public static string Ellipsize(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
        return maxChars <= 1 ? text[..1] : text[..(maxChars - 1)] + "…";
    }
}

namespace SpeedMeter.App.Theme;

/// <summary>
/// The page zoom level: Ctrl+Plus / Ctrl+Minus / Ctrl+0 and Ctrl+wheel, as in a
/// browser. The window shell applies it (Controls/ZoomBox); anything that
/// sizes itself from the WINDOW rather than its own box reads it here.
/// </summary>
public static class Zoom
{
    /// <summary>Chrome's steps between 50% and 200%, so the numbers feel familiar.</summary>
    public static readonly double[] Levels = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];

    public static double Level { get; private set; } = 1;

    public static event Action? Changed;

    /// <summary>Sets the level, snapped to the nearest step. True when it moved.</summary>
    public static bool Set(double level)
    {
        var snapped = Levels.OrderBy(l => Math.Abs(l - level)).First();
        if (snapped == Level) return false;
        Level = snapped;
        Changed?.Invoke();
        return true;
    }

    public static bool In() => Set(Levels.FirstOrDefault(l => l > Level + 1e-9, Levels[^1]));

    public static bool Out() => Set(Levels.LastOrDefault(l => l < Level - 1e-9, Levels[0]));

    public static bool Reset() => Set(1);

    /// <summary>"110%".</summary>
    public static string Label => $"{Math.Round(Level * 100)}%";
}

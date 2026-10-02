using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpeedMeter.App.Theme;

namespace SpeedMeter.App.Controls;

/// <summary>
/// Counts a figure up when a page loads or refreshes: fast out of the gate,
/// settling on the final value (easeOutExpo, 850ms). Figures use tabular
/// digits, so nothing shifts width while it runs. With animations turned off in
/// Windows it simply shows the value.
/// </summary>
public static class CountUp
{
    public static TextBlock Apply(TextBlock block, double value, Func<double, string> format, int durationMs = 850)
    {
        block.Text = format(value);
        if (!Motion.Enabled || value == 0) return block;

        var clock = Stopwatch.StartNew();
        block.Text = format(0);
        void Tick(object? sender, object e)
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / durationMs);
            var eased = t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);
            // Land on the value exactly, not on an interpolation that floating
            // point can miss by a hair.
            block.Text = format(t >= 1 ? value : value * eased);
            if (t >= 1) CompositionTarget.Rendering -= Tick;
        }
        block.Loaded += (_, _) =>
        {
            // A skeleton shows the final figure's width at once (Views/Skeleton).
            if (Views.Skeleton.Contains(block))
            {
                block.Text = format(value);
                return;
            }
            CompositionTarget.Rendering += Tick;
        };
        block.Unloaded += (_, _) => CompositionTarget.Rendering -= Tick;
        return block;
    }
}

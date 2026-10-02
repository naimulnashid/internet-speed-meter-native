using SpeedMeter.Core;
using SpeedMeter.Core.History;
using SpeedMeter.Core.SpeedTest;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.Views;

/// <summary>
/// Stand-in data for the loading skeletons (<see cref="Skeleton"/>): a page is
/// built by its own <c>Build()</c> from these, then turned into a skeleton, so
/// every height in it comes from the real layout code.
/// </summary>
/// <remarks>
/// Shaped like the demo history (tools and README screenshots use it): a
/// month of days, a full page of runs. What can differ from the real page is
/// only what depends on the data - how many rows a table has - and nothing
/// here is anyone's traffic.
/// </remarks>
public static class Placeholder
{
    private const double Mbps = 1_000_000 / 8.0;

    public static SpeedData Speed(UnitMode units, int rangeDays)
    {
        var range = Ranges.Parse(rangeDays);
        var today = DateTime.Now.Date;
        var at = DateTime.UtcNow;
        var days = Enumerable.Range(0, Math.Min(range, 120)).Reverse()
            .Select(i => (today.AddDays(-i), (DayStats?)new DayStats(today.AddDays(-i), new Peak((60 + i % 7 * 5) * Mbps, at), new Peak(18 * Mbps, at), 4_000_000_000, 300_000_000, 60_000)))
            .ToList();
        return new SpeedData(units, new Peak(94.2 * Mbps, at), new Peak(19.6 * Mbps, at), new Peak(98.7 * Mbps, at), new Peak(21.3 * Mbps, at),
            [new Sustained(10, 92.1 * Mbps, at), new Sustained(60, 88.4 * Mbps, at), new Sustained(300, 71.9 * Mbps, at)],
            days, range, today.AddDays(-119), 112, true, "", null);
    }

    public static TestData Test(UnitMode units)
    {
        var runs = Enumerable.Range(0, 25).Select(i => new RunRow(new SpeedTestResult
        {
            At = DateTime.UtcNow.AddDays(-i),
            Profile = "standard",
            Connections = 6,
            DownBps = 94.1 * Mbps,
            UpBps = 18.7 * Mbps,
            PeakDownBps = 99.8 * Mbps,
            LatencyMs = 21,
            JitterMs = 2.1,
            LoadedLatencyMs = 64,
            LoadedUpLatencyMs = 118,
            Loss = new LossResult(new LossCount(30, 0), new LossCount(180, 1), new LossCount(100, 0)),
            DownBytes = 104_857_600,
            UpBytes = 26_214_400,
            DownSeconds = 9.4,
            UpSeconds = 11.2,
        }, 98.3 * Mbps)).ToList();
        return new TestData(units, runs, 46, 1, 2);
    }
}

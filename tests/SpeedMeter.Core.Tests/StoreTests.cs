using SpeedMeter.Core.SpeedTest;

namespace SpeedMeter.Core.Tests;

public class StoreTests
{
    /// <summary>A line exactly as the web dashboard wrote it.</summary>
    private const string WebLine = """{"at":"2026-10-02T05:28:06.126Z","profile":"standard","server":"speed.cloudflare.com","connections":6,"loadedLatencyMs":55.1,"loadedUpLatencyMs":123.9,"loadedJitterMs":10.7,"loadedUpJitterMs":38.1,"loss":{"idle":{"sent":27,"lost":0},"down":{"sent":183,"lost":23},"up":{"sent":46,"lost":0}},"peakUpBps":6036676.39,"clientIsp":"Example ISP","clientAsn":64500,"clientPlace":"Somewhere","serverPlace":"Example City (EXA)","protocol":"HTTP/1.1","downBps":3629810.17,"upBps":4515160.16,"peakDownBps":4237250.27,"latencyMs":49.5,"jitterMs":8.6,"downBytes":73149786,"upBytes":26345472,"downSeconds":20.0016,"upSeconds":4.9117}""";

    /// <summary>An older line: no upload peak, no loss, a retired profile, a phone.</summary>
    private const string OldLine = """{"at":"2026-09-20T10:00:00.000Z","profile":"light","server":"speed.cloudflare.com","device":"phone","downBps":1000,"upBps":500,"peakDownBps":1200,"latencyMs":40,"jitterMs":2,"downBytes":10,"upBytes":5,"downSeconds":2,"upSeconds":1}""";

    [Fact]
    public void Reads_the_web_dashboards_lines_newest_first_and_skips_junk()
    {
        using var temp = new TempFolder();
        File.WriteAllText(SpeedTestStore.PathIn(temp.Path), OldLine + "\n{half a line\n" + WebLine + "\n");
        var runs = SpeedTestStore.ReadAll(temp.Path);
        Assert.Equal(2, runs.Count);
        var latest = runs[0];
        Assert.Equal(new DateTime(2026, 10, 2, 5, 28, 6, 126, DateTimeKind.Utc), latest.At.ToUniversalTime());
        Assert.Equal(23, latest.Loss!.Down.Lost);
        Assert.Equal(256, latest.Loss.Total.Sent);
        Assert.Equal("light", runs[1].Profile);
        Assert.Null(runs[1].PeakUpBps);
        Assert.Equal("phone", runs[1].Device);
        Assert.True(runs[1].TooShort);
    }

    [Fact]
    public void An_appended_run_reads_back_the_same()
    {
        using var temp = new TempFolder();
        var run = new SpeedTestResult
        {
            At = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            Profile = "thorough",
            Connections = 6,
            DownBps = 1,
            UpBps = 2,
            PeakDownBps = 3,
            LatencyMs = 4,
            JitterMs = 5,
            DownBytes = 6,
            UpBytes = 7,
            DownSeconds = 8,
            UpSeconds = 9,
        };
        SpeedTestStore.Append(temp.Path, run);
        var text = File.ReadAllText(SpeedTestStore.PathIn(temp.Path));
        Assert.DoesNotContain("loss", text);
        Assert.Contains("\"at\":\"2026-10-02T09:00:00Z\"", text);
        Assert.Equal(run, SpeedTestStore.ReadAll(temp.Path).Single());
    }
}

using SpeedMeter.Core.History;
using SpeedMeter.Core.Recording;
using SpeedMeter.Core.Sampling;

namespace SpeedMeter.Core.Tests;

public class RecorderTests
{
    private static MeterSettings Settings(TempFolder temp) => new()
    {
        LogFolder = temp.Sub("history"),
        RawFolder = temp.Sub("raw"),
        RawRetentionDays = 0,
        ActiveThresholdBps = 1000,
    };

    private static Reading Sample(long down, long up, double elapsed = 1.0, string id = "{A}") => new()
    {
        DownBytes = down,
        UpBytes = up,
        DownBytesPerSecond = down / elapsed,
        UpBytesPerSecond = up / elapsed,
        ElapsedSeconds = elapsed,
        Connected = true,
        SourceId = id,
        SourceName = "Wi-Fi",
    };

    [Fact]
    public void A_minute_round_trips_through_the_reader()
    {
        using var temp = new TempFolder();
        var settings = Settings(temp);
        var start = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        using (var recorder = new Recorder(settings))
        {
            for (var s = 0; s < 60; s++) recorder.Add(Sample(2000 + s, 100), start.AddSeconds(s));
            // The next minute's first sample flushes the first minute.
            recorder.Add(Sample(500, 50), start.AddSeconds(60));
        }

        var minutes = LogReader.ReadMinutes(settings.LogFolder, start.ToLocalTime().Date, start.ToLocalTime().Date.AddDays(1));
        Assert.Equal(2, minutes.Count);
        var first = minutes[0];
        Assert.Equal(start, first.Utc);
        Assert.Equal((ulong)Enumerable.Range(0, 60).Sum(s => 2000 + s), first.DownBytes);
        Assert.Equal(6000UL, first.UpBytes);
        Assert.Equal(2059U, first.MaxDownBps);
        Assert.Equal(60, first.Samples);
        Assert.Equal(60, first.ActiveSamples);
        Assert.Equal(1, first.Adapter);
        // The first minute a meter writes is marked, so a short minute reads as a start, not dropped ticks.
        Assert.NotEqual(0, first.Flags & LogFormat.FlagMeterStarted);
        Assert.Equal(0, minutes[1].Flags & LogFormat.FlagMeterStarted);

        var raw = 0;
        LogReader.ForEachRaw(settings.RawFolder, start.ToLocalTime().Date, start.ToLocalTime().Date.AddDays(1), (_, _, _, _, _, _) => raw++);
        Assert.Equal(61, raw);

        var table = AdapterTable.Read(settings.LogFolder);
        Assert.Single(table);
        Assert.Equal("{A}", table[0].Id);
    }

    [Fact]
    public void A_stretched_window_is_a_gap_with_no_bytes()
    {
        using var temp = new TempFolder();
        var settings = Settings(temp);
        var start = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        using (var recorder = new Recorder(settings))
        {
            recorder.Add(Sample(1000, 10), start);
            // A resume from sleep: an hour's bytes in one "sample".
            recorder.Add(Sample(900_000_000, 10, elapsed: 3600), start.AddSeconds(1));
        }
        var minute = Assert.Single(LogReader.ReadMinutes(settings.LogFolder, start.ToLocalTime().Date, start.ToLocalTime().Date));
        Assert.Equal(1000UL, minute.DownBytes);
        Assert.Equal(1000U, minute.MaxDownBps);
        Assert.NotEqual(0, minute.Flags & LogFormat.FlagGap);
    }

    [Fact]
    public void Existing_adapter_indices_are_kept()
    {
        using var temp = new TempFolder();
        var settings = Settings(temp);
        AdapterTable.Append(settings.LogFolder, 1, "{OLD}", "Ethernet");
        var start = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        using (var recorder = new Recorder(settings))
        {
            recorder.Add(Sample(1, 1, id: "{NEW}"), start);
        }
        var minute = Assert.Single(LogReader.ReadMinutes(settings.LogFolder, start.ToLocalTime().Date, start.ToLocalTime().Date));
        Assert.Equal(2, minute.Adapter);
    }

    [Fact]
    public void An_unwritable_folder_stops_recording_and_says_so()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "not-a-folder");
        File.WriteAllText(file, "");
        string? fault = null;
        var recorder = new Recorder(new MeterSettings { LogFolder = Path.Combine(file, "history"), RawFolder = "" });
        recorder.Faulted += f => fault = f;
        Assert.False(recorder.Enabled);
        Assert.NotNull(recorder.FaultMessage);
        Assert.Null(fault);
    }
}

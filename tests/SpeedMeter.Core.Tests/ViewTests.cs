using System.Globalization;
using SpeedMeter.Core.History;
using SpeedMeter.Core.SpeedTest;
using SpeedMeter.Core.View;

namespace SpeedMeter.Core.Tests;

public class ViewTests
{
    [Theory]
    [InlineData(1, 1, "1")]
    [InlineData(1, 5, "1 2 3 4 5")]
    [InlineData(1, 12, "1 2 3 … 12")]
    [InlineData(6, 12, "1 … 4 5 6 7 8 … 12")]
    [InlineData(4, 12, "1 2 3 4 5 6 … 12")]
    [InlineData(12, 12, "1 … 10 11 12")]
    public void Pager_shows_the_ends_and_two_either_side(int page, int count, string expected)
    {
        var items = Pages.Items(page, count).Select(i => i?.ToString(CultureInfo.InvariantCulture) ?? "…");
        Assert.Equal(expected, string.Join(' ', items));
    }

    [Theory]
    [InlineData(0, 25, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    public void Page_count_is_never_zero(int total, int size, int pages) => Assert.Equal(pages, Pages.Count(total, size));

    [Theory]
    [InlineData(29_250_000, UnitMode.Bits, "234", "Mbps")]
    [InlineData(1_536, UnitMode.Bytes, "1.50", "KB/s")]
    [InlineData(12, UnitMode.Bytes, "12.0", "B/s")]
    public void Rates_split_value_and_unit(double bps, UnitMode units, string value, string unit) =>
        Assert.Equal((value, unit), Format.SplitRate(bps, units));

    [Fact]
    public void Bits_are_decimal_and_bytes_binary()
    {
        Assert.Equal("1.00 Mbps", Format.Rate(125_000, UnitMode.Bits));
        Assert.Equal("1.00 MB/s", Format.Rate(1_048_576, UnitMode.Bytes));
        Assert.Equal("1.00 GB", Format.Bytes(1L << 30));
    }

    [Fact]
    public void Sustained_windows_reset_across_a_break()
    {
        using var temp = new TempFolder();
        var raw = temp.Sub("raw");
        var start = DateTime.Now.Date.AddHours(12).ToUniversalTime();
        var buffer = new List<byte>();
        void Add(int second, uint down)
        {
            var record = new byte[Recording.LogFormat.RawRecordBytes];
            Recording.LogFormat.WriteU32(record, 0, (uint)((start - DateTime.UnixEpoch).TotalSeconds + second));
            Recording.LogFormat.WriteU32(record, 4, down);
            Recording.LogFormat.WriteU16(record, 12, 1000);
            buffer.AddRange(record);
        }
        // Nine fast seconds, a two-second hole, then ten slow ones: no 10 s window
        // may average across the hole.
        for (var s = 0; s < 9; s++) Add(s, 1000);
        for (var s = 11; s < 21; s++) Add(s, 10);
        File.WriteAllBytes(Path.Combine(raw, Recording.LogFormat.RawFileName(start.ToLocalTime())), [.. buffer]);

        var sustained = HistoryStats.SustainedDown(raw, start.ToLocalTime().Date, start.ToLocalTime().Date)!;
        Assert.Equal(10, sustained[0].BytesPerSecond, 3);
        Assert.Equal(0, sustained[1].BytesPerSecond);
    }

    [Fact]
    public void Meter_comparison_needs_every_minute_of_the_run()
    {
        var end = new DateTime(2026, 10, 2, 9, 1, 10, DateTimeKind.Utc);
        var (first, last) = HistoryStats.MinutesOf(end, 20);
        var minutes = new Dictionary<long, MinuteRecord> { [last] = new(last, 0, 0, 5000, 0, 60, 60, 1, 1) };
        Assert.Null(HistoryStats.MeterPeakFor(minutes, end, 20));
        minutes[first] = new MinuteRecord(first, 0, 0, 7000, 0, 60, 60, 1, 1);
        Assert.Equal(7000, HistoryStats.MeterPeakFor(minutes, end, 20));
    }

    [Fact]
    public void Jitter_is_the_median_step()
    {
        // One stalled probe barely moves it.
        Assert.Equal(2, SpeedTestRunner.Jitter([30, 32, 30, 32, 300, 30, 32, 30, 32]));
        Assert.Equal(31, SpeedTestRunner.Median([30, 32, 31]));
    }
}

public class AdapterTests
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wi-Fi", "Ethernet 2", "Local Area Connection* 8", "Wi-Fi-QoS Packet Scheduler-0000",
    };

    [Theory]
    [InlineData("Wi-Fi-QoS Packet Scheduler-0000", true)]
    [InlineData("Wi-Fi-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet 2-WFP 802.3 MAC Layer LightWeight Filter-0001", true)]
    [InlineData("Local Area Connection* 8-QoS Packet Scheduler-0000", true)]
    [InlineData("Wi-Fi", false)]
    [InlineData("Ethernet 2", false)]
    // A real adapter whose name merely ends in digits is not a filter.
    [InlineData("Office-LAN-2024", false)]
    public void Filter_layers_of_another_adapter_are_not_adapters(string name, bool filter) =>
        Assert.Equal(filter, Sampling.NetMonitor.IsFilterLayer(name, Names));
}

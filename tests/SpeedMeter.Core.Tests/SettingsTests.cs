namespace SpeedMeter.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Reads_the_c_sharp_meters_file_and_keeps_what_it_does_not_know()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.ini");
        File.WriteAllLines(path,
        [
            "# Internet Speed Meter settings",
            "adapter=auto",
            "units=bits",
            "theme=auto",
            "side=right",
            "layout=down",
            "logfolder=D:\\History",
            "rawfolder=",
            "rawretentiondays=99999",
            "somethingnew=42",
        ]);
        var s = MeterSettings.Load(path);
        Assert.Equal(UnitMode.Bits, s.Units);
        Assert.Equal(TaskbarSide.Right, s.Side);
        Assert.Equal(IconLayout.DownloadOnly, s.Layout);
        Assert.Equal("D:\\History", s.LogFolder);
        // Empty raw folder is how minutes-only is asked for: it is kept, not defaulted.
        Assert.Equal("", s.RawFolder);
        Assert.Equal(3650, s.RawRetentionDays);

        Assert.True(s.Save(path));
        var lines = File.ReadAllLines(path);
        Assert.Contains("somethingnew=42", lines);
        Assert.Contains("side=right", lines);
    }

    [Fact]
    public void A_missing_file_means_the_defaults()
    {
        var s = MeterSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.ini"));
        Assert.True(s.TaskbarText);
        Assert.False(s.TrayNumbers);
        Assert.Equal(14, s.RawRetentionDays);
        Assert.Equal(50 * 1024, s.ActiveThresholdBps);
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SpeedMeter.Core;

namespace SpeedMeter.App.Meter;

/// <summary>
/// Development helpers that draw the meter's pieces to a PNG, so they can be
/// checked without squinting at the real tray:
/// <c>SpeedMeter.exe --preview-icon out.png</c> and <c>--preview-flyout out.png</c>.
/// </summary>
internal static class IconPreview
{
    private static readonly string[][] Samples = [["0", "0"], ["9.4K", "512"], ["1.2M", "88K"], ["118M", "9.9M"], ["999", "999"]];

    /// <summary>The tray icon at every DPI size, on dark and light taskbars, two-line then single-line, at 4x.</summary>
    public static void Icons(string path)
    {
        int[] sizes = [16, 20, 24, 32];
        const int Zoom = 4;
        const int Cell = 32 * Zoom + 16;
        using var sheet = new Bitmap(Samples.Length * Cell + 40, sizes.Length * 4 * Cell + 40);
        using var g = Graphics.FromImage(sheet);
        using var renderer = new IconRenderer();
        g.Clear(Color.FromArgb(0x80, 0x80, 0x88));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var row = 0;
        foreach (var single in new[] { false, true })
        {
            foreach (var dark in new[] { true, false })
            {
                var palette = MeterPalette.For(dark ? TextTheme.Dark : TextTheme.Light);
                var background = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);
                foreach (var size in sizes)
                {
                    for (var col = 0; col < Samples.Length; col++)
                    {
                        var x = 20 + col * Cell;
                        var y = 20 + row * Cell;
                        using (var back = new SolidBrush(background)) g.FillRectangle(back, x, y, 32 * Zoom, 32 * Zoom);
                        var icon = single
                            ? renderer.RenderSingle(Samples[col][0], palette.Download, size)
                            : renderer.Render(Samples[col][0], Samples[col][1], palette.Download, palette.Upload, size);
                        if (icon is null) continue;
                        using (icon)
                        using (var bitmap = icon.ToBitmap())
                        {
                            var drawn = size * Zoom;
                            var offset = (32 * Zoom - drawn) / 2;
                            g.DrawImage(bitmap, x + offset, y + offset, drawn, drawn);
                        }
                    }
                    row++;
                }
            }
        }
        sheet.Save(path, ImageFormat.Png);
    }

    /// <summary>The details flyout in both taskbar themes, with invented traffic.</summary>
    public static void Flyout(string path)
    {
        var random = new Random(7);
        var down = new double[60];
        var up = new double[60];
        for (var i = 0; i < down.Length; i++)
        {
            var wave = 0.5 + 0.5 * Math.Sin(i / 6.0);
            down[i] = wave * (6 * 1024 * 1024) * (0.6 + random.NextDouble() * 0.4);
            up[i] = wave * (700 * 1024) * (0.3 + random.NextDouble() * 0.7);
        }

        var shots = new List<Bitmap>();
        foreach (var mode in new[] { TextTheme.Dark, TextTheme.Light })
        {
            using var form = new FlyoutForm();
            form.SetPalette(MeterPalette.For(mode));
            form.UpdateData("Wi-Fi", down[^1], up[^1], 4.7 * 1024 * 1024 * 1024, 612.0 * 1024 * 1024, DateTime.Now.AddMinutes(-73),
                UnitMode.Bits, down, up, null);
            var shot = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(shot, new Rectangle(0, 0, form.Width, form.Height));
            shots.Add(shot);
        }

        const int Gap = 16;
        using var sheet = new Bitmap(shots.Sum(s => s.Width) + Gap * (shots.Count + 1), shots[0].Height + Gap * 2);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(0x80, 0x80, 0x88));
        var x = Gap;
        foreach (var shot in shots)
        {
            g.DrawImage(shot, x, Gap);
            x += shot.Width + Gap;
            shot.Dispose();
        }
        sheet.Save(path, ImageFormat.Png);
    }
}

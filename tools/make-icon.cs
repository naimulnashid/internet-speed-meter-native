#:package Svg.Skia@5.2.3
// Regenerates src/SpeedMeter.App/Assets/app.ico from src/SpeedMeter.App/Assets/app-icon.svg.
//
//   dotnet run tools/make-icon.cs
//
// The .ico is committed because the build embeds it in the exe, but it is a
// derived file: change the SVG, then run this. Nothing checks that the two
// still match, which is why the command lives next to them rather than in
// someone's memory.
using SkiaSharp;
using Svg.Skia;

var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath())!, ".."));
var source = Path.Combine(root, "src", "SpeedMeter.App", "Assets", "app-icon.svg");
var target = Path.Combine(root, "src", "SpeedMeter.App", "Assets", "app.ico");

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
var images = sizes.Select(size => (size, png: Render(source, size))).ToList();

using var output = File.Create(target);
using var writer = new BinaryWriter(output);
writer.Write((ushort)0);            // reserved
writer.Write((ushort)1);            // type: icon
writer.Write((ushort)images.Count);
var offset = 6 + 16 * images.Count;
foreach (var (size, png) in images)
{
    writer.Write((byte)(size >= 256 ? 0 : size));
    writer.Write((byte)(size >= 256 ? 0 : size));
    writer.Write((byte)0);          // palette
    writer.Write((byte)0);          // reserved
    writer.Write((ushort)1);        // planes
    writer.Write((ushort)32);       // bits per pixel
    writer.Write(png.Length);
    writer.Write(offset);
    offset += png.Length;
}
foreach (var (_, png) in images) writer.Write(png);
Console.WriteLine($"Wrote {target} ({string.Join(", ", sizes)} px)");

static byte[] Render(string path, int size)
{
    using var svg = new SKSvg();
    var picture = svg.Load(path) ?? throw new InvalidOperationException($"Could not read {path}");
    var bounds = picture.CullRect;
    var scale = size / Math.Max(bounds.Width, bounds.Height);
    using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static string ScriptPath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

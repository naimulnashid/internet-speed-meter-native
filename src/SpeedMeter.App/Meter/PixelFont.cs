using System.Drawing;
using System.Drawing.Drawing2D;

namespace SpeedMeter.App.Meter;

/// <summary>
/// Hand-drawn bitmap fonts for the 16 px tray icon. At that size an 8 px row
/// leaves a vector face about five pixels of ink, which no hinting rescues;
/// placing the pixels by hand keeps "118M" and "9.4K" readable. Larger icons
/// use the real font.
/// </summary>
/// <remarks>
/// Two heights: the 7-row set fills a 16 px icon's row, and the 5-row set is
/// the fallback when something has to fit in less. Both are three pixels wide
/// per glyph, because four characters plus gaps have to cross 16 px.
/// </remarks>
internal static class PixelFont
{
    public const int TallHeight = 7;
    public const int ShortHeight = 5;
    private const int Gap = 1;

    // Row-major bits, top row first, '1' = lit. Width is inferred from the length.
    private static readonly Dictionary<char, string> Tall = new()
    {
        ['0'] = "111" + "101" + "101" + "101" + "101" + "101" + "111",
        ['1'] = "010" + "110" + "010" + "010" + "010" + "010" + "111",
        ['2'] = "111" + "001" + "001" + "111" + "100" + "100" + "111",
        ['3'] = "111" + "001" + "001" + "111" + "001" + "001" + "111",
        ['4'] = "101" + "101" + "101" + "111" + "001" + "001" + "001",
        ['5'] = "111" + "100" + "100" + "111" + "001" + "001" + "111",
        ['6'] = "111" + "100" + "100" + "111" + "101" + "101" + "111",
        ['7'] = "111" + "001" + "001" + "010" + "010" + "010" + "010",
        ['8'] = "111" + "101" + "101" + "111" + "101" + "101" + "111",
        ['9'] = "111" + "101" + "101" + "111" + "001" + "001" + "111",
        ['.'] = "0" + "0" + "0" + "0" + "0" + "0" + "1",
        [','] = "0" + "0" + "0" + "0" + "0" + "1" + "1",
        ['K'] = "101" + "101" + "101" + "110" + "101" + "101" + "101",
        ['M'] = "101" + "111" + "111" + "101" + "101" + "101" + "101",
        ['G'] = "111" + "100" + "100" + "101" + "101" + "101" + "111",
        ['T'] = "111" + "010" + "010" + "010" + "010" + "010" + "010",
        ['B'] = "110" + "101" + "101" + "110" + "101" + "101" + "110",
        ['b'] = "100" + "100" + "100" + "110" + "101" + "101" + "110",
    };

    private static readonly Dictionary<char, string> Short = new()
    {
        ['0'] = "111" + "101" + "101" + "101" + "111",
        ['1'] = "010" + "110" + "010" + "010" + "111",
        ['2'] = "111" + "001" + "111" + "100" + "111",
        ['3'] = "111" + "001" + "111" + "001" + "111",
        ['4'] = "101" + "101" + "111" + "001" + "001",
        ['5'] = "111" + "100" + "111" + "001" + "111",
        ['6'] = "111" + "100" + "111" + "101" + "111",
        ['7'] = "111" + "001" + "010" + "010" + "010",
        ['8'] = "111" + "101" + "111" + "101" + "111",
        ['9'] = "111" + "101" + "111" + "001" + "111",
        ['.'] = "0" + "0" + "0" + "0" + "1",
        [','] = "0" + "0" + "0" + "1" + "1",
        ['K'] = "101" + "101" + "110" + "101" + "101",
        ['M'] = "101" + "111" + "111" + "101" + "101",
        ['G'] = "111" + "100" + "101" + "101" + "111",
        ['T'] = "111" + "010" + "010" + "010" + "010",
        ['B'] = "110" + "101" + "110" + "101" + "110",
        ['b'] = "100" + "100" + "110" + "101" + "110",
    };

    private static Dictionary<char, string> Table(bool tall) => tall ? Tall : Short;

    private static int Height(bool tall) => tall ? TallHeight : ShortHeight;

    /// <summary>Pixel width of the string, or -1 when a character has no glyph.</summary>
    public static int Measure(string text, bool tall)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var table = Table(tall);
        var height = Height(tall);
        var width = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!table.TryGetValue(text[i], out var glyph)) return -1;
            if (i > 0) width += Gap;
            width += glyph.Length / height;
        }
        return width;
    }

    /// <summary>
    /// Draws the string with each glyph row <paramref name="scaleY"/> pixels
    /// tall. Stretching only vertically is what makes a single-line 16 px icon
    /// fill its height: the glyphs are already as wide as 16 px allows.
    /// </summary>
    public static void Draw(Graphics g, string text, int x, int y, Color color, bool tall, int scaleY)
    {
        var table = Table(tall);
        var height = Height(tall);
        scaleY = Math.Max(1, scaleY);
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var brush = new SolidBrush(color))
        {
            var penX = x;
            foreach (var c in text)
            {
                var glyph = table[c];
                var width = glyph.Length / height;
                for (var row = 0; row < height; row++)
                    for (var col = 0; col < width; col++)
                        if (glyph[row * width + col] == '1') g.FillRectangle(brush, penX + col, y + row * scaleY, 1, scaleY);
                penX += width + Gap;
            }
        }
        g.SmoothingMode = previous;
    }
}

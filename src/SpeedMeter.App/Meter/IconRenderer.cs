using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SpeedMeter.App.Meter;

/// <summary>
/// Draws the two-line speed readout that becomes the tray icon. Cheap, but it
/// runs once a second forever, so results are cached by content and every
/// HICON is destroyed.
/// </summary>
internal sealed class IconRenderer : IDisposable
{
    private readonly Dictionary<int, Font> _fonts = [];
    private readonly Dictionary<int, Ink> _ink = [];

    // Two lines can stretch moderately; one line is an explicit "make it big".
    private const float TwoLineStretch = 1.8f;
    private const float SingleLineStretch = 2.6f;
    private readonly StringFormat _format;
    private readonly string _fontFamily;
    private string? _lastKey;
    private IntPtr _currentHandle;
    private IntPtr _retiredHandle;

    public IconRenderer()
    {
        // Rows are positioned by hand, so the origin stays at each glyph's top-left.
        _format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
        };
        _format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        _fontFamily = PickFamily();
    }

    /// <summary>Tahoma keeps its shape at 7-8 px far better than Segoe UI, which is what the tray needs at 100%.</summary>
    private static string PickFamily()
    {
        foreach (var name in new[] { "Tahoma", "Segoe UI", "Microsoft Sans Serif" })
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name;
            }
            catch (ArgumentException)
            {
            }
        }
        return FontFamily.GenericSansSerif.Name;
    }

    /// <summary>A new icon, or null when the content is unchanged and the caller should keep the one it has.</summary>
    public Icon? Render(string top, string bottom, Color topColor, Color bottomColor, int size)
    {
        var key = $"2|{size}|{top}|{bottom}|{topColor.ToArgb()}|{bottomColor.ToArgb()}";
        return Build(key, size, g =>
        {
            var rowHeight = size / 2f;
            DrawRow(g, top, topColor, new RectangleF(0, 0, size, rowHeight), size, TwoLineStretch);
            DrawRow(g, bottom, bottomColor, new RectangleF(0, rowHeight, size, rowHeight), size, TwoLineStretch);
        });
    }

    /// <summary>One metric across the whole icon: twice the row height, far larger glyphs.</summary>
    public Icon? RenderSingle(string text, Color color, int size)
    {
        var key = $"1|{size}|{text}|{color.ToArgb()}";
        return Build(key, size, g => DrawRow(g, text, color, new RectangleF(0, 0, size, size), size, SingleLineStretch));
    }

    /// <summary>Forgets the last content, so the next render draws even if nothing changed (a theme switch).</summary>
    public void Invalidate() => _lastKey = null;

    private Icon? Build(string key, int size, Action<Graphics> draw)
    {
        if (key == _lastKey) return null;
        _lastKey = key;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Sub-pixel smoothing means nothing on a transparent bitmap, and at
            // 16 px hinted bi-level rendering is simply easier to read.
            g.TextRenderingHint = size <= 17 ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;
            draw(g);
        }

        var handle = bitmap.GetHicon();
        // The handle from two generations back is certainly off the tray by now.
        if (_retiredHandle != IntPtr.Zero) Native.DestroyIcon(_retiredHandle);
        _retiredHandle = _currentHandle;
        _currentHandle = handle;
        return Icon.FromHandle(handle);
    }

    /// <summary>
    /// Fits one row of text. Width is almost always the binding constraint -
    /// four characters across 24 px leave 6 px each - so the font is picked by
    /// width, then the glyphs are stretched vertically to fill the row: the
    /// tall, condensed look every good tray meter ends up with. Digits and unit
    /// letters have no descenders, which is what makes the stretch safe.
    /// </summary>
    private void DrawRow(Graphics g, string text, Color color, RectangleF bounds, int iconSize, float maxStretch)
    {
        if (string.IsNullOrEmpty(text)) return;

        // A hairline between the two rows so they never read as one block; a
        // single line gets a slightly larger margin against the edges instead.
        var gap = bounds.Height > iconSize * 0.75f ? Math.Max(2f, iconSize / 8f) : iconSize >= 20 ? 2f : 1f;
        var targetInk = bounds.Height - gap;

        // Small icons get the bitmap font; anything larger has room for real type.
        if (iconSize < 20)
        {
            var tall = targetInk >= PixelFont.TallHeight;
            var pixelWidth = PixelFont.Measure(text, tall);
            if (pixelWidth > 0 && pixelWidth <= bounds.Width)
            {
                var glyphHeight = tall ? PixelFont.TallHeight : PixelFont.ShortHeight;
                // Whole-pixel vertical scaling only: half a source row would blur the edges.
                var scaleY = Math.Max(1, (int)Math.Floor(targetInk / glyphHeight));
                var drawn = glyphHeight * scaleY;
                var px = (int)Math.Round(bounds.X + (bounds.Width - pixelWidth) / 2f);
                var py = (int)Math.Round(bounds.Y + (bounds.Height - drawn) / 2f);
                PixelFont.Draw(g, text, px, py, color, tall, scaleY);
                return;
            }
        }

        var max = (int)Math.Round(bounds.Height * 1.6f);
        var min = Math.Max(5, iconSize / 4);
        for (var px = max; px >= min; px--)
        {
            var font = GetFont(px);
            var ink = MeasureInk(g, px);
            // Never start from a size that already overflows the row before stretching.
            if (ink.Height > targetInk && px > min) continue;

            var widths = new float[text.Length];
            var natural = 0f;
            for (var i = 0; i < text.Length; i++)
            {
                widths[i] = g.MeasureString(text[i].ToString(), font, PointF.Empty, _format).Width;
                natural += widths[i];
            }

            var tighten = 0f;
            var gaps = text.Length - 1;
            if (natural > bounds.Width && gaps > 0) tighten = Math.Min(1f, (natural - bounds.Width) / gaps);
            var finalWidth = natural - tighten * gaps;
            if (finalWidth > bounds.Width + 0.6f && px > min) continue;

            DrawStretched(g, text, font, color, bounds, widths, tighten, finalWidth, ink, targetInk, maxStretch);
            return;
        }
    }

    private void DrawStretched(Graphics g, string text, Font font, Color color, RectangleF bounds, float[] widths,
        float tighten, float finalWidth, Ink ink, float targetInk, float maxStretch)
    {
        // Cap the distortion: stretched too far, the digits stop reading as digits.
        var scaleY = ink.Height > 0.5f ? targetInk / ink.Height : 1f;
        scaleY = Math.Max(0.9f, Math.Min(maxStretch, scaleY));
        var rowCenter = bounds.Y + bounds.Height / 2f;
        var x = bounds.X + (bounds.Width - finalWidth) / 2f;

        // Scale about the row centre, then place the text so its ink lands on that centre.
        var state = g.Save();
        g.TranslateTransform(0f, rowCenter);
        g.ScaleTransform(1f, scaleY);
        var y = -(ink.Top + ink.Height / 2f);
        using (var brush = new SolidBrush(color))
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                // A typographic full stop at these sizes anti-aliases to almost
                // nothing, and "1.2M" misread as "12M" is a tenfold error.
                if (c is '.' or ',')
                {
                    var dot = Math.Max(1f, font.Size / 7f);
                    var dotY = y + ink.Top + ink.Height - dot;
                    g.FillRectangle(brush, (float)Math.Round(x), dotY, dot, dot);
                }
                else
                {
                    g.DrawString(c.ToString(), font, brush, (float)Math.Round(x), y, _format);
                }
                x += widths[i] - tighten;
            }
        }
        g.Restore(state);
    }

    /// <summary>Where the ink of the digit/unit glyph set actually sits inside a line box.</summary>
    private struct Ink
    {
        public float Top;
        public float Height;
    }

    // GDI+ line boxes include ascent, descent and leading, none of which these
    // glyphs use. Measuring the real ink once per size lets the row be filled.
    private Ink MeasureInk(Graphics g, int pixelHeight)
    {
        if (_ink.TryGetValue(pixelHeight, out var ink)) return ink;
        const string Sample = "0123456789.KMGTBb";
        var font = GetFont(pixelHeight);
        var box = g.MeasureString(Sample, font, PointF.Empty, _format);
        var width = (int)Math.Ceiling(box.Width) + 4;
        var height = (int)Math.Ceiling(box.Height) + 8;
        ink.Top = 0f;
        ink.Height = box.Height;
        using (var probe = new Bitmap(Math.Max(width, 8), Math.Max(height, 8)))
        {
            using (var pg = Graphics.FromImage(probe))
            {
                pg.Clear(Color.Transparent);
                pg.TextRenderingHint = g.TextRenderingHint;
                pg.DrawString(Sample, font, Brushes.White, 2f, 2f, _format);
            }
            int first = -1, last = -1;
            for (var y = 0; y < probe.Height; y++)
            {
                var lit = false;
                for (var x = 0; x < probe.Width && !lit; x++) lit = probe.GetPixel(x, y).A > 0;
                if (!lit) continue;
                if (first < 0) first = y;
                last = y;
            }
            if (first >= 0)
            {
                ink.Top = first - 2f;
                ink.Height = last - first + 1f;
            }
        }
        _ink[pixelHeight] = ink;
        return ink;
    }

    private Font GetFont(int pixelHeight)
    {
        if (_fonts.TryGetValue(pixelHeight, out var font)) return font;
        font = new Font(_fontFamily, pixelHeight, FontStyle.Regular, GraphicsUnit.Pixel);
        _fonts[pixelHeight] = font;
        return font;
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        _fonts.Clear();
        _format.Dispose();
        if (_retiredHandle != IntPtr.Zero) Native.DestroyIcon(_retiredHandle);
        if (_currentHandle != IntPtr.Zero) Native.DestroyIcon(_currentHandle);
        _retiredHandle = _currentHandle = IntPtr.Zero;
    }
}

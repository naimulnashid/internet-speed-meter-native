using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using SpeedMeter.Core;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.Meter;

/// <summary>
/// The borderless details panel a click on the readout opens: live rates, a
/// 60-second graph, session totals, and the way into the dashboard. Painted by
/// hand so it stays crisp at any DPI, and so it needs nothing of WinUI: the
/// dashboard's framework is only loaded once the dashboard is opened.
/// </summary>
internal sealed class FlyoutForm : Form
{
    private readonly float _scale;
    private MeterPalette _palette = MeterPalette.For(TextTheme.Auto);

    private Font _fontLabel = null!;
    private Font _fontValue = null!;
    private Font _fontUnit = null!;
    private Font _fontTiny = null!;
    private Font _fontLink = null!;

    private string _adapter = "";
    private double _down;
    private double _up;
    private double _totalDown;
    private double _totalUp;
    private DateTime _since = DateTime.Now;
    private UnitMode _units = UnitMode.Bytes;
    private double[] _historyDown = [];
    private double[] _historyUp = [];
    private string? _status;
    private double _peak;
    private RectangleF _dashboardLink;
    private RectangleF _testLink;
    private int _hover;

    public FlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        using (var g = CreateGraphics()) _scale = g.DpiX / 96f;
        ClientSize = new Size(S(300), S(248));
        BuildFonts();
    }

    /// <summary>"Open dashboard" was clicked.</summary>
    public event Action? OpenDashboard;

    /// <summary>"Run a speed test" was clicked.</summary>
    public event Action? OpenSpeedTest;

    private int S(double value) => (int)Math.Round(value * _scale);

    private void BuildFonts()
    {
        _fontLabel = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontValue = new Font("Segoe UI Semibold", S(22), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontUnit = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontTiny = new Font("Segoe UI", S(11), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontLink = new Font("Segoe UI Semibold", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    public void SetPalette(MeterPalette palette)
    {
        _palette = palette;
        BackColor = palette.CardBack;
        if (Visible) Invalidate();
    }

    public void UpdateData(string adapter, double down, double up, double totalDown, double totalUp, DateTime since,
        UnitMode units, double[] historyDown, double[] historyUp, string? status)
    {
        _adapter = adapter;
        _down = down;
        _up = up;
        _totalDown = totalDown;
        _totalUp = totalUp;
        _since = since;
        _units = units;
        _historyDown = historyDown;
        _historyUp = historyUp;
        _status = status;
        if (Visible) Invalidate();
    }

    /// <summary>
    /// Places the panel beside the click and clear of the taskbar. The working
    /// area is not enough: an auto-hiding taskbar reserves none, so the visible
    /// taskbar rectangle is subtracted explicitly.
    /// </summary>
    public void ShowNear(Point anchor)
    {
        var limit = Screen.FromPoint(anchor).WorkingArea;
        if (TaskbarWidget.TryGetVisibleTaskbarRect(out var bar))
        {
            if (bar.Width > bar.Height)
            {
                limit = bar.Top <= limit.Top + bar.Height / 2
                    ? Rectangle.FromLTRB(limit.Left, Math.Max(limit.Top, bar.Bottom), limit.Right, limit.Bottom)
                    : Rectangle.FromLTRB(limit.Left, limit.Top, limit.Right, Math.Min(limit.Bottom, bar.Top));
            }
            else
            {
                limit = bar.Left <= limit.Left + bar.Width / 2
                    ? Rectangle.FromLTRB(Math.Max(limit.Left, bar.Right), limit.Top, limit.Right, limit.Bottom)
                    : Rectangle.FromLTRB(limit.Left, limit.Top, Math.Min(limit.Right, bar.Left), limit.Bottom);
            }
        }

        var margin = S(8);
        var x = Math.Max(limit.Left + margin, Math.Min(anchor.X - Width / 2, limit.Right - Width - margin));
        var y = anchor.Y > limit.Top + limit.Height / 2 ? limit.Bottom - Height - margin : limit.Top + margin;
        // A limit smaller than the panel must not push it off-screen.
        Location = new Point(x, Math.Max(limit.Top, y));
        Invalidate();
        Show();
        Activate();
    }

    /// <summary>
    /// With the panel open, a click on the tray first deactivates the panel
    /// (hiding it) and only then arrives as a tray click. The caller uses this
    /// stamp to tell that apart from a genuine "open me".
    /// </summary>
    public DateTime HiddenAt { get; private set; }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) HiddenAt = DateTime.UtcNow;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
        else if (e.KeyCode == Keys.Enter) Fire(OpenDashboard);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = _dashboardLink.Contains(e.Location) ? 1 : _testLink.Contains(e.Location) ? 2 : 0;
        Cursor = hover != 0 ? Cursors.Hand : Cursors.Default;
        if (hover == _hover) return;
        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == 0) return;
        _hover = 0;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_dashboardLink.Contains(e.Location)) Fire(OpenDashboard);
        else if (_testLink.Contains(e.Location)) Fire(OpenSpeedTest);
        else Hide();
    }

    private void Fire(Action? action)
    {
        Hide();
        action?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(_palette.CardBack);
        using (var border = new Pen(_palette.CardBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var pad = S(14);
        var right = Width - pad;
        using var dim = new SolidBrush(_palette.TextDim);

        // Header: which adapter is metered, and how long the session has run.
        g.DrawString(Format.Ellipsize(_adapter, 26), _fontLabel, dim, pad, S(10));
        var uptime = Format.Duration(DateTime.Now - _since);
        var uptimeSize = g.MeasureString(uptime, _fontTiny);
        g.DrawString(uptime, _fontTiny, dim, right - uptimeSize.Width, S(11));

        var rowY = S(34);
        DrawRate(g, pad, rowY, true, _down);
        DrawRate(g, pad, rowY + S(38), false, _up);

        DrawGraph(g, new Rectangle(pad, S(112), Width - pad * 2, S(58)));

        var footY = S(178);
        g.DrawString($"Session   ▼ {Format.Total(_totalDown)}    ▲ {Format.Total(_totalUp)}", _fontTiny, dim, pad, footY);
        var peak = "peak " + Format.SpeedLong(_peak, _units);
        var peakSize = g.MeasureString(peak, _fontTiny);
        g.DrawString(peak, _fontTiny, dim, right - peakSize.Width, footY);

        // A status line only when something is wrong (recording stopped).
        if (_status is { Length: > 0 } status)
        {
            using var warn = new SolidBrush(_palette.Dark ? Color.FromArgb(0xFF, 0x7A, 0x7C) : Color.FromArgb(0xB4, 0x23, 0x18));
            g.DrawString(Format.Ellipsize(status, 46), _fontTiny, warn, pad, footY + S(17));
        }

        // The way into the dashboard, ruled off from the live part above.
        var linkY = Height - S(30);
        using (var rule = new Pen(_palette.CardBorder)) g.DrawLine(rule, 0, linkY - S(8), Width, linkY - S(8));
        _dashboardLink = DrawLink(g, "Open dashboard", pad, linkY, _hover == 1, alignRight: false);
        _testLink = DrawLink(g, "Run a speed test", right, linkY, _hover == 2, alignRight: true);
    }

    private RectangleF DrawLink(Graphics g, string text, float x, float y, bool hover, bool alignRight)
    {
        var size = g.MeasureString(text, _fontLink);
        var left = alignRight ? x - size.Width : x;
        using var brush = new SolidBrush(hover ? _palette.Text : _palette.Link);
        g.DrawString(text, _fontLink, brush, left, y);
        if (hover)
        {
            using var pen = new Pen(_palette.Text);
            g.DrawLine(pen, left + S(3), y + size.Height - S(2), left + size.Width - S(3), y + size.Height - S(2));
        }
        return new RectangleF(left - S(4), y - S(4), size.Width + S(8), size.Height + S(8));
    }

    private void DrawRate(Graphics g, int x, int y, bool download, double bytesPerSecond)
    {
        var arrow = S(9);
        using (var brush = new SolidBrush(download ? _palette.Download : _palette.Upload))
        {
            PointF[] triangle = download
                ? [new(x, y + S(9)), new(x + arrow, y + S(9)), new(x + arrow / 2f, y + S(9) + arrow)]
                : [new(x, y + S(9) + arrow), new(x + arrow, y + S(9) + arrow), new(x + arrow / 2f, y + S(9))];
            g.FillPolygon(brush, triangle);
        }

        var text = Format.SpeedLong(bytesPerSecond, _units);
        var split = text.IndexOf(' ');
        var value = split > 0 ? text[..split] : text;
        var unit = split > 0 ? text[(split + 1)..] : "";
        var textX = x + arrow + S(8);
        using (var brush = new SolidBrush(_palette.Text)) g.DrawString(value, _fontValue, brush, textX, y);
        var valueSize = g.MeasureString(value, _fontValue);
        using var dim = new SolidBrush(_palette.TextDim);
        g.DrawString(unit, _fontUnit, dim, textX + valueSize.Width - S(4), y + S(13));
    }

    private void DrawGraph(Graphics g, Rectangle area)
    {
        using (var grid = new SolidBrush(_palette.GraphGrid)) g.FillRectangle(grid, area);
        _peak = Math.Max(_historyDown.DefaultIfEmpty().Max(), _historyUp.DefaultIfEmpty().Max());
        // A floor keeps an idle graph flat instead of amplifying a few stray bytes.
        var scale = Math.Max(_peak, 8 * 1024.0);
        // Download first, upload on top: upload is usually the smaller series.
        DrawSeries(g, area, _historyDown, scale, _palette.Download);
        DrawSeries(g, area, _historyUp, scale, _palette.Upload);
    }

    private void DrawSeries(Graphics g, Rectangle area, double[] values, double scale, Color color)
    {
        if (values.Length < 2) return;
        // Newest sample at the right edge; a short history starts partway across.
        var step = area.Width / (float)(values.Length - 1);
        var line = new PointF[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var ratio = Math.Min(1.0, values[i] / scale);
            line[i] = new PointF(area.Left + i * step, area.Bottom - (float)(ratio * (area.Height - 2)) - 1);
        }
        var fill = new PointF[line.Length + 2];
        Array.Copy(line, fill, line.Length);
        fill[line.Length] = new PointF(area.Right, area.Bottom);
        fill[line.Length + 1] = new PointF(area.Left, area.Bottom);
        using (var brush = new SolidBrush(Color.FromArgb(70, color))) g.FillPolygon(brush, fill);
        using var pen = new Pen(color, Math.Max(1f, _scale)) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, line);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontLabel.Dispose();
            _fontValue.Dispose();
            _fontUnit.Dispose();
            _fontTiny.Dispose();
            _fontLink.Dispose();
        }
        base.Dispose(disposing);
    }
}

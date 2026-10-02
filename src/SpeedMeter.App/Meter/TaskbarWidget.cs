using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using SpeedMeter.Core;
using SpeedMeter.Core.View;

namespace SpeedMeter.App.Meter;

internal enum WidgetPlacement
{
    /// <summary>Drawn on the taskbar right now.</summary>
    Shown,

    /// <summary>The taskbar exists but is auto-hidden at the moment.</summary>
    TemporarilyHidden,

    /// <summary>No horizontal taskbar, or no room on it: the tray icon must carry the numbers.</summary>
    Unavailable,
}

/// <summary>
/// The readout as taskbar text, in the clock's font and size.
/// </summary>
/// <remarks>
/// <para>A tray icon is a square about 24 px across, so four characters get six
/// pixels each: clock-sized text cannot fit there however it is drawn. The
/// clock is not an icon; it is text with room around it. So this is a layered,
/// topmost, non-activating window parked in the empty taskbar space, painted
/// with per-pixel alpha so the taskbar's own background shows through.</para>
/// <para>The taskbar OWNS it, which is what keeps it above the bar even when
/// Start lifts the bar into a z-order band this process cannot reach (see
/// <see cref="EnsureOwnedByTaskbar"/>). Because of that, moves use
/// SWP_NOZORDER: re-ordering an owned window synchronises with Explorer's
/// thread, about 100 ms a call mid-animation.</para>
/// <para>Every choice here was measured on the C# meter this app replaces, and
/// is carried over unchanged. Read the comments before changing any of it.</para>
/// </remarks>
internal sealed class TaskbarWidget : Form
{
    private const int SidePadding = 10;
    private const int VerticalPadding = 4;
    private const int GapFromTray = 8;

    /// <summary>Posted by the watcher thread when the taskbar's geometry has changed.</summary>
    private const int WmFollow = Native.WM_APP + 1;

    /// <summary>Cadence the watcher falls back to when there is no compositor to pace it.</summary>
    private const int UncomposedFrameMs = 8;

    /// <summary>
    /// How long after the last movement the watcher keeps sampling every frame.
    /// Longer than the ~185 ms slide, so an animation is never throttled midway.
    /// </summary>
    private const int AnimationTailMs = 400;

    /// <summary>
    /// Sampling interval once the taskbar has gone quiet. Sleep rounds this up
    /// to the system tick, about 15 ms: one wakeup per 60 Hz frame instead of
    /// one per frame on a screen running four times that.
    /// </summary>
    private const int IdleIntervalMs = 8;

    private MeterPalette _palette = MeterPalette.For(TextTheme.Auto);
    private UnitMode _units = UnitMode.Bytes;
    private TaskbarSide _side = TaskbarSide.Left;
    private double _down;
    private double _up;
    private Font? _font;
    private int _fontPixels;
    private int _measuredWidth;
    private bool _placed;
    private bool _dirty = true;
    private int _lastX;
    private int _lastY;
    private int _lastWidth;
    private int _lastHeight;
    private bool _clickThrough;
    private IntPtr _clickThroughHandle;
    private IntPtr _trayHandle;
    private IntPtr _notifyHandle;
    private IntPtr _ownedHandle;
    private IntPtr _ownedTo;
    private Thread? _watcher;
    private volatile bool _watching;
    private IntPtr _followTarget;
    private int _followPending;

    public TaskbarWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Visible = false;
    }

    public event EventHandler? LeftClicked;

    public event EventHandler? RightClicked;

    public event EventHandler? DoubleClicked;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Never take focus from whatever the user is working in.</summary>
    protected override bool ShowWithoutActivation => true;

    public void SetPalette(MeterPalette palette)
    {
        if (ReferenceEquals(palette, _palette)) return;
        _palette = palette;
        _dirty = true;
    }

    public void SetUnits(UnitMode units)
    {
        if (units == _units) return;
        _units = units;
        _measuredWidth = 0;
        _dirty = true;
    }

    public void SetSide(TaskbarSide side) => _side = side;

    /// <summary>
    /// Takes the foreground on the PRESS, so the menu the release opens is a
    /// foreground window's menu.
    /// </summary>
    /// <remarks>
    /// The readout is WS_EX_NOACTIVATE - a speed meter must never pull the
    /// caret out of whatever is being typed - but a menu belonging to a
    /// background application is a menu nothing ever tells to close, so the
    /// foreground has to be asked for. The activation travels as messages that
    /// land once this thread is back in its loop. Asked for on the release, they
    /// land on top of the menu it just opened, which a dropdown reads as a click
    /// elsewhere and closes: the first right-click after losing the foreground
    /// opened nothing at all. The press is one trip round the loop earlier.
    /// </remarks>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) Native.SetForegroundWindow(Handle);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) LeftClicked?.Invoke(this, EventArgs.Empty);
        else if (e.Button == MouseButtons.Right) RightClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) DoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The taskbar rectangle, but only while it is really on screen. An
    /// auto-hiding taskbar reserves no working area, so the working area alone
    /// will happily place a window straight over it.
    /// </summary>
    public static bool TryGetVisibleTaskbarRect(out Rectangle bar)
    {
        bar = Rectangle.Empty;
        var tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray)) return false;
        if (!Native.GetWindowRect(tray, out var rect)) return false;
        bar = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var onScreen = Rectangle.Intersect(bar, Screen.FromRectangle(bar).Bounds);
        if (onScreen.IsEmpty) return false;
        // A parked auto-hidden bar keeps a couple of pixels on screen: not something to dodge.
        var horizontal = bar.Width > bar.Height;
        var visible = horizontal ? onScreen.Height : onScreen.Width;
        var full = horizontal ? bar.Height : bar.Width;
        if (visible < full * 0.6) return false;
        bar = onScreen;
        return true;
    }

    /// <summary>A new reading. The repaint itself happens in <see cref="Sync"/>.</summary>
    public WidgetPlacement Update(double downBytesPerSecond, double upBytesPerSecond)
    {
        _down = downBytesPerSecond;
        _up = upBytesPerSecond;
        _dirty = true;
        return Sync();
    }

    /// <summary>
    /// Rides the taskbar. An auto-hiding taskbar does not teleport: Windows
    /// animates its rectangle off and on screen over ~185 ms. So the widget
    /// adopts that rectangle every frame and inherits the same motion - no
    /// easing of its own to keep in step, and no threshold where it pops.
    /// Position-only changes move without repainting.
    /// </summary>
    public WidgetPlacement Sync()
    {
        if (!TryGetTaskbar(out var taskbar, out var anchor, out var visibleHeight))
        {
            HideWidget();
            return WidgetPlacement.Unavailable;
        }

        var height = taskbar.Height;
        if (height < 20 || taskbar.Width < 200)
        {
            HideWidget();
            return WidgetPlacement.Unavailable;
        }

        EnsureOwnedByTaskbar();
        EnsureFont(height);

        var width = _measuredWidth;
        var y = taskbar.Top;
        var edge = Math.Max(8, height / 6);

        // Windows 11 centres the taskbar buttons, leaving the left end empty and
        // the calmer place to read from; the right end butts against the tray.
        var x = _side == TaskbarSide.Left ? taskbar.Left + edge : anchor.Left - width - GapFromTray;

        // Never encroach on the notification area, whichever end we are anchored to.
        if (x < taskbar.Left || x + width > anchor.Left - 8)
        {
            HideWidget();
            return WidgetPlacement.Unavailable;
        }

        var resized = width != _lastWidth || height != _lastHeight;
        var moved = x != _lastX || y != _lastY;

        // Repaint only once a meaningful sliver is on screen: no point drawing into
        // the two-pixel stub of a parked taskbar, but early enough that nothing pops.
        var worthDrawing = visibleHeight >= height * 0.15;

        if (worthDrawing && (_dirty || !_placed || resized))
        {
            RenderFrame(width, height, x, y);
        }
        else if (_placed && moved)
        {
            // Moved without touching the z-order, which is where all the cost of
            // this call is: 100 ms a call, measured, on every frame of a slide.
            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        }

        _lastX = x;
        _lastY = y;
        _lastWidth = width;
        _lastHeight = height;
        if (worthDrawing) _dirty = false;

        var placement = visibleHeight >= height * 0.6 ? WidgetPlacement.Shown : WidgetPlacement.TemporarilyHidden;
        SetClickThrough(placement != WidgetPlacement.Shown);
        return placement;
    }

    /// <summary>
    /// Takes the readout out of the mouse's way while the taskbar is away.
    /// </summary>
    /// <remarks>
    /// A parked auto-hiding taskbar keeps a two-pixel sliver on screen, and
    /// that sliver is how Explorer notices the pointer and slides the bar back.
    /// The readout rides the bar down and covers its stretch of the sliver -
    /// and is hit-testable there, since every pixel carries alpha 1 - so the bar
    /// never came back at this end of the screen. WS_EX_TRANSPARENT takes the
    /// window out of hit-testing while the bar is away.
    /// </remarks>
    private void SetClickThrough(bool through)
    {
        var self = Handle;
        if (through == _clickThrough && self == _clickThroughHandle) return;
        var style = Native.GetWindowExStyle(self);
        var updated = through ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        if (updated != style) Native.SetWindowExStyle(self, updated);
        _clickThrough = through;
        _clickThroughHandle = self;
    }

    /// <summary>Starts or stops following the taskbar. Off, the widget is hidden and nothing watches.</summary>
    public void SetFollowing(bool following)
    {
        if (following == _watching)
        {
            if (!following) HideWidget();
            return;
        }

        _watching = following;
        if (following)
        {
            _watcher = new Thread(Watch) { IsBackground = true, Name = "taskbar-follow" };
            _watcher.Start();
            return;
        }

        var watcher = _watcher;
        _watcher = null;
        // A frame at worst; the thread checks the flag every time round.
        watcher?.Join(250);
        HideWidget();
    }

    /// <summary>
    /// Watches the taskbar's rectangle on the compositor's clock, and pokes the
    /// UI thread only when it has actually changed.
    /// </summary>
    /// <remarks>
    /// A WinForms timer cannot sample the slide: WM_TIMER is rounded up to whole
    /// system ticks and delivered only when the queue is otherwise empty, so a
    /// 16 ms timer measured a median period of 30 ms and a tail past 90 ms.
    /// DwmFlush blocks until the compositor finishes its next frame - exactly
    /// one frame at any refresh rate, asleep in between. It only watches; all
    /// moving and painting stays on the UI thread, reached by a posted message.
    /// </remarks>
    private void Watch()
    {
        var tray = IntPtr.Zero;
        var notify = IntPtr.Zero;
        var lastBar = new Native.RECT();
        var lastAnchor = new Native.RECT();
        var retryTick = 0;
        var movedTick = unchecked(Environment.TickCount - AnimationTailMs);

        while (_watching)
        {
            // Frame by frame only while something is moving.
            if (unchecked(Environment.TickCount - movedTick) < AnimationTailMs) WaitForFrame();
            else Thread.Sleep(IdleIntervalMs);

            if (tray == IntPtr.Zero || !Native.IsWindow(tray))
            {
                // No taskbar: Explorer is restarting. Looking every frame would be wasteful.
                if (unchecked(Environment.TickCount - retryTick) < 0) continue;
                retryTick = unchecked(Environment.TickCount + 500);
                tray = Native.FindWindow("Shell_TrayWnd", null);
                notify = IntPtr.Zero;
                if (tray == IntPtr.Zero) continue;
            }

            if (notify == IntPtr.Zero || !Native.IsWindow(notify)) notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);

            if (!Native.GetWindowRect(tray, out var bar))
            {
                tray = IntPtr.Zero;
                continue;
            }

            // The notification area moves without the bar moving when icons come and go.
            var anchor = new Native.RECT();
            if (notify != IntPtr.Zero) Native.GetWindowRect(notify, out anchor);

            if (Same(bar, lastBar) && Same(anchor, lastAnchor)) continue;
            lastBar = bar;
            lastAnchor = anchor;
            movedTick = Environment.TickCount;
            Poke();
        }
    }

    /// <summary>Asks the UI thread for a Sync, at most one outstanding at a time.</summary>
    private void Poke()
    {
        var target = Volatile.Read(ref _followTarget);
        if (target == IntPtr.Zero) return;
        if (Interlocked.CompareExchange(ref _followPending, 1, 0) != 0) return;
        // The window went away between the read and the post: let the next frame retry.
        if (!Native.PostMessage(target, WmFollow, IntPtr.Zero, IntPtr.Zero)) Interlocked.Exchange(ref _followPending, 0);
    }

    private static bool Same(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Waits out one composition frame; without a compositor DwmFlush returns at once, so it is backed by a sleep.</summary>
    private static void WaitForFrame()
    {
        var before = Stopwatch.GetTimestamp();
        if (!Native.DwmFlush())
        {
            Thread.Sleep(UncomposedFrameMs);
            return;
        }
        if (Stopwatch.GetTimestamp() - before < Stopwatch.Frequency / 1000) Thread.Sleep(1);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmFollow)
        {
            Interlocked.Exchange(ref _followPending, 0);
            if (_watching) Sync();
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Hands the taskbar ownership of this window.
    /// </summary>
    /// <remarks>
    /// Opening Start moves Shell_TrayWnd into a higher z-order band (band 1 to
    /// band 6, measured) so the bar draws over the menu's backdrop. A band
    /// outranks WS_EX_TOPMOST outright, and SetWindowBand is denied without
    /// UIAccess. An owned window, though, is always above its owner, whatever
    /// band the owner is in: the widget rides the taskbar's promotion instead
    /// of trying to out-rank it.
    /// </remarks>
    private void EnsureOwnedByTaskbar()
    {
        if (_trayHandle == IntPtr.Zero) return;
        var self = Handle;
        // Destroying a window destroys what it owns, so an Explorer restart takes
        // this window with the taskbar and WinForms hands back a fresh one.
        if (self != _ownedHandle)
        {
            _ownedHandle = self;
            _ownedTo = IntPtr.Zero;
            _placed = false;
            Volatile.Write(ref _followTarget, self);
        }
        if (_trayHandle == _ownedTo && Native.GetWindowOwner(self) == _trayHandle) return;
        Native.SetWindowOwner(self, _trayHandle);
        _ownedTo = _trayHandle;
    }

    private void HideWidget()
    {
        if (Visible) Visible = false;
        _placed = false;
    }

    /// <summary>
    /// The primary taskbar and the notification area inside it; both windows
    /// still exist on Windows 11 although the taskbar itself is XAML.
    /// <paramref name="visibleHeight"/> is how much of the bar is on screen: it
    /// never gates positioning, because hiding on a threshold is exactly what
    /// makes a widget pop instead of slide.
    /// </summary>
    private bool TryGetTaskbar(out Native.RECT taskbar, out Native.RECT anchor, out int visibleHeight)
    {
        taskbar = new Native.RECT();
        anchor = new Native.RECT();
        visibleHeight = 0;

        if (_trayHandle == IntPtr.Zero || !Native.IsWindow(_trayHandle))
        {
            _trayHandle = Native.FindWindow("Shell_TrayWnd", null);
            _notifyHandle = IntPtr.Zero;
        }

        var tray = _trayHandle;
        if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray)) return false;
        if (!Native.GetWindowRect(tray, out taskbar)) return false;

        // Only a horizontal taskbar has spare width beside the clock.
        if (taskbar.Width < taskbar.Height * 3) return false;

        var bounds = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        visibleHeight = Math.Max(0, Math.Min(taskbar.Bottom, bounds.Bottom) - Math.Max(taskbar.Top, bounds.Top));

        if (_notifyHandle == IntPtr.Zero || !Native.IsWindow(_notifyHandle))
            _notifyHandle = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (_notifyHandle != IntPtr.Zero && Native.GetWindowRect(_notifyHandle, out anchor) && anchor.Width > 0) return true;

        // No notification area: fall back to the taskbar's right edge.
        anchor = taskbar;
        anchor.Left = taskbar.Right - 8;
        return true;
    }

    /// <summary>
    /// Sizes type to the taskbar the way the clock does: two stacked lines
    /// filling the bar's height. The width is fixed to the widest reading, so
    /// the text never jitters as numbers change.
    /// </summary>
    private void EnsureFont(int taskbarHeight)
    {
        var lineHeight = (taskbarHeight - VerticalPadding * 2) / 2;
        var pixels = Math.Clamp((int)Math.Round(lineHeight * 0.78), 11, 26);
        if (_font is not null && pixels == _fontPixels && _measuredWidth > 0) return;

        _font?.Dispose();
        _fontPixels = pixels;
        _font = MakeFont(pixels);
        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        // The widest reading in either unit mode, so the box never has to resize.
        var widest = new[] { "888.8 MB/s", "888.8 Mbps", "888.8 KB/s" }.Max(s => g.MeasureString(s, _font).Width);
        _measuredWidth = (int)Math.Ceiling(widest) + ArrowWidth() + SidePadding * 2;
        _dirty = true;
    }

    /// <summary>Windows 11 sets the clock in Segoe UI Variable; Windows 10 has only Segoe UI.</summary>
    private static Font MakeFont(int pixels)
    {
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI", "Tahoma" })
        {
            try
            {
                using var family = new FontFamily(name);
                return new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
            }
        }
        return new Font(FontFamily.GenericSansSerif, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private int ArrowWidth() => (int)Math.Round(_fontPixels * 0.62) + 6;

    private void RenderFrame(int width, int height, int screenX, int screenY)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            // A layered window is hit-tested by its alpha, so fully transparent
            // pixels pass clicks through and only the glyphs would be clickable.
            // Alpha 1 is invisible on any background but still counts as the window.
            using (var hitTest = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(hitTest, 0, 0, width, height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Sub-pixel AA needs an opaque backdrop, which a layered window lacks.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var lineHeight = (height - VerticalPadding * 2) / 2f;
            DrawLine(g, true, _down, VerticalPadding, lineHeight);
            DrawLine(g, false, _up, VerticalPadding + lineHeight, lineHeight);
        }
        Commit(bitmap, screenX, screenY);
    }

    private void DrawLine(Graphics g, bool download, double bytesPerSecond, float top, float lineHeight)
    {
        var arrow = _fontPixels * 0.62f;
        var centre = top + lineHeight / 2f;
        using (var brush = new SolidBrush(download ? _palette.Download : _palette.Upload))
        {
            var half = arrow / 2f;
            float x = SidePadding;
            PointF[] triangle = download
                ? [new(x, centre - half * 0.7f), new(x + arrow, centre - half * 0.7f), new(x + half, centre + half * 0.9f)]
                : [new(x, centre + half * 0.7f), new(x + arrow, centre + half * 0.7f), new(x + half, centre - half * 0.9f)];
            g.FillPolygon(brush, triangle);
        }

        var text = Format.SpeedLong(bytesPerSecond, _units);
        var size = g.MeasureString(text, _font!);
        using var textBrush = new SolidBrush(_palette.Text);
        g.DrawString(text, _font!, textBrush, SidePadding + ArrowWidth(), centre - size.Height / 2f);
    }

    /// <summary>Pushes the bitmap to the layered window.</summary>
    private void Commit(Bitmap bitmap, int screenX, int screenY)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        var memoryDc = Native.CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = Native.SelectObject(memoryDc, hBitmap);
            var size = new Native.SIZE(bitmap.Width, bitmap.Height);
            var source = new Native.POINT(0, 0);
            var destination = new Native.POINT(screenX, screenY);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            if (!Visible) Visible = true;
            Native.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, Native.ULW_ALPHA);

            // First placement: shown and given its band before anything is on
            // screen. Every move after leaves the z-order to the ownership.
            if (!_placed)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, screenX, screenY, bitmap.Width, bitmap.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                _placed = true;
            }
            else if (Location.X != screenX || Location.Y != screenY)
            {
                Native.SetWindowPos(Handle, IntPtr.Zero, screenX, screenY, bitmap.Width, bitmap.Height, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
            }
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                Native.SelectObject(memoryDc, previous);
                Native.DeleteObject(hBitmap);
            }
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // Nothing to post to until the UI thread has built a window again.
        Volatile.Write(ref _followTarget, IntPtr.Zero);
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SetFollowing(false);
            _font?.Dispose();
            _font = null;
        }
        base.Dispose(disposing);
    }
}

using System.Runtime.InteropServices;

namespace SpeedMeter.App.Meter;

/// <summary>The Win32 calls the meter makes: taskbar geometry, layered windows, ownership.</summary>
internal static partial class Native
{
    public const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const int WM_APP = 0x8000;

    public const int ULW_ALPHA = 0x02;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    public const int GWLP_HWNDPARENT = -8;
    public const int GWL_EXSTYLE = -20;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr handle);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr window, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Brings a window to the front and activates it. WS_EX_NOACTIVATE stops a
    /// window being activated by a click; it does not stop it being activated
    /// when asked outright, which is what this is for.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static partial int DwmFlushNative();

    /// <summary>
    /// Blocks until the compositor has finished its next frame, or fails when
    /// nothing is composing (a remote session, DWM restarting) rather than waiting.
    /// </summary>
    public static bool DwmFlush()
    {
        try
        {
            return DwmFlushNative() == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    /// <summary>The window that owns this one, or zero.</summary>
    public static IntPtr GetWindowOwner(IntPtr window) => GetWindowLongPtr(window, GWLP_HWNDPARENT);

    /// <summary>Hands <paramref name="owner"/> ownership of <paramref name="window"/>.</summary>
    public static void SetWindowOwner(IntPtr window, IntPtr owner) => SetWindowLongPtr(window, GWLP_HWNDPARENT, owner);

    public static int GetWindowExStyle(IntPtr window) => (int)GetWindowLongPtr(window, GWL_EXSTYLE);

    public static void SetWindowExStyle(IntPtr window, int style) => SetWindowLongPtr(window, GWL_EXSTYLE, style);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateLayeredWindow(
        IntPtr window, IntPtr destinationDc, ref POINT destinationPoint, ref SIZE size,
        IntPtr sourceDc, ref POINT sourcePoint, int colorKey, ref BLENDFUNCTION blend, int flags);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    /// <summary>Lets the GUI executable print into the console that launched it, if any.</summary>
    public static bool AttachParentConsole()
    {
        try
        {
            return AttachConsole(-1);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Rounds a borderless window on Windows 11; older builds ignore the attribute.</summary>
    public static void RoundCorners(IntPtr window)
    {
        try
        {
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(window, 33, ref preference, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Never crash over cosmetics.
        }
    }

    /// <summary>Tray icon edge in physical pixels for the current DPI (16, 20, 24, 32...).</summary>
    public static int TrayIconSize() => Math.Clamp(GetSystemMetrics(SM_CXSMICON), 16, 64);
}

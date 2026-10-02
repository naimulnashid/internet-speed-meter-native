<#
.SYNOPSIS
  Launches the app (or attaches to a running copy), captures its window to a
  PNG, and optionally closes it again.

.DESCRIPTION
  Used while developing to see the UI without a person at the screen. Captures
  with PrintWindow's PW_RENDERFULLCONTENT flag, which is what includes WinUI's
  composition-rendered content; a plain screen grab would also pick up any
  window lying on top of it.

  Screenshots go to screenshots/, which is gitignored: pointed at the real
  history, they show this machine's real speeds.
#>
param(
  [string]$Exe,
  [string]$Out = 'screenshots\window.png',
  [int]$Width = 1400,
  [int]$Height = 1000,
  [int]$WaitSeconds = 6,
  [string[]]$Arguments = @(),
  # Move the pointer to this point (physical pixels from the window's top-left)
  # before capturing, to photograph a hover state. -1 leaves it alone.
  [int]$HoverX = -1,
  [int]$HoverY = -1,
  # Click at the hover point, then wait for what the click opens before capturing.
  [switch]$Click,
  # Maximise instead of sizing to -Width x -Height.
  [switch]$Maximize,
  # Leave the size to the app (SPEEDMETER_DEBUG_FULLPAGE grows the window to the
  # page's height) and capture once it has stopped changing.
  [switch]$AppSized,
  [switch]$Close
)

$ErrorActionPreference = 'Stop'

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WindowCapture {
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }

  // SendInput rather than SetCursorPos: only real input produces the pointer
  // events WinUI's hover states listen for.
  static void MoveTo(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    var input = new INPUT { type = 0 };
    input.mi.dx = (int)((x - vx) * 65535.0 / (vw - 1));
    input.mi.dy = (int)((y - vy) * 65535.0 / (vh - 1));
    input.mi.flags = 0x0001 | 0x8000 | 0x4000; // MOVE | ABSOLUTE | VIRTUALDESK
    SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
  }

  public static void Click() {
    var down = new INPUT { type = 0 }; down.mi.flags = 0x0002; // LEFTDOWN
    var up = new INPUT { type = 0 }; up.mi.flags = 0x0004;     // LEFTUP
    SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(60);
    SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
  }

  public static void Hover(IntPtr h, int x, int y) {
    RECT r; GetWindowRect(h, out r);
    for (int i = 6; i >= 0; i--) {
      MoveTo(r.L + x - i * 4, r.T + y - i * 2);
      System.Threading.Thread.Sleep(40);
    }
  }
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

  public static string Size(IntPtr h) { RECT r; GetWindowRect(h, out r); return (r.R - r.L) + "x" + (r.B - r.T); }

  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

  // Cropped to the client area: the window rectangle also holds Windows 11's
  // invisible resize borders, which PrintWindow draws as a dark frame. The app
  // draws its own title bar, so the client area is everything that is seen.
  public static void Save(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    RECT c; GetClientRect(h, out c);
    var origin = new POINT(); ClientToScreen(h, ref origin);
    using (var bmp = new Bitmap(r.R - r.L, r.B - r.T))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT
      g.ReleaseHdc(dc);
      var area = new Rectangle(origin.X - r.L, origin.Y - r.T, c.R - c.L, c.B - c.T);
      area.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
      using (var client = bmp.Clone(area, bmp.PixelFormat)) client.Save(path, ImageFormat.Png);
    }
  }
}
'@

# Without this PowerShell is DPI-unaware, so on a scaled display the window
# rectangle comes back in logical pixels and the capture is cropped. -4 is
# DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. -Width/-Height are then physical.
[void][WindowCapture]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))

$proc = $null
if ($Exe) {
  $proc = if ($Arguments.Count) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
} else {
  $proc = Get-Process -Name SpeedMeter -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $proc) { throw 'No -Exe given and no running SpeedMeter process.' }
}

$deadline = (Get-Date).AddSeconds(30)
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 250
  $proc.Refresh()
  if ($proc.HasExited) { throw "The app exited with code $($proc.ExitCode) before showing a window." }
}
if ($proc.MainWindowHandle -eq 0) { throw 'No window appeared within 30 seconds.' }

$h = $proc.MainWindowHandle
if ($AppSized) {
  # Nothing to do here: the app sizes itself.
} elseif ($Maximize) {
  [void][WindowCapture]::ShowWindow($h, 3) # SW_MAXIMIZE
} elseif ($HoverX -ge 0) {
  # Topmost for a hover capture: Windows will not hand focus to a background
  # launch, so without this the pointer lands on whatever window is in front.
  [void][WindowCapture]::SetWindowPos($h, [IntPtr]::new(-1), 40, 40, $Width, $Height, 0)
} else {
  [void][WindowCapture]::SetWindowPos($h, [IntPtr]::Zero, 40, 40, $Width, $Height, 0x0004) # SWP_NOZORDER
}
[void][WindowCapture]::SetForegroundWindow($h)
Start-Sleep -Seconds $WaitSeconds
if ($AppSized) {
  # Settled = the same size for two seconds running.
  $last = ''; $since = Get-Date; $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline) {
    $now = [WindowCapture]::Size($h)
    if ($now -ne $last) { $last = $now; $since = Get-Date }
    elseif (((Get-Date) - $since).TotalSeconds -ge 2) { break }
    Start-Sleep -Milliseconds 250
  }
  Write-Output "Window settled at $last"
}
if ($HoverX -ge 0 -and $HoverY -ge 0) {
  [WindowCapture]::Hover($h, $HoverX, $HoverY)
  Start-Sleep -Milliseconds 1200
  if ($Click) {
    [WindowCapture]::Click()
    Start-Sleep -Milliseconds 3000
  }
}

$full = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
[WindowCapture]::Save($h, $full)
Write-Output "Saved $full (PID $($proc.Id))"

if ($Close) {
  # By PID, never by name: another copy may be the one someone is using.
  Stop-Process -Id $proc.Id -Force
}

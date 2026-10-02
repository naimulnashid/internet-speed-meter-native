<#
.SYNOPSIS
  Renders dashboard pages to PNGs from inside the app, one launch each.

.DESCRIPTION
  Each entry is "name=page[,scroll]" (page is speed or test). The app renders
  its own XAML tree (SPEEDMETER_DEBUG_SNAPSHOT) and exits, so this works with
  the screen locked or the window covered, where Capture-Window.ps1's screen
  and PrintWindow grabs come back black. Writes screenshots\<name>.png (the
  window) and screenshots\<name>.page.png (the whole page, top to bottom).
  Runs against -DataDir (default .\demo-data) with its own settings and local
  folders, so neither the real history nor your preferences are touched.
#>
param(
  [string[]]$Views = @('speed=speed'),
  [string]$DataDir,
  [string]$Configuration = 'Debug',
  [ValidateSet('dark', 'light')][string]$Theme = 'dark',
  [double]$Delay = 2.5
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $DataDir) { $DataDir = Join-Path $root 'demo-data' }
$exe = Join-Path $root "src\SpeedMeter.App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\SpeedMeter.exe"
$local = Join-Path $DataDir 'local'
New-Item -ItemType Directory -Force $local | Out-Null
# The theme is a window preference, so it is set in the demo's own local folder.
Set-Content -Path (Join-Path $local 'app-settings.json') -Value ('{ "Theme": "' + $Theme + '", "TrayNoteShown": true }') -Encoding ASCII
$env:SPEEDMETER_SETTINGS_DIR = $DataDir
$env:SPEEDMETER_LOCAL_DIR = $local
$env:SPEEDMETER_DEBUG_SNAPSHOT_DELAY = "$Delay"
# An invented connection card: a screenshot must never show this machine's address.
$env:SPEEDMETER_DEBUG_FAKE_META = '1'

# Through `powershell -File` an array arrives as one string, so views may
# also be separated by semicolons: -Views 'speed=speed;test=test'.
$Views = @($Views | ForEach-Object { $_ -split '\s*;\s*' } | Where-Object { $_ })
foreach ($view in $Views) {
  $name, $spec = $view -split '=', 2
  $out = Join-Path $root "screenshots\$name.png"
  $env:SPEEDMETER_DEBUG_VIEW = $spec
  $env:SPEEDMETER_DEBUG_SNAPSHOT = $out
  $p = Start-Process -FilePath $exe -PassThru
  if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw "No snapshot of $name within 120 seconds." }
  Write-Output "Saved $out"
  # The meter exits first, its dashboard a moment later. Until the dashboard
  # has gone, a new one would hand itself to it and draw nothing.
  $deadline = (Get-Date).AddSeconds(20)
  while ((Get-Process SpeedMeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
  Start-Sleep -Seconds 1
}

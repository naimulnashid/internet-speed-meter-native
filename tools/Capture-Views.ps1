<#
.SYNOPSIS
  Photographs several views of the dashboard, one launch each.

.DESCRIPTION
  Each entry is "name=page,scroll" (see SPEEDMETER_DEBUG_VIEW in MainWindow;
  page is speed or test), optionally "@x:y" to hover at a point before the
  capture, or "@x:y!" to click there and capture what that opens.
  Runs against -DataDir with its own SPEEDMETER_SETTINGS_DIR and a separate
  local folder, so neither the real history nor your preferences are touched.
  Defaults to .\demo-data (make it with `speedmeter demo-data demo-data`).
  Screenshots land in screenshots\ (gitignored).
#>
param(
  [string[]]$Views = @('speed=speed,0'),
  [string]$DataDir,
  [string]$Configuration = 'Debug',
  [int]$Width = 2160,
  [int]$Height = 1500,
  [int]$WaitSeconds = 7,
  # Capture each page top to bottom at this width (in DIPs): the app grows its
  # window to the page's full height (SPEEDMETER_DEBUG_FULLPAGE). 0 = one screen.
  [int]$FullPage = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $DataDir) { $DataDir = Join-Path $root 'demo-data' }
$exe = Join-Path $root "src\SpeedMeter.App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\SpeedMeter.exe"
$env:SPEEDMETER_SETTINGS_DIR = $DataDir
$env:SPEEDMETER_LOCAL_DIR = Join-Path $DataDir 'local'
$env:SPEEDMETER_DEBUG_FULLPAGE = if ($FullPage -gt 0) { "$FullPage" } else { $null }
# @(...) around the if: a one-item result would otherwise unroll to a string,
# which cannot be splatted.
$sizing = @(if ($FullPage -gt 0) { '-AppSized' } else { '-Width', $Width, '-Height', $Height })

foreach ($view in $Views) {
  $name, $spec = $view -split '=', 2
  $hoverX = -1; $hoverY = -1; $click = $false
  if ($spec -match '^(.*)@(\d+):(\d+)(!?)$') { $spec = $Matches[1]; $hoverX = [int]$Matches[2]; $hoverY = [int]$Matches[3]; $click = $Matches[4] -eq '!' }
  $clickArg = @(if ($click) { '-Click' })
  $env:SPEEDMETER_DEBUG_VIEW = $spec
  # Windows PowerShell 5.1, which has System.Drawing built in.
  powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Capture-Window.ps1') -Exe $exe -Out (Join-Path $root "screenshots\$name.png") @sizing -WaitSeconds $WaitSeconds -HoverX $hoverX -HoverY $hoverY @clickArg -Close
  Start-Sleep -Milliseconds 800
}

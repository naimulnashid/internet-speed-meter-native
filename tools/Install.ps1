<#
.SYNOPSIS
  Builds the app and installs it for the current user. No administrator
  rights needed.

.DESCRIPTION
  1. Publishes a self-contained Release build (it carries its own .NET and
     Windows App SDK) into %LOCALAPPDATA%\Programs\Internet Speed Meter. A
     running copy is closed first, by the path it runs from - never by name.
     From a release zip, which carries that build in app\ beside this script,
     nothing is built: the build is copied as it is.
  2. Adds "Internet Speed Meter" to the Start menu.
  3. Registers it under Settings -> Apps -> Installed apps, per user, with
     Uninstall.ps1 copied into the program folder as its uninstall command.
  4. Where the history goes. With no settings.ini yet - a first install, or
     after a Windows reset - it asks for the history folder and the folder
     for one-second samples, suggesting a drive other than the system one,
     and says when the folder already holds a history (which then carries
     on). -HistoryFolder and -RawFolder answer without asking, and also move
     an existing install's folders (the files already there are not moved).
  5. Starts it with Windows (straight to the tray), through the per-user Run
     entry the C# meter used - so this REPLACES that meter's entry rather
     than adding a second meter. -NoStartup skips this.
  6. Starts it, unless -NoLaunch. If the older meter is running, the app
     offers to stop it: only one meter can record at a time.

  The history is never touched, and neither is an existing settings.ini
  unless a folder is given: the app reads and carries on the history the
  older meter wrote, wherever settings.ini says it is. Re-running updates
  the installed copy.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -HistoryFolder D:\History\speed -RawFolder D:\History\speed-raw
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Install.ps1 -Uninstall
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install.ps1    # inside an extracted release zip
#>
param(
  [switch]$Uninstall,
  [switch]$RemoveData,
  [switch]$NoStartup,
  [switch]$NoLaunch,
  [string]$HistoryFolder,
  [string]$RawFolder
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Internet Speed Meter'
$exe = Join-Path $target 'SpeedMeter.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Internet Speed Meter.lnk'

if ($Uninstall) {
  # One implementation, shared with the Installed apps entry.
  & (Join-Path $PSScriptRoot 'Uninstall.ps1') -RemoveData:$RemoveData -Quiet
  return
}

function Stop-Installed {
  Get-Process SpeedMeter -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      # Killed, so the minute in hand is lost: at most sixty seconds of history.
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }
}

# ---- 1. Publish and copy ------------------------------------------------------
# A release zip is this script and Uninstall.ps1 beside app\, the published
# build. Anywhere else this is a clone, and it builds.
$packaged = Test-Path (Join-Path $PSScriptRoot 'app\SpeedMeter.exe')
if ($packaged) {
  $staging = Join-Path $PSScriptRoot 'app'
  Write-Host "Installing the build in $staging"
} else {
  $staging = Join-Path $root 'publish\SpeedMeter'
  if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
  Write-Host 'Publishing a Release build...'
  & dotnet publish (Join-Path $root 'src\SpeedMeter.App\SpeedMeter.App.csproj') -c Release -o $staging --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
# Without its own resources.pri the app dies at startup (0xC000027B) - what a
# publish without EnableMsixTooling produces. Never install that.
if (-not (Test-Path (Join-Path $staging 'SpeedMeter.pri'))) {
  throw 'The published build has no SpeedMeter.pri, so it would crash at startup. Check EnableMsixTooling in SpeedMeter.App.csproj.'
}

Stop-Installed
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $staging '*') $target -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $target 'Uninstall.ps1') -Force
# Explorer marks every file it extracts from a downloaded zip as downloaded,
# and SmartScreen then stops the app at each launch. Running this script is
# the decision to install it: clear the mark on the installed copy only.
if ($packaged) { Get-ChildItem $target -Recurse -File | Unblock-File }

# ---- 2. Start menu -------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'Live upload and download speed on the taskbar, with its history and a speed test'
$link.Save()

# ---- 3. Installed apps -----------------------------------------------------------
$bytes = (Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum
# From the exe, not Directory.Build.props: a release zip has no props file.
$version = ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion).ToString(3)
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall.ps1')`""
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\InternetSpeedMeterNative'
New-Item -Path $key -Force | Out-Null
$entry = @{
  DisplayName = 'Internet Speed Meter'
  DisplayVersion = [string]$version
  Publisher = 'Naimul Nashid'
  DisplayIcon = "$exe,0"
  InstallLocation = $target
  UninstallString = $uninstallCommand
  QuietUninstallString = "$uninstallCommand -Quiet"
  URLInfoAbout = 'https://github.com/naimulnashid/internet-speed-meter-native'
  InstallDate = (Get-Date -Format 'yyyyMMdd')
}
foreach ($name in $entry.Keys) { New-ItemProperty -Path $key -Name $name -Value $entry[$name] -PropertyType String -Force | Out-Null }
# No Modify or Repair buttons: re-running this script is the repair.
foreach ($name in 'NoModify', 'NoRepair') { New-ItemProperty -Path $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
New-ItemProperty -Path $key -Name 'EstimatedSize' -Value ([int]($bytes / 1KB)) -PropertyType DWord -Force | Out-Null

# ---- 4. Where the history goes --------------------------------------------------------
# settings.ini lives in %AppData%, which a Windows reset erases, while the history
# it points at may well have survived on another drive. So a fresh install - no
# settings.ini, as after a reset - asks where the folders are, offering a drive
# other than the system one; the history found there carries on. An existing
# settings.ini is left alone unless -HistoryFolder or -RawFolder says otherwise:
# re-running the installer must never quietly move a history.
$settingsDir = if ($env:SPEEDMETER_SETTINGS_DIR) { $env:SPEEDMETER_SETTINGS_DIR } else { Join-Path $env:APPDATA 'InternetSpeedMeter' }
$settingsFile = Join-Path $settingsDir 'settings.ini'
$fresh = -not (Test-Path $settingsFile)

function Read-Folder([string]$label, [string]$default) {
  try { $answer = Read-Host "  $label [$default]" } catch { return $default }   # -NonInteractive
  if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
  return [Environment]::ExpandEnvironmentVariables($answer.Trim().Trim('"'))
}

function Show-Folder([string]$label, [string]$path) {
  $systemRoot = [IO.Path]::GetPathRoot($env:SystemRoot)
  $minutes = @(Get-ChildItem -LiteralPath $path -Filter '*.bin' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{4}-\d{2}\.bin$' } | Sort-Object Name)
  $note = if ($label -eq 'History' -and $minutes.Count) { " (history found: $($minutes[0].BaseName) to $($minutes[-1].BaseName), carried on)" } else { '' }
  Write-Host "$label folder: $path$note"
  if ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($path)) -ieq $systemRoot) {
    Write-Host "  It is on the system drive, which a Windows reset erases." -ForegroundColor Yellow
  }
}

if ($fresh -or $HistoryFolder -or $RawFolder) {
  # A fixed drive other than the system one, for the defaults: the history is
  # the one thing here that cannot be rebuilt.
  $systemRoot = [IO.Path]::GetPathRoot($env:SystemRoot)
  $other = [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady -and $_.Name -ine $systemRoot } | Select-Object -First 1
  $base = if ($other) { Join-Path $other.Name 'PersistentData' } else { Join-Path $env:LOCALAPPDATA 'InternetSpeedMeter' }
  $defaultHistory = if ($other) { Join-Path $base 'internet-speed-meter' } else { Join-Path $base 'history' }
  $defaultRaw = if ($other) { Join-Path $base 'internet-speed-meter-raw' } else { Join-Path $base 'raw' }

  if ($fresh -and -not $HistoryFolder -and -not $RawFolder) {
    Write-Host ''
    Write-Host 'Where should the speed history be kept? Press Enter for the suggestion.'
    Write-Host 'After a Windows reset, give the folders the history was in before, and it carries on.'
    $HistoryFolder = Read-Folder 'History (kept forever)' $defaultHistory
    $RawFolder = Read-Folder "One-second samples (kept for two weeks)" $defaultRaw
  }

  # A list, not an array: PowerShell unwraps a one-line array into a string,
  # and += then joins text instead of adding a line.
  $lines = New-Object 'System.Collections.Generic.List[string]'
  if ($fresh) { $lines.Add('# Internet Speed Meter settings') } else { foreach ($l in Get-Content -LiteralPath $settingsFile) { $lines.Add($l) } }
  foreach ($pair in @(@('logfolder', $HistoryFolder), @('rawfolder', $RawFolder))) {
    if (-not $pair[1]) { continue }
    $full = [IO.Path]::GetFullPath($pair[1])
    New-Item -ItemType Directory -Force $full | Out-Null
    $line = "$($pair[0])=$full"
    $at = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "^\s*$($pair[0])\s*=") { $at = $i } }
    if ($at -ge 0) { $lines[$at] = $line } else { $lines.Add($line) }
    Show-Folder $(if ($pair[0] -eq 'logfolder') { 'History' } else { 'One-second samples' }) $full
  }
  if (-not $fresh) { Write-Host '  Files already in the old folders stay where they are; move them yourself to keep them.' }
  New-Item -ItemType Directory -Force $settingsDir | Out-Null
  # BOM-less: Windows PowerShell's utf8 writes a BOM.
  [IO.File]::WriteAllLines($settingsFile, $lines.ToArray(), (New-Object Text.UTF8Encoding($false)))
} else {
  $current = Get-Content -LiteralPath $settingsFile | Where-Object { $_ -match '^\s*logfolder\s*=' } | Select-Object -First 1
  if ($current) { Write-Host "History folder already set: $(($current -split '=', 2)[1].Trim())" }
}

# ---- 5. Start with Windows ---------------------------------------------------------
# The value name is the C# meter's, so this replaces its entry (StartupRegistration.cs).
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoStartup) {
  $previous = (Get-ItemProperty -Path $runKey -Name 'InternetSpeedMeter' -ErrorAction SilentlyContinue).InternetSpeedMeter
  New-ItemProperty -Path $runKey -Name 'InternetSpeedMeter' -Value "`"$exe`" --tray" -PropertyType String -Force | Out-Null
  if ($previous -and $previous -notlike "*$exe*") { Write-Host "Start with Windows now starts this app instead of $previous" }
  else { Write-Host 'Starts with Windows (turn it off from the tray menu or Task Manager).' }
}

Write-Host ("Installed to {0} ({1:N0} MB) with a Start menu shortcut and an Installed apps entry." -f $target, ($bytes / 1MB)) -ForegroundColor Green

# ---- 6. Start it --------------------------------------------------------------------
if (-not $NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $target }

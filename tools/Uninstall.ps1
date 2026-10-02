<#
.SYNOPSIS
  Removes Internet Speed Meter installed by Install.ps1.

.DESCRIPTION
  Closes the installed copy (by the path it runs from, never by name), then
  removes the program folder, the Start menu shortcut, the start-with-Windows
  entry (only when it points at this install) and the Installed apps entry.

  THE HISTORY IS NEVER TOUCHED, and neither is settings.ini: they are the
  meter's, and the older meter or a reinstall carries on from them. Delete
  them by hand if you mean to. -RemoveData removes only
  %LOCALAPPDATA%\Internet Speed Meter Native (window preferences, logs).

  Install.ps1 copies this script into the program folder and registers it as
  the uninstall command. Run from there, it first copies itself to %TEMP% and
  hands over, because it is about to delete the folder it lives in.
#>
param(
  [switch]$RemoveData,
  # No final pause - for Install.ps1 -Uninstall and scripted use.
  [switch]$Quiet
)
$ErrorActionPreference = 'Stop'

$target = Join-Path $env:LOCALAPPDATA 'Programs\Internet Speed Meter'
$exe = Join-Path $target 'SpeedMeter.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Internet Speed Meter.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\InternetSpeedMeterNative'
$localDir = Join-Path $env:LOCALAPPDATA 'Internet Speed Meter Native'

# Running from inside the folder about to be deleted: continue from a copy.
if ($PSCommandPath -and $PSCommandPath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
  $copy = Join-Path $env:TEMP "SpeedMeter-Uninstall-$PID.ps1"
  Copy-Item $PSCommandPath $copy -Force
  $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$copy`"")
  if ($RemoveData) { $forward += '-RemoveData' }
  if ($Quiet) { $forward += '-Quiet' }
  Set-Location $env:TEMP
  $p = Start-Process powershell.exe -ArgumentList $forward -Wait -PassThru
  Remove-Item $copy -Force -ErrorAction SilentlyContinue
  exit $p.ExitCode
}

try {
  Get-Process SpeedMeter -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
    ForEach-Object {
      Write-Host "Closing the running copy (PID $($_.Id))"
      Stop-Process -Id $_.Id -Force
      $_.WaitForExit(5000) | Out-Null
    }

  if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
  $run = (Get-ItemProperty -Path $runKey -Name 'InternetSpeedMeter' -ErrorAction SilentlyContinue).InternetSpeedMeter
  if ($run -and $run -like "*$exe*") { Remove-ItemProperty -Path $runKey -Name 'InternetSpeedMeter' }
  if (Test-Path $uninstallKey) { Remove-Item $uninstallKey -Recurse -Force }
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  if ($RemoveData -and (Test-Path $localDir)) {
    Remove-Item $localDir -Recurse -Force
    Write-Host "Removed $localDir"
  }
  Write-Host 'Internet Speed Meter is uninstalled.' -ForegroundColor Green
  Write-Host 'Your speed history and settings.ini were kept. Reinstalling carries on from them.'
  $code = 0
}
catch {
  Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
  $code = 1
}

# Launched from Installed apps, this is a console window of its own: leave the
# result on screen long enough to read.
if (-not $Quiet) { Start-Sleep -Seconds 5 }
exit $code

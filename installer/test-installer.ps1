# CI check of the Windows installer on a clean runner: install for just me and for everyone, check the files,
# shortcuts and the Apps & features entry, run the installed app's self-test, update it the way the app does
# ("Install and restart" runs the next installer silently), then uninstall and check that it's all gone.
# Usage: installer\test-installer.ps1 <setup.exe> <version>
param([Parameter(Mandatory)] [string] $Setup, [Parameter(Mandatory)] [string] $Version)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path
$AppKey = '{7E6B1C3A-5D2F-4E8B-9A41-2C5D7F1E0B93}_is1'
$failed = 0

function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what"; $script:failed++ }
}

function Wait-Until([scriptblock] $cond, [int] $seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $cond)
}

function Stop-Savefile { Get-Process Cnema -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1 }

function Test-Mode([string] $mode) {
    Write-Host "== $mode =="
    if ($mode -eq 'ALLUSERS') {
        $dir = Join-Path $env:ProgramFiles 'Savefile'
        $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Savefile.lnk'
        $desk = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Savefile.lnk'
        $reg = "Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppKey"
    } else {
        $dir = Join-Path $env:LOCALAPPDATA 'Programs\Savefile'
        $menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Savefile.lnk'
        $desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Savefile.lnk'
        $reg = "Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppKey"
    }
    $log = Join-Path $env:RUNNER_TEMP "setup-$mode.log"
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/$mode", "/LOG=$log" -Wait -PassThru
    Check ($p.ExitCode -eq 0) "installer exit code $($p.ExitCode)"
    Check (Test-Path "$dir\Cnema.exe") "app in $dir"
    Check (Test-Path "$dir\unins000.exe") "uninstaller next to the app"
    Check (Test-Path "$dir\libvlc\win-x64\libvlc.dll") "libVLC installed"
    Check (Test-Path "$dir\tools\ffmpeg.exe") "ffmpeg installed"
    Check (Test-Path $menu) "Start menu shortcut $menu"
    Check (Test-Path $desk) "desktop shortcut $desk"
    $entry = Get-ItemProperty $reg -ErrorAction SilentlyContinue
    Check ($null -ne $entry) "Apps & features entry"
    if ($entry) {
        Check ($entry.DisplayName -eq 'Savefile') "  named Savefile ($($entry.DisplayName))"
        Check ($entry.DisplayVersion -eq $Version) "  version $Version ($($entry.DisplayVersion))"
        Check ($entry.InstallLocation.TrimEnd('\') -eq $dir) "  location $($entry.InstallLocation)"
    }

    $t = Start-Process "$dir\Cnema.exe" -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
    Check ($t.ExitCode -eq 0) "installed app self-test (exit $($t.ExitCode))"

    # The update path: the installed app hands over to the installer and quits; the installer runs silently in
    # the same mode, then the app is started again (in the tray).
    Stop-Savefile
    $out = Join-Path $env:RUNNER_TEMP "update-$mode.txt"
    $u = Start-Process "$dir\Cnema.exe" -ArgumentList '--selftest-update', $Setup -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
    Get-Content $out | Write-Host
    Check ($u.ExitCode -eq 0) "update handed over to the installer (exit $($u.ExitCode))"
    $wantMode = if ($mode -eq 'ALLUSERS') { 'True' } else { 'False' }
    Check ((Get-Content $out -Raw) -match "for everyone: $wantMode") "update detects the $mode install"
    Check ((Get-Content $out -Raw) -match 'windows-x64-setup\.exe') "update fetches the installer, not the zip"
    $ulog = Join-Path $env:LOCALAPPDATA 'Cnema\update-setup.log'
    $done = Wait-Until { (Test-Path $ulog) -and ((Get-Content $ulog -Raw) -match 'Installation process succeeded') } 180
    Check $done "update installer ran and succeeded"
    if (-not $done -and (Test-Path $ulog)) { Get-Content $ulog -Tail 30 | Write-Host }
    Check (Wait-Until { $null -ne (Get-Process Cnema -ErrorAction SilentlyContinue) } 60) "app started again after the update"
    Check (Test-Path $menu) "Start menu shortcut kept after the update"
    Check ((Get-ItemProperty $reg -ErrorAction SilentlyContinue).InstallLocation.TrimEnd('\') -eq $dir) "same folder after the update"

    # Uninstall: the uninstaller closes the running app, copies itself to temp and returns early, so wait for it.
    Stop-Savefile
    Start-Process "$dir\unins000.exe" -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Check (Wait-Until { -not (Test-Path "$dir\Cnema.exe") } 120) "app removed"
    Check (Wait-Until { -not (Test-Path $menu) } 30) "Start menu shortcut removed"
    Check (-not (Test-Path $desk)) "desktop shortcut removed"
    Check (-not (Test-Path $reg)) "Apps & features entry removed"
    Stop-Savefile
}

Test-Mode 'CURRENTUSER'
Test-Mode 'ALLUSERS'
if ($failed -gt 0) { Write-Host "$failed check(s) failed"; exit 1 }
Write-Host 'installer checks OK'

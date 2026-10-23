# Destructive ONLY inside a new, isolated hosted runner. Never run on a user's PC.
[CmdletBinding()]
param([string] $OutputDirectory = 'artifacts/rc')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
. (Join-Path $PSScriptRoot 'rc-version.ps1')
Assert-IsolatedDesktopRunner
if (-not [Environment]::UserInteractive) { throw 'Installed acceptance requires an interactive desktop.' }
$version = Get-ClipScopeVersion
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$evidence = Join-Path $output 'evidence'
[void][IO.Directory]::CreateDirectory($evidence)
$install = Join-Path $env:LOCALAPPDATA 'Programs/ClipScope'
$data = Join-Path $env:LOCALAPPDATA 'ClipScope'
$exe = Join-Path $install 'ClipScope.App.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ClipScope.lnk'
$runKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Run'
$arp = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/ClipScope.Desktop_is1'
if ((Test-Path $install) -or (Test-Path $data) -or (Test-Path $arp) -or (Test-Path $shortcut) -or
    (Get-Process -Name 'ClipScope.App' -ErrorAction SilentlyContinue)) { throw 'Existing installation or data is protected.' }
if (Get-ItemProperty -Path $runKey -Name ClipScope -ErrorAction SilentlyContinue) { throw 'Existing startup registration is protected.' }
function Invoke-Setup([string] $Path, [string[]] $Extra = @()) {
    $process = Start-Process -FilePath $Path -ArgumentList (@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') + $Extra) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer returned $($process.ExitCode)." }
    $process.Dispose()
}
function Stop-ClipScope {
    if (Get-Process -Name 'ClipScope.App' -ErrorAction SilentlyContinue) {
        $request = Start-Process -FilePath $exe -ArgumentList '--shutdown' -PassThru -Wait
        if ($request.ExitCode -ne 0) { throw 'Graceful installed shutdown request failed.' }
        $request.Dispose()
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (Get-Process -Name 'ClipScope.App' -ErrorAction SilentlyContinue) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Installed application did not exit gracefully.' }
            Start-Sleep -Milliseconds 100
        }
    }
}
$profile = $null
try {
    $installer = Join-Path $output "ClipScope-$($version.Version)-win-x64-setup.exe"
    Invoke-Setup $installer
    if (-not (Test-Path $exe) -or -not (Test-Path $shortcut) -or -not (Test-Path $arp)) { throw 'Installation metadata or shortcut is missing.' }
    if ((Get-ItemProperty $arp).DisplayVersion -ne $version.Version) { throw 'Apps & Features version differs.' }
    $shell = New-Object -ComObject WScript.Shell
    if ($shell.CreateShortcut($shortcut).TargetPath -ne $exe) { throw 'Start Menu target is incorrect.' }
    $payload = Join-Path $output ('payload-' + $version.Version)
    $manifest = Get-Content (Join-Path $output 'build-manifest.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.payload) {
        if ((Get-FileHash -LiteralPath (Join-Path $install $file.path)).Hash -ne $file.sha256) { throw "Installed payload hash differs: $($file.path)" }
    }
    # Move staging out of the expected location: execution must use only installed files.
    Move-Item -LiteralPath $payload -Destination ($payload + '-not-in-use')
    & powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'smoke-windows.ps1') -ExecutablePath $exe -RequireDesktop
    if ($LASTEXITCODE -ne 0) { throw 'Installed functional smoke failed.' }
    Write-Output '::notice title=RC installer::PASS: fresh per-user install, payload hashes, metadata, Start Menu, installed functional smoke independent of staging'
    $profile = New-OwnedTestDirectory $data
    $settings = '{"MinimizeToTray":true,"ScreenshotsEnabled":true,"ExcludedApplications":["C:\\Synthetic\\private.exe"]}'
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'), $settings)
    [IO.File]::WriteAllText((Join-Path $data 'preservation-sentinel.txt'), 'synthetic user history sentinel')
    New-ItemProperty -Path $runKey -Name ClipScope -Value ('"' + $exe + '" --background') -PropertyType String -Force | Out-Null
    $app = Start-Process -FilePath $exe -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 3
    if ($app.HasExited) { throw 'Quiet installed launch exited.' }
    $duplicate = Start-Process -FilePath $exe -ArgumentList '--background' -Wait -PassThru
    if ($duplicate.ExitCode -ne 0 -or @(Get-Process -Name 'ClipScope.App').Count -ne 1) { throw 'Single-instance quiet activation failed.' }
    $duplicate.Dispose()
    # Reinstallation is the supported repair operation; it must close the running app.
    Invoke-Setup $installer
    if (Get-Process -Name 'ClipScope.App' -ErrorAction SilentlyContinue) { throw 'Installer did not close the installed app.' }
    if ([IO.File]::ReadAllText((Join-Path $data 'settings.json')) -ne $settings) { throw 'Repair changed existing settings.' }
    if ((Get-ItemProperty $runKey).ClipScope -ne ('"' + $exe + '" --background')) { throw 'Repair lost startup registration.' }
    Invoke-Setup (Join-Path $install 'unins000.exe')
    if ((Test-Path $exe) -or (Test-Path $shortcut) -or (Test-Path $arp)) { throw 'Uninstall left app binaries or registration.' }
    if (Get-ItemProperty -Path $runKey -Name ClipScope -ErrorAction SilentlyContinue) { throw 'Uninstall left a startup entry.' }
    if ([IO.File]::ReadAllText((Join-Path $data 'settings.json')) -ne $settings -or
        -not (Test-Path (Join-Path $data 'preservation-sentinel.txt'))) { throw 'Default uninstall lost user data.' }
    Invoke-Setup $installer
    $app = Start-Process -FilePath $exe -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 3
    if ($app.HasExited) { throw 'Reinstall cannot launch with the retained profile.' }
    Stop-ClipScope
    Invoke-Setup (Join-Path $install 'unins000.exe') @('/PURGEUSERDATA=1')
    if (Test-Path $data) { throw 'Explicit data purge did not remove the synthetic profile.' }
    Write-Output '::notice title=RC installer::PASS: quiet single instance, graceful repair, startup preservation/removal, uninstall preserving data, reinstall, deliberate full purge'
    @{ os = [Environment]::OSVersion.VersionString; version = $version.Version; sha = (& git -C $root rev-parse HEAD);
       result = 'PASS'; physicalWindows11 = 'NOT TESTED'; tests = @('install','hashes','shortcut','installed smoke','quiet duplicate','repair','startup registry','uninstall preserve','reinstall','explicit purge') } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $evidence 'installer.json') -Encoding utf8
} finally {
    if (Test-Path $exe) { Stop-ClipScope }
    Remove-OwnedTestDirectory $profile
}

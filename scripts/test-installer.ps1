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
function Get-ProfileSnapshot {
    $json = & dotnet (Join-Path $root 'tools/ClipScope.Diagnostics/bin/Release/net10.0/ClipScope.Diagnostics.dll') snapshot $data
    if ($LASTEXITCODE -ne 0) { throw 'Upgrade integrity verification failed.' }
    return ($json | ConvertFrom-Json)
}
function Assert-SameSnapshot($Before, $After) {
    foreach ($property in @('clips','pins','recordsSha256','settingsSha256','searchHits')) {
        if ($Before.$property -ne $After.$property) { throw "Upgrade changed $property." }
    }
    if (($Before.media | ConvertTo-Json -Compress) -ne ($After.media | ConvertTo-Json -Compress)) { throw 'Upgrade changed media bytes.' }
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
    Invoke-Setup (Join-Path $install 'unins000.exe')
    if (Test-Path $data) { throw 'Smoke did not release its owned test profile.' }

    # Build the exact pre-RC main, then let its real app create history, pins,
    # source snapshots and settings. No RC schema generator substitutes for it.
    $baselineSha = 'f488f25344bce85fcef361241797eeed33510968'
    $baseline = New-OwnedTestDirectory (Join-Path $root 'artifacts/upgrade-baseline')
    & git -C $root fetch --no-tags --depth=1 origin $baselineSha
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the recorded main baseline.' }
    $archive = Join-Path $baseline.Path 'main.zip'
    & git -C $root archive --format=zip "--output=$archive" $baselineSha
    if ($LASTEXITCODE -ne 0) { throw 'Could not archive the main baseline.' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $baseline.Path 'source')
    Push-Location (Join-Path $baseline.Path 'source')
    try {
        & dotnet publish src/ClipScope.App/ClipScope.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -warnaserror -o (Join-Path $baseline.Path 'publish')
        if ($LASTEXITCODE -ne 0) { throw 'The actual main baseline did not publish.' }
    } finally { Pop-Location }
    $receipt = Join-Path $evidence 'profile-ownership.json'
    & powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'smoke-windows.ps1') -ExecutablePath (Join-Path $baseline.Path 'publish/ClipScope.App.exe') -RequireDesktop -RetainProfile -OwnershipReceipt $receipt
    if ($LASTEXITCODE -ne 0) { throw 'Actual main could not create the upgrade history.' }
    # Explicit handoff from the child we launched, never adoption of pre-existing data.
    $profile = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if ($profile.Path -ne $data -or $profile.Token -notmatch '^[a-f0-9]{32}$') { throw 'Invalid synthetic profile handoff.' }
    $settingsPath = Join-Path $data 'settings.json'
    $configuration = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $configuration.LaunchAtStartup = $true
    $configuration.ExcludedApplications = @('C:\Synthetic\private.exe')
    $configuration | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settingsPath -Encoding utf8
    $before = Get-ProfileSnapshot
    if ($before.clips -lt 1 -or $before.pins -lt 1 -or @($before.media).Count -lt 1 -or $before.searchHits -lt 1) { throw 'Main upgrade fixture lacks actual history, pins, media or FTS.' }
    $before | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'main-before-upgrade.json')
    $settings = [IO.File]::ReadAllText($settingsPath)

    # No older published RC exists. This lower-version installer is explicitly a
    # synthetic fixture, built with the same packaging code and an owned stale file.
    $predecessor = '0.0.0-rc.1'
    & (Join-Path $PSScriptRoot 'package-rc.ps1') -OutputDirectory 'artifacts/predecessor-fixture' -FixtureVersion $predecessor -SkipValidation -IncludeStaleFixtureFile
    New-ItemProperty -Path $runKey -Name ClipScope -Value ('"' + (Join-Path $baseline.Path 'publish/ClipScope.App.exe') + '" --background') -PropertyType String -Force | Out-Null
    Invoke-Setup (Join-Path $root "artifacts/predecessor-fixture/ClipScope-$predecessor-win-x64-setup.exe")
    if (-not (Test-Path (Join-Path $install 'rc-stale-fixture.txt'))) { throw 'Predecessor did not install its stale-file fixture.' }
    Invoke-Setup $installer
    if (Test-Path (Join-Path $install 'rc-stale-fixture.txt')) { throw 'Upgrade left a stale previously owned payload file.' }
    $app = Start-Process -FilePath $exe -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 3
    if ($app.HasExited) { throw 'Upgraded profile cannot launch.' }
    Stop-ClipScope
    $after = Get-ProfileSnapshot
    Assert-SameSnapshot $before $after
    $after | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'rc-after-upgrade.json')
    Write-Output '::notice title=RC upgrade::PASS: actual main history, pins, settings, exclusions, media hashes, FTS and SQLite integrity preserved; synthetic predecessor upgrade removed its stale owned file'
    [IO.File]::WriteAllText((Join-Path $data 'preservation-sentinel.txt'), 'synthetic user history sentinel')
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
    Assert-SameSnapshot $before (Get-ProfileSnapshot)
    Invoke-Setup (Join-Path $install 'unins000.exe') @('/PURGEUSERDATA=1')
    if (Test-Path $data) { throw 'Explicit data purge did not remove the synthetic profile.' }
    Write-Output '::notice title=RC installer::PASS: quiet single instance, graceful repair, startup preservation/removal, uninstall preserving data, reinstall, deliberate full purge'
    @{ os = [Environment]::OSVersion.VersionString; version = $version.Version; sha = (& git -C $root rev-parse HEAD);
       result = 'PASS'; baselineSha = $baselineSha; predecessor = 'synthetic 0.0.0-rc.1 (not a previously released RC)'; physicalWindows11 = 'NOT TESTED'; tests = @('install','hashes','shortcut','installed smoke','actual main profile upgrade','synthetic predecessor upgrade','stale owned file cleanup','quiet duplicate','repair','startup registry','uninstall preserve','reinstall','explicit purge') } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $evidence 'installer.json') -Encoding utf8
} catch {
    Write-Output ('::error title=Installed acceptance::' + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A') + " at line $($_.InvocationInfo.ScriptLineNumber)")
    throw
} finally {
    if (Test-Path $exe) { Stop-ClipScope }
    Remove-OwnedTestDirectory $profile
}

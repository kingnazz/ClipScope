# Build tools only. The resulting installed application needs none of these tools.
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/rc',
    [string] $FixtureVersion = '',
    [switch] $SkipValidation,
    [string] $SigningCommand = '',
    [switch] $IncludeStaleFixtureFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'rc-version.ps1')
$version = Get-ClipScopeVersion $FixtureVersion
if ($IncludeStaleFixtureFile -and -not $FixtureVersion) { throw 'Stale-file injection is restricted to the synthetic predecessor installer.' }
Push-Location $root
try {
    if (-not $SkipValidation) { & (Join-Path $PSScriptRoot 'validate.ps1') }
    else {
        # WinAppSDK omits its PRI-generation targets from dotnet publish for an
        # unpackaged app. A regular Windows build must generate the merged index,
        # including when building the lower-version installer test fixture.
        $buildLines = @(& dotnet build src/ClipScope.App/ClipScope.App.csproj -c Release -p:Platform=x64 "-p:ClipScopeVersion=$($version.Version)" -p:RestoreLockedMode=true -warnaserror 2>&1)
        $buildLines | Write-Output
        if ($LASTEXITCODE -ne 0) { throw ('Resource build: ' + (($buildLines | Select-Object -Last 15) -join "`n")) }
    }
    $output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    [void][IO.Directory]::CreateDirectory($output)
    $stage = Join-Path $output ('payload-' + $version.Version)
    if (Test-Path -LiteralPath $stage) { throw 'Use a new output directory; packaging never adopts or deletes an existing staging tree.' }
    # The app declares win-x64. A global -r would also rewrite the portable
    # reference projects' lockfiles and invalidate subsequent locked restores.
    $publish = @('publish', 'src/ClipScope.App/ClipScope.App.csproj', '-c', 'Release',
        '--self-contained', 'true', '-p:Platform=x64', '-p:WindowsAppSDKSelfContained=true',
        '-p:RestoreLockedMode=true',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', "-p:ClipScopeVersion=$($version.Version)",
        '-p:ContinuousIntegrationBuild=true', '-warnaserror', '-o', $stage)
    $publishLines = @(& dotnet @publish 2>&1)
    $publishLines | Write-Output
    if ($LASTEXITCODE -ne 0) { throw ('Self-contained publication: ' + (($publishLines | Select-Object -Last 15) -join "`n")) }
    $buildOutput = Join-Path $root 'src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'
    $applicationPri = Join-Path $buildOutput 'ClipScope.App.pri'
    if (-not (Test-Path -LiteralPath $applicationPri)) { throw 'The regular Windows Release build did not generate the application PRI.' }
    Copy-Item -LiteralPath $applicationPri -Destination $stage
    $resourceInventory = @{}
    foreach ($location in @(@{label='build';path=$buildOutput},@{label='published';path=$stage})) {
        $resourceInventory[$location.label] = @(Get-ChildItem -LiteralPath $location.path -File -Recurse |
            Where-Object { $_.Extension -in @('.pri','.xbf','.xaml') } |
            ForEach-Object { @{path=[IO.Path]::GetRelativePath($location.path,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
    }
    Write-Output ('::notice title=RC resource inventory::' + ($resourceInventory | ConvertTo-Json -Compress -Depth 5))
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'The licensed Visual C++ release redistributable directory is required on the build machine.' }
    $crtFile = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Redist/MSVC') -Filter 'vcruntime140.dll' -File -Recurse |
        Where-Object { $_.FullName -match '\\x64\\Microsoft\.VC[0-9]+\.CRT\\vcruntime140\.dll$' } |
        Sort-Object { [version]::new($_.VersionInfo.FileMajorPart,$_.VersionInfo.FileMinorPart,$_.VersionInfo.FileBuildPart,$_.VersionInfo.FilePrivatePart) } -Descending | Select-Object -First 1
    $crt = if ($crtFile) { $crtFile.DirectoryName } else { $null }
    if (-not $crt) { throw 'The x64 release CRT redistribution payload was not found.' }
    $crtVersion = [version]::new($crtFile.VersionInfo.FileMajorPart,$crtFile.VersionInfo.FileMinorPart,$crtFile.VersionInfo.FileBuildPart,$crtFile.VersionInfo.FilePrivatePart).ToString()
    if ($crtVersion -ne $version.Crt) { throw "Expected release CRT $($version.Crt); found $crtVersion. Update the toolchain deliberately and revalidate." }
    Get-ChildItem -LiteralPath $crt -Filter '*.dll' | Copy-Item -Destination $stage
    Write-Output "::notice title=RC runtime toolchain::App-local release CRT $($crtFile.VersionInfo.FileVersion)"
    $licenses = Join-Path $stage 'licenses'
    [void][IO.Directory]::CreateDirectory($licenses)
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    foreach ($package in @('microsoft.windowsappsdk.runtime', 'microsoft.windowsappsdk.winui')) {
        $lock = Get-Content -LiteralPath 'src/ClipScope.App/packages.lock.json' -Raw | ConvertFrom-Json -AsHashtable
        $dependency = $lock.dependencies.Values | ForEach-Object { $_.GetEnumerator() } | Where-Object { $_.Key.ToLowerInvariant() -eq $package } | Select-Object -First 1
        $packagePath = Join-Path $nuget "$package/$($dependency.Value.resolved)"
        $label = if ($package -eq 'microsoft.windowsappsdk.runtime') { 'WindowsAppSDK' } else { 'WinUI' }
        foreach ($name in @('license.txt','NOTICE.txt')) {
            Copy-Item -LiteralPath (Join-Path $packagePath $name) -Destination (Join-Path $licenses "$label-$name")
        }
    }
    # .NET's self-contained runtime brings its own LICENSE/ThirdPartyNotices files.
    Copy-Item -LiteralPath 'packaging/THIRD_PARTY_NOTICES.txt' -Destination $licenses
    Copy-Item -Path 'packaging/licenses/*' -Destination $licenses
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'ClipScope.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtimeVersion = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })[0].version
    if ($runtimeVersion -ne $version.Runtime) { throw 'Published .NET runtime differs from the pinned toolchain.' }
    $runtimePackage = Join-Path $nuget "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    foreach ($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
        Copy-Item -LiteralPath (Join-Path $runtimePackage $notice) -Destination (Join-Path $licenses ".NET-$notice")
    }
    $required = @('ClipScope.App.exe','ClipScope.App.dll','ClipScope.App.deps.json','ClipScope.App.runtimeconfig.json','ClipScope.App.pri',
        'hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll','Microsoft.UI.Xaml.dll',
        'Microsoft.WindowsAppRuntime.dll','e_sqlite3.dll','vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','Assets/ClipScope.ico')
    foreach ($file in $required) { if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Required deployed file missing: $file" } }
    $runtime = Get-Content -LiteralPath (Join-Path $stage 'ClipScope.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($runtime.runtimeOptions.ContainsKey('framework') -or $runtime.runtimeOptions.ContainsKey('frameworks')) { throw 'Published runtime is framework-dependent.' }
    $meta = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'ClipScope.App.exe'))
    if ($meta.FileVersion -ne $version.FileVersion -or $meta.ProductVersion -notlike "$($version.Version)*") { throw 'Executable version metadata disagrees with Version.props.' }
    if ($SigningCommand) {
        $SigningCommand = (Resolve-Path -LiteralPath $SigningCommand).Path
        if ($SigningCommand.Contains('$')) { throw 'Inno signing-hook paths must not contain its macro delimiter ($).' }
        # Trusted build-machine script supplied by the release owner; no credentials in arguments/logs.
        & $SigningCommand -Path (Join-Path $stage 'ClipScope.App.exe')
        if (-not $?) { throw 'Executable signing failed.' }
    }
    if ($IncludeStaleFixtureFile) { 'Owned by the synthetic predecessor only.' | Set-Content (Join-Path $stage 'rc-stale-fixture.txt') }
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) } | Sort-Object)
    @($files + 'install-files.txt') | Set-Content -LiteralPath (Join-Path $stage 'install-files.txt') -Encoding utf8
    $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    # Inno 6's /? omits the engine version and ISCC.exe has a 0.0.0.0 PE
    # version. Compile a tiny throwaway probe (never installed or shipped) to
    # query the loaded engine's own version banner before packaging the app.
    $probe = Join-Path $output ('compiler-probe-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $probe)
    $probeScript = Join-Path $probe 'probe.iss'
    @('[Setup]','AppName=ClipScope compiler probe','AppVersion=0','CreateAppDir=no','Uninstallable=no',
        "OutputDir=$probe",'OutputBaseFilename=probe') | Set-Content -LiteralPath $probeScript -Encoding ascii
    $compilerBanner = @(& $compiler $probeScript 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ('Inno engine probe failed: ' + (($compilerBanner | Select-Object -Last 8) -join ' ')) }
    $engine = [regex]::Match(($compilerBanner -join "`n"), 'Compiler engine version:\s*Inno Setup\s+([0-9]+\.[0-9]+\.[0-9]+)')
    if (-not $engine.Success) { throw 'Could not read the Inno compiler engine version from its banner.' }
    $compilerVersion = $engine.Groups[1].Value
    if ($compilerVersion -ne $version.Compiler) { throw "Expected Inno Setup $($version.Compiler); found $compilerVersion." }
    Remove-Item -LiteralPath $probeScript,(Join-Path $probe 'probe.exe')
    [IO.Directory]::Delete($probe,$false)
    $compile = @("/DPayload=$stage", "/DOutput=$output", "/DAppVersion=$($version.Version)",
        "/DFileVersion=$($version.FileVersion)", "/DPublisher=$($version.Publisher)", "/DAppId=$($version.AppId)", 'packaging/ClipScope.iss')
    if ($SigningCommand) {
        $compile = @('/DEnableSigning', ('/Sclipscope=pwsh.exe -NoProfile -File $q' + $SigningCommand + '$q -Path $f')) + $compile
    }
    $compilerOutput = @(& $compiler @compile 2>&1)
    $compilerOutput | ForEach-Object { Write-Output $_ }
    if ($LASTEXITCODE -ne 0) {
        $detail = ($compilerOutput | Select-Object -Last 10) -join "`n"
        Write-Output ('::error title=Installer compiler::' + $detail.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A'))
    }
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $output "ClipScope-$($version.Version)-win-x64-setup.exe"
    if ($SigningCommand) {
        & $SigningCommand -Path $installer
        if (-not $?) { throw 'Installer signing failed.' }
    }
    $manifest = [ordered]@{ version = $version.Version; fileVersion = $version.FileVersion; commit = (& git rev-parse HEAD);
        sdk = (& dotnet --version); compiler = $compilerVersion; runtime = 'win-x64/self-contained'; dotnetRuntime = $runtimeVersion;
        crtVersion = $crtFile.VersionInfo.FileVersion;
        signing = $(if ($SigningCommand) { 'external-signing-hook' } else { 'unsigned-development' });
        installer = (Split-Path $installer -Leaf); sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;
        payload = @($files | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash } }) }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding utf8
    "$($manifest.sha256)  $($manifest.installer)" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Built $($manifest.installer); self-contained payload validated."
} catch {
    Write-Output ('::error title=RC packaging::' + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A'))
    throw
} finally { Pop-Location }

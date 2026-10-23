# Build tools only. The resulting installed application needs none of these tools.
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/rc',
    [string] $FixtureVersion = '',
    [switch] $SkipValidation,
    [string] $SigningCommand = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'rc-version.ps1')
$version = Get-ClipScopeVersion $FixtureVersion
Push-Location $root
try {
    if (-not $SkipValidation) { & (Join-Path $PSScriptRoot 'validate.ps1') }
    $output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
    [void][IO.Directory]::CreateDirectory($output)
    $stage = Join-Path $output ('payload-' + $version.Version)
    if (Test-Path -LiteralPath $stage) { throw 'Use a new output directory; packaging never adopts or deletes an existing staging tree.' }
    $publish = @('publish', 'src/ClipScope.App/ClipScope.App.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:Platform=x64', '-p:WindowsAppSDKSelfContained=true',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', "-p:ClipScopeVersion=$($version.Version)",
        '-p:ContinuousIntegrationBuild=true', '-warnaserror', '-o', $stage)
    & dotnet @publish
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained Release publication failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'The licensed Visual C++ release redistributable directory is required on the build machine.' }
    $crt = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Redist/MSVC') -Directory |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/Microsoft.VC143.CRT' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $crt) { throw 'The x64 release CRT redistribution payload was not found.' }
    Get-ChildItem -LiteralPath $crt -Filter '*.dll' | Copy-Item -Destination $stage
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
    $required = @('ClipScope.App.exe','ClipScope.App.dll','ClipScope.App.deps.json','ClipScope.App.runtimeconfig.json',
        'hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll','Microsoft.UI.Xaml.dll',
        'Microsoft.WindowsAppRuntime.dll','e_sqlite3.dll','vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','Assets/ClipScope.ico')
    foreach ($file in $required) { if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Required deployed file missing: $file" } }
    $runtime = Get-Content -LiteralPath (Join-Path $stage 'ClipScope.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($runtime.runtimeOptions.ContainsKey('framework') -or $runtime.runtimeOptions.ContainsKey('frameworks')) { throw 'Published runtime is framework-dependent.' }
    $meta = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'ClipScope.App.exe'))
    if ($meta.FileVersion -ne $version.FileVersion -or $meta.ProductVersion -notlike "$($version.Version)*") { throw 'Executable version metadata disagrees with Version.props.' }
    if ($SigningCommand) {
        # Trusted build-machine script supplied by the release owner; no credentials in arguments/logs.
        & $SigningCommand -Path (Join-Path $stage 'ClipScope.App.exe')
        if (-not $?) { throw 'Executable signing failed.' }
    }
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) } | Sort-Object)
    @($files + 'install-files.txt') | Set-Content -LiteralPath (Join-Path $stage 'install-files.txt') -Encoding utf8
    $compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    $compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).ProductVersion
    if ($compilerVersion -notlike "$($version.Compiler)*") { throw "Expected Inno Setup $($version.Compiler); found $compilerVersion." }
    $compile = @("/DPayload=$stage", "/DOutput=$output", "/DAppVersion=$($version.Version)",
        "/DFileVersion=$($version.FileVersion)", "/DPublisher=$($version.Publisher)", "/DAppId=$($version.AppId)", 'packaging/ClipScope.iss')
    & $compiler @compile
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $output "ClipScope-$($version.Version)-win-x64-setup.exe"
    if ($SigningCommand) {
        & $SigningCommand -Path $installer
        if (-not $?) { throw 'Installer signing failed.' }
    }
    $manifest = [ordered]@{ version = $version.Version; fileVersion = $version.FileVersion; commit = (& git rev-parse HEAD);
        sdk = (& dotnet --version); compiler = $compilerVersion; runtime = 'win-x64/self-contained';
        signing = $(if ($SigningCommand) { 'external-signing-hook' } else { 'unsigned-development' });
        installer = (Split-Path $installer -Leaf); sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash;
        payload = @($files | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash } }) }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding utf8
    "$($manifest.sha256)  $($manifest.installer)" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Built $($manifest.installer); self-contained payload validated."
} finally { Pop-Location }

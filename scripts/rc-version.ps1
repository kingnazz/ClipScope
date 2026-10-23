# Build-time metadata; Version.props is the only release-version source.
function Get-ClipScopeVersion([string] $Override = '') {
    [xml]$props = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Version.props') -Raw
    $value = if ($Override) { $Override } else { $props.SelectSingleNode('/Project/PropertyGroup/ClipScopeVersion').InnerText }
    if ($value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-rc\.([1-9][0-9]*))?$') {
        throw 'Version.props must contain a stable SemVer or numbered release candidate.'
    }
    $parts = @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3], $(if ($Matches.ContainsKey(4)) { [int]$Matches[4] } else { 0 }))
    if (@($parts | Where-Object { $_ -gt 65535 }).Count) { throw 'Windows version components exceed 65535.' }
    [PSCustomObject]@{ Version = $value; FileVersion = ($parts -join '.'); Prefix = ($parts[0..2] -join '.');
        Publisher = [string]$props.Project.PropertyGroup.Company; AppId = 'ClipScope.Desktop';
        Compiler = [string]$props.Project.PropertyGroup.ClipScopeInstallerCompilerVersion;
        Runtime = [string]$props.Project.PropertyGroup.RuntimeFrameworkVersion; Crt = [string]$props.Project.PropertyGroup.ClipScopeCrtVersion }
}

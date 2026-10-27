# Repository latency measurement, separate from UI debounce/render latency.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
Assert-IsolatedDesktopRunner
$root = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $root 'artifacts/rc/evidence'
foreach ($count in @(100,10000)) {
    $owner = New-OwnedTestDirectory (Join-Path $root "artifacts/search-$count")
    try {
        $json = & dotnet (Join-Path $root 'tools/ClipScope.Diagnostics/bin/Release/net10.0/ClipScope.Diagnostics.dll') benchmark $owner.Path $count
        if ($LASTEXITCODE -ne 0) { throw 'Repository benchmark failed.' }
        $json | Set-Content -LiteralPath (Join-Path $evidence "search-$count.json") -Encoding utf8
        Write-Output "::notice title=RC search measurement::$json"
    } finally { Remove-OwnedTestDirectory $owner }
}

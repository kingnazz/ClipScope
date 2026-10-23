# Focused safety regression checks, using only a uniquely owned temp fixture.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
$fixture = New-OwnedTestDirectory (Join-Path ([IO.Path]::GetTempPath()) ('ClipScope-ownership-' + [Guid]::NewGuid().ToString('N')))
try {
    $existing = Join-Path $fixture.Path 'existing'
    [void](New-Item -ItemType Directory -Path $existing)
    [IO.File]::WriteAllText((Join-Path $existing 'sentinel'), 'keep')
    $rejected = $false
    try { New-OwnedTestDirectory $existing | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath (Join-Path $existing 'sentinel'))) { throw 'Existing directory protection failed.' }
    $owned = New-OwnedTestDirectory (Join-Path $fixture.Path 'owned')
    [void](New-Item -ItemType Directory -Path (Join-Path $owned.Path 'nested'))
    [IO.File]::WriteAllText((Join-Path $owned.Path 'nested/file'), 'synthetic')
    $wrongOwner = [PSCustomObject]@{ Path = $owned.Path; Token = 'wrong' }
    $rejected = $false
    try { Remove-OwnedTestDirectory $wrongOwner } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $owned.Path)) { throw 'Ownership mismatch protection failed.' }
    $junction = Join-Path $owned.Path 'outside'
    [void](New-Item -ItemType Junction -Path $junction -Target $existing)
    $rejected = $false
    try { Remove-OwnedTestDirectory $owned } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath (Join-Path $existing 'sentinel'))) { throw 'Junction protection failed.' }
    [IO.Directory]::Delete($junction, $false)
    Remove-OwnedTestDirectory $owned
    if (Test-Path -LiteralPath $owned.Path) { throw 'Owned directory cleanup failed.' }
    Write-Output '::notice title=ClipScope profile safety::PASS: existing data, owner mismatch, junction refusal, owned cleanup'
} finally { Remove-OwnedTestDirectory $fixture }

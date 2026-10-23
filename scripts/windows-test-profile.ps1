# Shared ownership checks for synthetic desktop tests. Never adopt an existing tree.
function Assert-IsolatedDesktopRunner {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        throw 'Desktop automation requires an isolated GitHub-hosted runner; real user desktops are protected.'
    }
}
function Assert-NoTestReparsePoint([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse point in a test directory.' }
}
function New-OwnedTestDirectory([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $full) { throw 'Existing data is protected; a fresh test directory is required.' }
    $parent = [IO.DirectoryInfo]([IO.Path]::GetDirectoryName($full))
    while ($parent) { Assert-NoTestReparsePoint $parent.FullName; $parent = $parent.Parent }
    # Without -Force this fails if another process created the directory since the check.
    [void](New-Item -ItemType Directory -Path $full -ErrorAction Stop)
    $token = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $full '.clipscope-test-owner'), $token)
    return [PSCustomObject]@{ Path = $full; Token = $token }
}
function Remove-OwnedTestDirectory($Owner) {
    if ($null -eq $Owner -or -not (Test-Path -LiteralPath $Owner.Path)) { return }
    Assert-NoTestReparsePoint $Owner.Path
    $marker = Join-Path $Owner.Path '.clipscope-test-owner'
    Assert-NoTestReparsePoint $marker
    if ([IO.File]::ReadAllText($marker) -cne $Owner.Token) { throw 'Test directory ownership changed; refusing cleanup.' }
    # Validate the whole tree first. Get-ChildItem -Recurse/Remove-Item -Recurse can
    # follow or ambiguously handle junctions; use a checked, non-following walk.
    function Get-OwnedEntries([string] $Directory) {
        foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
            Assert-NoTestReparsePoint $entry.FullName
            if ($entry.PSIsContainer) { Get-OwnedEntries $entry.FullName }
            $entry
        }
    }
    $entries = @(Get-OwnedEntries $Owner.Path)
    foreach ($entry in $entries) {
        Assert-NoTestReparsePoint $entry.FullName
        if ($entry.PSIsContainer) { [IO.Directory]::Delete($entry.FullName, $false) }
        else { [IO.File]::Delete($entry.FullName) }
    }
    [IO.Directory]::Delete($Owner.Path, $false)
}

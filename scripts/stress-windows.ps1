# powershell.exe -NoProfile -STA -File scripts/stress-windows.ps1
# Installs only on a fresh GitHub-hosted desktop, with synthetic clipboard data.
param([int] $Minutes = 20, [int] $Operations = 2000, [string] $OutputDirectory = 'artifacts/rc')
$ErrorActionPreference = 'Stop'
if ($Minutes -lt 1 -or $Minutes -gt 180 -or $Operations -lt 100 -or $Operations -gt 20000) { throw 'Use 1..180 minutes and 100..20000 operations.' }
. (Join-Path $PSScriptRoot 'windows-rc-common.ps1')
. (Join-Path $PSScriptRoot 'rc-version.ps1')
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$evidence = Join-Path $output 'evidence'
$data = Join-Path $env:LOCALAPPDATA 'ClipScope'
$install = Join-Path $env:LOCALAPPDATA 'Programs/ClipScope'
$exe = Join-Path $install 'ClipScope.App.exe'
if ((Test-Path $install) -or (Test-Path $data) -or (Get-Process 'ClipScope.App' -ErrorAction SilentlyContinue)) { throw 'A fresh runner/profile is required.' }
$profile = New-OwnedTestDirectory $data
$app = $null; $producer = $null; $counter = $null
$samples = New-Object 'System.Collections.Generic.List[object]'
function Sample-Resources([string] $Stage) {
    $app.Refresh()
    if ($app.HasExited) { throw 'Application exited during stress.' }
    $memory = New-Object ClipScopeRcNative+MemoryCounters
    $memory.Size = [Runtime.InteropServices.Marshal]::SizeOf($memory)
    if (-not [ClipScopeRcNative]::GetProcessMemoryInfo($app.Handle,[ref]$memory,$memory.Size)) { throw 'Private working-set measurement failed.' }
    $databaseBytes = (Get-ChildItem -LiteralPath $data -Filter 'clips.db*' | Measure-Object -Property Length -Sum).Sum
    $mediaBytes = (Get-ChildItem -LiteralPath (Join-Path $data 'media'),(Join-Path $data 'thumbnails') -File -Recurse | Measure-Object -Property Length -Sum).Sum
    $samples.Add([PSCustomObject]@{ utc = [DateTime]::UtcNow.ToString('o'); stage = $Stage;
        privateWorkingSetBytes = $memory.PrivateWorkingSetSize.ToUInt64(); workingSetBytes = $app.WorkingSet64;
        privateBytes = $app.PrivateMemorySize64; handles = $app.HandleCount;
        gdi = [ClipScopeRcNative]::GetGuiResources($app.Handle,0); user = [ClipScopeRcNative]::GetGuiResources($app.Handle,1);
        totalCpuMs = $app.TotalProcessorTime.TotalMilliseconds; databaseBytes = $databaseBytes; mediaBytes = $mediaBytes })
    $samples | Export-Csv -LiteralPath (Join-Path $evidence 'resources.csv') -NoTypeInformation
}
try {
    $version = Get-ClipScopeVersion
    $setup = Start-Process (Join-Path $output "ClipScope-$($version.Version)-win-x64-setup.exe") -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw 'Stress installation failed.' }
    '{"ScreenshotsEnabled":true}' | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
    $producer = New-Object System.Windows.Forms.Form
    $producer.Text = 'ClipScope synthetic endurance source'; $producer.Width = 640; $producer.Height = 400
    $producer.Show(); $producer.Activate()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $app = Start-Process -FilePath $exe -PassThru
    Wait-Rc { $script:window = Get-RcWindow $app; $null -ne $script:window } 'cold installed launch'
    $coldMs = $watch.Elapsed.TotalMilliseconds
    Close-RcHistory
    $app.Refresh(); $cpuBefore = $app.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 10
    $app.Refresh(); $idleCpu = ($app.TotalProcessorTime.TotalMilliseconds - $cpuBefore) / 10000 * 100
    $counterFile = Join-Path $evidence 'managed-heap.csv'
    # Pinned developer tool, never shipped. EventCounters keeps heap units in MB.
    $counterArgs = @('tool','run','dotnet-counters','--','collect','--process-id',"$($app.Id)",
        '--counters','EventCounters\System.Runtime[gc-heap-size]','--refresh-interval','5','--format','csv','--output',('"' + $counterFile + '"'),
        '--duration',([TimeSpan]::FromMinutes($Minutes + 2).ToString('hh\:mm\:ss')))
    $counter = Start-Process dotnet -ArgumentList $counterArgs -WorkingDirectory $root -PassThru -RedirectStandardOutput (Join-Path $evidence 'counters-stdout.txt') -RedirectStandardError (Join-Path $evidence 'counters-stderr.txt')
    Sample-Resources 'baseline'
    $warm = @(); $hotkey = @(); $captureLatency = @()
    $session = [Diagnostics.Stopwatch]::StartNew()
    $interval = [TimeSpan]::FromMinutes($Minutes).TotalMilliseconds / $Operations
    for ($index = 0; $index -lt $Operations; $index++) {
        $producer.Activate(); [void][ClipScopeRcNative]::SetForegroundWindow($producer.Handle)
        Wait-Rc { [ClipScopeRcNative]::GetForegroundWindow() -eq $producer.Handle } 'synthetic foreground source'
        if ($index % 10 -eq 0) {
            $bitmap = New-Object System.Drawing.Bitmap 160,100
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.Clear([Drawing.Color]::FromArgb(255,($index % 255),80,160)); [System.Windows.Forms.Clipboard]::SetImage($bitmap) }
            finally { $graphics.Dispose(); $bitmap.Dispose() }
        } else {
            $needle = 'endurance' + $index.ToString('D6')
            $watch.Restart()
            [System.Windows.Forms.Clipboard]::SetText("$needle Unicode 日本語 🧪`r`nsynthetic multiline")
            if ($index % 100 -eq 1) {
                Wait-Rc { (Get-RcProbe $data $needle).payloadHits -eq 1 } 'hidden capture persisted'
                $captureLatency += $watch.Elapsed.TotalMilliseconds
            }
        }
        if ($index % 100 -eq 0) {
            $watch.Restart(); Open-RcHistory $app; $warm += $watch.Elapsed.TotalMilliseconds; Close-RcHistory
            $producer.Activate(); [void][ClipScopeRcNative]::SetForegroundWindow($producer.Handle)
            $watch.Restart(); Open-RcHistory $app 'hotkey'; $hotkey += $watch.Elapsed.TotalMilliseconds; Close-RcHistory
        }
        if ($index -eq [int]($Operations / 2)) {
            Open-RcHistory $app
            Invoke-RcAction 'Settings'
            $selection = $null
            Wait-Rc { $script:section = Get-RcPattern 'Capture' ([System.Windows.Automation.SelectionItemPattern]::Pattern); $null -ne $script:section } 'Capture settings'
            $script:section.Select()
            Wait-Rc { $script:snapshot = Get-RcPattern 'Source snapshots' ([System.Windows.Automation.TogglePattern]::Pattern); $null -ne $script:snapshot } 'Snapshot toggle'
            $script:snapshot.Toggle(); Invoke-RcAction 'Save'
            Wait-Rc { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).ScreenshotsEnabled -eq $false } 'snapshots disabled for second half'
            Close-RcHistory
        }
        if ($index % 10 -eq 0) { Sample-Resources $(if ($index -lt $Operations / 2) { 'snapshots-on' } else { 'snapshots-off' }) }
        $remaining = (($index + 1) * $interval) - $session.Elapsed.TotalMilliseconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Min($remaining,60000)) }
    }
    Start-Sleep -Seconds 10
    Sample-Resources 'settled'
    $probe = Get-RcProbe $data 'endurance'
    if ($probe.clips -lt [int]($Operations * 0.9)) { throw 'Too many clipboard operations failed to persist.' }
    $elapsedMinutes = $session.Elapsed.TotalMinutes
    Stop-RcApp $exe $app
    if (-not $counter.WaitForExit(15000)) { $counter.Kill(); throw 'Managed counter collection did not stop with the app.' }
    if (-not (Test-Path $counterFile) -or (Get-Item $counterFile).Length -lt 100) { throw 'No managed heap evidence was recorded.' }
    @{ os = [Environment]::OSVersion.VersionString; sha = (& git -C $root rev-parse HEAD); durationMinutes = $elapsedMinutes;
       generatedOperations = $Operations; persistedClips = $probe.clips; coldLaunchMs = $coldMs;
       idleCpuOneCorePercent = $idleCpu; trayCallbackReopenMs = $warm; globalHotkeyFocusMs = $hotkey;
       hiddenCaptureProbeMs = $captureLatency; latencyNote = 'UIA polling at 50ms; capture probes include diagnostic process startup';
       managedHeap = 'managed-heap.csv (EventCounters gc-heap-size MB)'; integrity = $probe.integrity;
       result = 'Measurements complete; inspect trends against observed baseline before assigning leak thresholds.' } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'stress.json') -Encoding UTF8
    $summary = "Measured $Operations operations, $($probe.clips) persisted, $([Math]::Round($elapsedMinutes,1)) min; cold=$([Math]::Round($coldMs))ms idle=$([Math]::Round($idleCpu,3))% of one core; private WS $($samples[0].privateWorkingSetBytes)->$($samples[$samples.Count-1].privateWorkingSetBytes), handles $($samples[0].handles)->$($samples[$samples.Count-1].handles), GDI $($samples[0].gdi)->$($samples[$samples.Count-1].gdi), USER $($samples[0].user)->$($samples[$samples.Count-1].user)."
    Write-Output "::notice title=RC stress measurements::$summary"
    $remove = Start-Process (Join-Path $install 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($remove.ExitCode -ne 0) { throw 'Stress uninstall failed.' }
} catch {
    Write-Output ('::error title=RC stress::' + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A') + " at line $($_.InvocationInfo.ScriptLineNumber), phase $script:phase")
    throw
} finally {
    if ($app -and -not $app.HasExited) { Stop-RcApp $exe $app }
    if ($counter -and -not $counter.HasExited) { $counter.Kill(); $counter.WaitForExit() }
    if ($producer) { $producer.Close(); $producer.Dispose() }
    [System.Windows.Forms.Clipboard]::Clear()
    Remove-OwnedTestDirectory $profile
}

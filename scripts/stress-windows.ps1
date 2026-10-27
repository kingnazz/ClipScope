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
    $mediaBytes = [long](Get-ChildItem -LiteralPath (Join-Path $data 'media'),(Join-Path $data 'thumbnails') -File -Recurse | Measure-Object -Property Length -Sum).Sum
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
    # Pinned developer tool, never shipped. Committed GC memory uses GCMemoryInfo.
    # The prior gc-committed counter emitted an impossible negative sample.
    $counterArgs = @('tool','run','dotnet-counters','--','collect','--process-id',"$($app.Id)",
        '--counters','EventCounters\System.Runtime[gc-committed]','--refresh-interval','5','--format','csv','--output',('"' + $counterFile + '"'),
        '--duration',([TimeSpan]::FromMinutes($Minutes + 2).ToString('hh\:mm\:ss')))
    $counter = Start-Process dotnet -ArgumentList $counterArgs -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidence 'counters-stdout.txt') -RedirectStandardError (Join-Path $evidence 'counters-stderr.txt')
    Sample-Resources 'baseline'
    $warm = @(); $hotkey = @(); $captureLatency = @()
    $imageSelectionMs = $null
    $session = [Diagnostics.Stopwatch]::StartNew()
    $interval = [TimeSpan]::FromMinutes($Minutes).TotalMilliseconds / $Operations
    for ($index = 0; $index -lt $Operations; $index++) {
        $producer.Activate(); Set-RcForeground $producer.Handle
        Wait-Rc { [ClipScopeRcNative]::GetForegroundWindow() -eq $producer.Handle } 'synthetic foreground source'
        if ($index % 10 -eq 0) {
            $bitmap = New-Object System.Drawing.Bitmap 160,100
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.Clear([Drawing.Color]::FromArgb(255,($index % 255),80,160)); Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::SetImage($bitmap) } }
            finally { $graphics.Dispose(); $bitmap.Dispose() }
        } else {
            $needle = 'endurance' + $index.ToString('D6')
            $watch.Restart()
            Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::SetText("$needle Unicode 日本語 🧪`r`nsynthetic multiline") }
            if ($index % 100 -eq 1) {
                Wait-Rc { (Get-RcProbe $data $needle).payloadHits -eq 1 } 'hidden capture persisted'
                $captureLatency += $watch.Elapsed.TotalMilliseconds
            }
        }
        if ($index % 100 -eq 0) {
            $watch.Restart(); Open-RcHistory $app 'tray'; $warm += $watch.Elapsed.TotalMilliseconds; Close-RcHistory
            $producer.Activate(); Set-RcForeground $producer.Handle
            $watch.Restart(); Open-RcHistory $app 'hotkey'; $hotkey += $watch.Elapsed.TotalMilliseconds; Close-RcHistory
        }
        if ($index -eq 0) {
            Open-RcHistory $app
            (Get-RcPattern 'Text' ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Wait-Rc {
                $oldPreview = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, '160 × 100 pixels')))
                $null -eq $oldPreview
            } 'previous image preview cleared before timing'
            $imagesFilter = Get-RcPattern 'Images' ([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $watch.Restart(); $imagesFilter.Select()
            Wait-Rc {
                $decoded = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, '160 × 100 pixels')))
                $null -ne $decoded
            } 'clipboard image decoded in detail pane'
            $imageSelectionMs = $watch.Elapsed.TotalMilliseconds
            Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Invoke-RcAction 'Copy'
            Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsImage() } 'native image restored from history'
            $restored = Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::GetImage() }
            try {
                if ($restored.Width -ne 160 -or $restored.Height -ne 100 -or $restored.GetPixel(80,50).ToArgb() -ne [Drawing.Color]::FromArgb(255,0,80,160).ToArgb()) { throw 'Restored image dimensions or pixel differ.' }
            } finally { $restored.Dispose() }
            (Get-RcPattern 'All clips' ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Close-RcHistory
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
    $expectedImages = [int][Math]::Ceiling($Operations / 10.0)
    if ($probe.kindCounts.images -ne $expectedImages -or $probe.kindCounts.text -ne ($Operations - $expectedImages)) { throw 'Generated text/image operation counts differ from persisted clips.' }
    $elapsedMinutes = $session.Elapsed.TotalMinutes
    Stop-RcApp $exe $app
    if (-not $counter.WaitForExit(15000)) {
        # The diagnostic CLI can leave its console reader alive after exporting.
        # Stop only the observer after the application has exited; require the
        # full-duration exported series below, not merely a non-empty file.
        & taskkill.exe /PID $counter.Id /T /F | Out-Null
        $counter.WaitForExit()
        Write-Output '::notice title=RC diagnostic observer::Counter CLI remained alive after app exit; observer stopped after export.'
    }
    if (-not (Test-Path $counterFile) -or (Get-Item $counterFile).Length -lt 100) { throw 'No managed heap evidence was recorded.' }
    $heap = @(Import-Csv -LiteralPath $counterFile | Where-Object { $_.'Counter Name' -eq 'GC Committed Bytes (MB)' } | ForEach-Object { [double]::Parse($_.'Mean/Increment',[Globalization.CultureInfo]::InvariantCulture) })
    if ($heap.Count -lt [int]($Minutes * 60 / 5 * 0.8)) { throw 'Managed heap counter series does not cover the requested session.' }
    if (@($heap | Where-Object { [double]::IsNaN($_) -or [double]::IsInfinity($_) -or $_ -lt 0 }).Count -gt 0) { throw 'Managed GC committed-memory series contains invalid samples.' }
    $heapSummary = @{ metric='GC committed memory (MB), not live object size'; firstMB=$heap[0]; lastMB=$heap[$heap.Count-1]; minMB=($heap | Measure-Object -Minimum).Minimum; maxMB=($heap | Measure-Object -Maximum).Maximum; samples=$heap.Count }
    $trend = @(); for ($sampleIndex = 0; $sampleIndex -lt $samples.Count; $sampleIndex += 10) {
        $point = $samples[$sampleIndex]
        $trend += @{ stage=$point.stage; privateWorkingSetMB=[Math]::Round($point.privateWorkingSetBytes/1MB,2); privateMB=[Math]::Round($point.privateBytes/1MB,2); handles=$point.handles; gdi=$point.gdi; user=$point.user }
    }
    @{ os = [Environment]::OSVersion.VersionString; sha = (& git -C $root rev-parse HEAD); durationMinutes = $elapsedMinutes;
       generatedOperations = $Operations; persistedClips = $probe.clips; coldLaunchMs = $coldMs;
       idleCpuOneCorePercent = $idleCpu; trayCallbackReopenMs = $warm; globalHotkeyFocusMs = $hotkey;
       hiddenCaptureProbeMs = $captureLatency; imageFilterToDecodedMs = $imageSelectionMs; imageRoundtrip = 'PASS dimensions and sampled pixel'; latencyNote = 'UIA polling at 50ms; capture probes include diagnostic process startup';
       resourceTrend = $trend; heapSummary = $heapSummary;
       managedHeap = 'managed-heap.csv (EventCounters gc-committed MB)'; integrity = $probe.integrity;
       result = 'Measurements complete; inspect trends against observed baseline before assigning leak thresholds.' } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'stress.json') -Encoding UTF8
    $summary = "Measured $Operations operations, $($probe.clips) persisted, $([Math]::Round($elapsedMinutes,1)) min; cold=$([Math]::Round($coldMs))ms idle=$([Math]::Round($idleCpu,3))% of one core; private WS $($samples[0].privateWorkingSetBytes)->$($samples[$samples.Count-1].privateWorkingSetBytes), handles $($samples[0].handles)->$($samples[$samples.Count-1].handles), GDI $($samples[0].gdi)->$($samples[$samples.Count-1].gdi), USER $($samples[0].user)->$($samples[$samples.Count-1].user)."
    Write-Output "::notice title=RC stress measurements::$summary"
    Write-Output ('::notice title=RC storage measurements::' + (@{ textClips=$probe.kindCounts.text; imageClips=$probe.kindCounts.images;
        databaseBytesFirst=$samples[0].databaseBytes; databaseBytesLast=$samples[$samples.Count-1].databaseBytes;
        mediaBytesFirst=$samples[0].mediaBytes; mediaBytesLast=$samples[$samples.Count-1].mediaBytes;
        privateBytesFirst=$samples[0].privateBytes; privateBytesLast=$samples[$samples.Count-1].privateBytes } | ConvertTo-Json -Compress))
    Write-Output ('::notice title=RC heap measurements::' + ($heapSummary | ConvertTo-Json -Compress))
    Write-Output ('::notice title=RC resource trend::' + ($trend | ConvertTo-Json -Compress))
    Write-Output ('::notice title=RC UI latency samples::' + (@{ coldMs=$coldMs; trayCallbackMs=$warm; hotkeyMs=$hotkey; captureProbeMs=$captureLatency; imageFilterToDecodedMs=$imageSelectionMs } | ConvertTo-Json -Compress))
    $remove = Start-Process (Join-Path $install 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($remove.ExitCode -ne 0) { throw 'Stress uninstall failed.' }
} catch {
    Write-Output ('::error title=RC stress::' + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A') + " at line $($_.InvocationInfo.ScriptLineNumber), phase $script:phase")
    throw
} finally {
    if ($app -and -not $app.HasExited) { Stop-RcApp $exe $app }
    if ($counter -and -not $counter.HasExited) { & taskkill.exe /PID $counter.Id /T /F | Out-Null; $counter.WaitForExit() }
    if ($producer) { $producer.Close(); $producer.Dispose() }
    Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }
    Remove-OwnedTestDirectory $profile
}

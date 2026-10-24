# Real applications on the isolated hosted desktop. Does not certify Windows 11.
param([string] $OutputDirectory = 'artifacts/rc')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'windows-rc-common.ps1')
. (Join-Path $PSScriptRoot 'rc-version.ps1')
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$evidence = Join-Path $output 'evidence'
$data = Join-Path $env:LOCALAPPDATA 'ClipScope'
$install = Join-Path $env:LOCALAPPDATA 'Programs/ClipScope'
$exe = Join-Path $install 'ClipScope.App.exe'
if ((Test-Path $install) -or (Test-Path $data) -or (Get-Process 'ClipScope.App' -ErrorAction SilentlyContinue)) { throw 'A fresh hosted desktop is required.' }
$profile = New-OwnedTestDirectory $data
$fixtures = New-OwnedTestDirectory (Join-Path $root 'artifacts/compat-fixtures')
$app = $null; $sourceWindow = [IntPtr]::Zero; $notepad = $null; $pauseSource = $null
$results = New-Object 'System.Collections.Generic.List[object]'
function Find-Source([string] $Title) {
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $windows) {
        if ($element.Current.Name.Contains($Title) -and -not $element.Current.IsOffscreen) { return [IntPtr]$element.Current.NativeWindowHandle }
    }
    return [IntPtr]::Zero
}
function Copy-RealSource([IntPtr] $Handle) {
    Set-RcForeground $Handle
    Wait-Rc { [ClipScopeRcNative]::GetForegroundWindow() -eq $Handle } 'real source foreground'
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait('^c')
}
function Restore-FromHistory([string] $Needle) {
    Open-RcHistory $app
    $search = Get-RcPattern 'Search clipboard history' ([System.Windows.Automation.ValuePattern]::Pattern)
    $search.SetValue($Needle)
    Start-Sleep -Milliseconds 500
    Invoke-RcAction 'Copy'
}
function Record-Capture([string] $Name, [string] $Needle, [string] $Version) {
    Wait-Rc { (Get-RcProbe $data $Needle).payloadHits -ge 1 } "$Name persisted"
    Start-Sleep -Milliseconds 1500
    $record = Get-RcProbe $data $Needle
    $results.Add([PSCustomObject]@{ app = $Name; version = $Version; result = 'PASS capture'; stored = $record.latest;
        screenshot = $(if ($record.latest.context) { 'PNG decoded; visual crop/GPU correctness requires physical review' } else { 'No context captured; NOT VERIFIED' }) })
    return $record
}
try {
    $version = Get-ClipScopeVersion
    $setup = Start-Process (Join-Path $output "ClipScope-$($version.Version)-win-x64-setup.exe") -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw 'Compatibility installation failed.' }
    '{"ScreenshotsEnabled":true}' | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8
    $app = Start-Process $exe -ArgumentList '--background' -PassThru
    Wait-Rc { [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',$null) -ne [IntPtr]::Zero } 'installed native host'

    # Native Notepad, real Ctrl+C and restore + Ctrl+V, not a Forms text producer.
    if (Get-Process notepad -ErrorAction SilentlyContinue) { throw 'A pre-existing Notepad instance is protected.' }
    $needle = 'rcnotepad' + [Guid]::NewGuid().ToString('N')
    $text = "$needle Unicode 日本語 🧪`r`nsecond line https://example.test/"
    $file = Join-Path $fixtures.Path "$needle.txt"
    [IO.File]::WriteAllText($file,$text,(New-Object Text.UTF8Encoding $true))
    $notepad = Start-Process notepad.exe -ArgumentList ('"' + $file + '"') -PassThru
    Wait-Rc { $script:sourceWindow = Find-Source $needle; $script:sourceWindow -ne [IntPtr]::Zero } 'Notepad fixture window'
    Copy-RealSource $sourceWindow
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $text } 'Notepad native Unicode copy'
    $record = Record-Capture 'Notepad' $needle ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:WINDIR 'System32/notepad.exe')).FileVersion)
    if ($record.latest.source -ne 'notepad') { throw 'Notepad source attribution is wrong.' }
    [System.Windows.Forms.Clipboard]::Clear(); Restore-FromHistory $needle
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $text } 'Notepad exact Unicode restoration'
    Close-RcHistory
    [void][ClipScopeRcNative]::SetForegroundWindow($sourceWindow)
    [System.Windows.Forms.SendKeys]::SendWait('^a^v^a^c')
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $text } 'Notepad real paste roundtrip'

    # Exercise the installed Settings integration, including successful removal.
    foreach ($enableStartup in @($true,$false)) {
        Open-RcHistory $app; Invoke-RcAction 'Settings'
        Wait-Rc { $script:startupToggle = Get-RcPattern 'Start with Windows' ([System.Windows.Automation.TogglePattern]::Pattern); $null -ne $script:startupToggle } 'startup toggle'
        $script:startupToggle.Toggle(); Invoke-RcAction 'Save'
        Wait-Rc { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).LaunchAtStartup -eq $enableStartup } 'startup preference saved'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
        try { $entry = $key.GetValue('ClipScope',$null) } finally { $key.Dispose() }
        if ($enableStartup -and $entry -ne ('"' + $exe + '" --background')) { throw 'Settings enabled an incorrect startup path.' }
        if (-not $enableStartup -and $null -ne $entry) { throw 'Settings did not remove startup registration.' }
        Close-RcHistory
    }
    $results.Add(@{ app='Installed startup Settings'; result='PASS enable/disable exact one HKCU Run value; real sign-in NOT TESTED' })

    # Configure a real application's exclusion through the shipped Settings UI.
    Open-RcHistory $app; Invoke-RcAction 'Settings'
    Wait-Rc { $script:section = Get-RcPattern 'Privacy' ([System.Windows.Automation.SelectionItemPattern]::Pattern); $null -ne $script:section } 'Privacy section'
    $script:section.Select()
    Wait-Rc { $script:exclusions = Get-RcPattern 'Excluded apps, one executable path or process name per line' ([System.Windows.Automation.ValuePattern]::Pattern); $null -ne $script:exclusions } 'Excluded apps input'
    $script:exclusions.SetValue('notepad.exe'); Invoke-RcAction 'Save'
    Wait-Rc { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).ExcludedApplications -contains 'notepad.exe' } 'exclusion persisted'
    Close-RcHistory
    foreach ($restart in @($false,$true)) {
        if ($restart) {
            Stop-RcApp $exe $app
            $app = Start-Process $exe -ArgumentList '--background' -PassThru
            Wait-Rc { [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',$null) -ne [IntPtr]::Zero } 'restart with exclusions'
        }
        $secret = 'rcexcluded' + [Guid]::NewGuid().ToString('N')
        $before = Get-RcProbe $data $secret
        # Paste into Notepad, clear clipboard, then make Notepad itself own Ctrl+C.
        [System.Windows.Forms.Clipboard]::SetText($secret)
        [void][ClipScopeRcNative]::SetForegroundWindow($sourceWindow)
        [System.Windows.Forms.SendKeys]::SendWait('^a^v')
        [System.Windows.Forms.Clipboard]::Clear()
        Copy-RealSource $sourceWindow
        Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $secret } 'excluded Notepad copied'
        Start-Sleep -Seconds 2
        $after = Get-RcProbe $data $secret
        if ($after.clips -ne $before.clips -or $after.payloadHits -ne 0 -or $after.metadataHits -ne 0 -or $after.ftsHits -ne 0 -or $after.mediaFiles -ne $before.mediaFiles) { throw 'Excluded application leaked persisted content, metadata, FTS or media.' }
    }
    $results.Add(@{ app = 'Notepad exclusion'; result = 'PASS content/metadata/FTS/media absence before and after restart' })
    # Only processes launched after the explicit no-Notepad check belong to this test.
    Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
    $notepad = $null; $sourceWindow = [IntPtr]::Zero

    $pauseSource = New-Object System.Windows.Forms.Form
    $pauseSource.Text='ClipScope synthetic pause source'; $pauseSource.Show(); Set-RcForeground $pauseSource.Handle
    Invoke-RcTrayMenu $app 'Pause clipboard capture'
    Wait-Rc { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).CaptureEnabled -eq $false } 'tray pause persisted'
    foreach ($restart in @($false,$true)) {
        if ($restart) {
            Stop-RcApp $exe $app
            $app = Start-Process $exe -ArgumentList '--background' -PassThru
            Wait-Rc { [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',$null) -ne [IntPtr]::Zero } 'restart while paused'
        }
        $pausedNeedle='rcpaused'+[Guid]::NewGuid().ToString('N')
        $pauseSource.Activate(); Set-RcForeground $pauseSource.Handle
        Wait-Rc { [ClipScopeRcNative]::GetForegroundWindow() -eq $pauseSource.Handle } 'pause source foreground'
        [System.Windows.Forms.Clipboard]::SetText($pausedNeedle)
        Start-Sleep -Seconds 2
        $record=Get-RcProbe $data $pausedNeedle
        if ($record.payloadHits -ne 0 -or $record.ftsHits -ne 0) { throw 'Paused capture persisted content, including after restart.' }
    }
    Invoke-RcTrayMenu $app 'Resume clipboard capture'
    Wait-Rc { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).CaptureEnabled -eq $true } 'tray resume persisted'
    $resumedNeedle='rcresumed'+[Guid]::NewGuid().ToString('N')
    $pauseSource.Activate(); Set-RcForeground $pauseSource.Handle
    [System.Windows.Forms.Clipboard]::SetText($resumedNeedle)
    Wait-Rc { (Get-RcProbe $data $resumedNeedle).payloadHits -eq 1 } 'capture resumes after paused restart'
    Invoke-RcTrayMenu $app 'Settings'
    Wait-Rc { $script:window=Get-RcWindow $app; $script:window -and (Get-RcPattern 'Shortcut key' ([System.Windows.Automation.ValuePattern]::Pattern)) } 'tray Settings opened'
    Invoke-RcAction 'Cancel'; Close-RcHistory
    $pauseSource.Close(); $pauseSource.Dispose(); $pauseSource=$null
    $results.Add(@{ app='Tray menu and pause'; result='PASS real popup Pause/Resume/Settings via notification callback; paused restart does not capture, resume captures' })

    foreach ($browser in @(
        @{ name='Edge'; path=(Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe') },
        @{ name='Chrome'; path=(Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe') })) {
        if (-not (Test-Path $browser.path)) { $results.Add(@{ app=$browser.name; result='NOT TESTED: not installed' }); continue }
        $needle = 'rcbrowser' + [Guid]::NewGuid().ToString('N')
        $html = Join-Path $fixtures.Path "$needle.html"
        [IO.File]::WriteAllText($html,"<!doctype html><meta charset=utf-8><title>$needle</title><h1>$needle</h1><p>Unicode 日本語 🧪</p><p><b>Rich HTML</b> https://example.test/</p>")
        $browserProfile = Join-Path $fixtures.Path ($browser.name + '-profile')
        $child = Start-Process $browser.path -ArgumentList @('--no-first-run','--no-default-browser-check','--disable-background-mode',('--user-data-dir="' + $browserProfile + '"'),('--app="' + ([Uri]$html).AbsoluteUri + '"')) -PassThru
        Wait-Rc { $script:sourceWindow = Find-Source $needle; $script:sourceWindow -ne [IntPtr]::Zero } ($browser.name + ' real browser window') 40
        Copy-RealSource $sourceWindow
        Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsData('HTML Format') -and [System.Windows.Forms.Clipboard]::GetText().Contains($needle) } ($browser.name + ' native HTML copy')
        $copied = [System.Windows.Forms.Clipboard]::GetText()
        $htmlCopied = [System.Windows.Forms.Clipboard]::GetData('HTML Format')
        $record = Record-Capture $browser.name $needle ([Diagnostics.FileVersionInfo]::GetVersionInfo($browser.path).FileVersion)
        if (-not $record.latest.hasHtml) { throw ($browser.name + ' HTML payload missing from storage.') }
        [System.Windows.Forms.Clipboard]::Clear(); Restore-FromHistory $needle
        Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $copied -and [System.Windows.Forms.Clipboard]::GetData('HTML Format') -eq $htmlCopied } ($browser.name + ' text and CF_HTML restoration')
        Close-RcHistory
        [void][ClipScopeRcNative]::PostMessage($sourceWindow,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
        $sourceWindow = [IntPtr]::Zero
        if (-not $child.HasExited) { [void]$child.WaitForExit(10000) }
    }
    # Explorer publishes the genuine CF_HDROP list for the selected fixture file.
    $folderName = 'rcexplorer' + [Guid]::NewGuid().ToString('N')
    $folder = Join-Path $fixtures.Path $folderName
    [void][IO.Directory]::CreateDirectory($folder)
    $file = Join-Path $folder 'rcfile-synthetic.txt'; [IO.File]::WriteAllText($file,'synthetic file payload')
    [void](Start-Process explorer.exe -ArgumentList ('/select,"' + $file + '"') -PassThru)
    Wait-Rc { $script:sourceWindow = Find-Source $folderName; $script:sourceWindow -ne [IntPtr]::Zero } 'Explorer fixture window'
    [void][ClipScopeRcNative]::SetForegroundWindow($sourceWindow)
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsFileDropList() } 'Explorer CF_HDROP copy'
    $record = Record-Capture 'File Explorer' 'rcfile' ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:WINDIR 'explorer.exe')).FileVersion)
    if ($record.latest.kind -ne 'Files' -or $record.latest.fileCount -ne 1) { throw 'Explorer file list was not stored.' }
    [System.Windows.Forms.Clipboard]::Clear(); Restore-FromHistory 'rcfile'
    Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsFileDropList() -and [System.Windows.Forms.Clipboard]::GetFileDropList()[0] -eq $file } 'Explorer exact file restoration'
    Close-RcHistory
    [void][ClipScopeRcNative]::PostMessage($sourceWindow,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    $sourceWindow = [IntPtr]::Zero
    # Actual Explorer process restart, restricted to this hosted desktop's session.
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    Get-Process explorer | Where-Object { $_.SessionId -eq $sessionId } | Stop-Process -Force
    [void](Start-Process explorer.exe -PassThru)
    Wait-Rc { (Get-Content (Join-Path $data 'logs/diagnostic.log') -Raw).Contains('tray.recovered') } 'tray recovery after actual Explorer restart' 30
    Open-RcHistory $app 'hotkey'; Close-RcHistory
    $results.Add(@{ app='Explorer restart'; result='PASS actual shell restart, tray recovery log, global hotkey and search focus' })
    $results.Add(@{ app='Snipping Tool, Paint, Windows Terminal, PowerShell console, Office, GPU/protected windows'; result='NOT TESTED by this harness; physical matrix remains open' })
    $results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'compatibility.json') -Encoding UTF8
    Write-Output '::notice title=RC real apps::PASS: Notepad Unicode/multiline/URL copy and paste; Edge and Chrome when installed CF_HTML/text restoration; Explorer file list restoration; Notepad privacy persisted over restart; actual Explorer restart tray recovery and hotkey'
    foreach ($result in $results) { Write-Output ('::notice title=RC application evidence::' + ($result | ConvertTo-Json -Compress -Depth 8)) }
    Invoke-RcTrayMenu $app 'Exit'
    if (-not $app.WaitForExit(15000)) { throw 'Tray Exit did not stop the app.' }
    $remove = Start-Process (Join-Path $install 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    if ($remove.ExitCode -ne 0) { throw 'Compatibility uninstall failed.' }
} catch {
    $results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $evidence 'compatibility-partial.json') -Encoding UTF8
    Write-Output ('::error title=RC real apps::' + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A') + " at line $($_.InvocationInfo.ScriptLineNumber), phase $script:phase")
    throw
} finally {
    if ($app -and -not $app.HasExited) { Stop-RcApp $exe $app }
    if ($notepad) { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
    if ($pauseSource) { $pauseSource.Close(); $pauseSource.Dispose() }
    if ($sourceWindow -ne [IntPtr]::Zero) { [void][ClipScopeRcNative]::PostMessage($sourceWindow,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }
    [System.Windows.Forms.Clipboard]::Clear()
    Remove-OwnedTestDirectory $profile
    Remove-OwnedTestDirectory $fixtures
}

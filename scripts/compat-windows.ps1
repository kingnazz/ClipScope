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
$app = $null; $sourceWindow = [IntPtr]::Zero; $notepad = $null; $pauseSource = $null; $consoleChild = $null
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
function Record-Capture([string] $Name, [string] $Needle, [string] $Version, [int] $PublisherId = 0) {
    Wait-Rc {
        $candidate = Get-RcProbe $data $Needle
        $candidate.payloadHits -ge 1 -and ($PublisherId -eq 0 -or $candidate.latest.sourceProcessId -eq $PublisherId)
    } "$Name persisted from expected publisher"
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
    Wait-Rc { [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',[IntPtr]::Zero) -ne [IntPtr]::Zero } 'installed native host'

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
    Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Restore-FromHistory $needle
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $text } 'Notepad exact Unicode restoration'
    Close-RcHistory
    Set-RcForeground $sourceWindow
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
            Wait-Rc { [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',[IntPtr]::Zero) -ne [IntPtr]::Zero } 'restart with exclusions'
        }
        $secret = 'rcexcluded' + [Guid]::NewGuid().ToString('N')
        $before = Get-RcProbe $data $secret
        # Paste into Notepad, clear clipboard, then make Notepad itself own Ctrl+C.
        Set-RcForeground $sourceWindow
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::SetText($secret) }
        [System.Windows.Forms.SendKeys]::SendWait('^a^v')
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }
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
            Wait-Rc { [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',[IntPtr]::Zero) -ne [IntPtr]::Zero } 'restart while paused'
        }
        $pausedNeedle='rcpaused'+[Guid]::NewGuid().ToString('N')
        $pauseSource.Activate(); Set-RcForeground $pauseSource.Handle
        Wait-Rc { [ClipScopeRcNative]::GetForegroundWindow() -eq $pauseSource.Handle } 'pause source foreground'
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::SetText($pausedNeedle) }
        Start-Sleep -Seconds 2
        $record=Get-RcProbe $data $pausedNeedle
        if ($record.payloadHits -ne 0 -or $record.ftsHits -ne 0) { throw 'Paused capture persisted content, including after restart.' }
    }
    Invoke-RcTrayMenu $app 'Resume clipboard capture'
    Wait-Rc { (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).CaptureEnabled -eq $true } 'tray resume persisted'
    $resumedNeedle='rcresumed'+[Guid]::NewGuid().ToString('N')
    $pauseSource.Activate(); Set-RcForeground $pauseSource.Handle
    Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::SetText($resumedNeedle) }
    Wait-Rc { (Get-RcProbe $data $resumedNeedle).payloadHits -eq 1 } 'capture resumes after paused restart'
    Invoke-RcTrayMenu $app 'Settings'
    Wait-Rc { $script:window=Get-RcWindow $app; $script:window -and (Get-RcPattern 'Shortcut key' ([System.Windows.Automation.ValuePattern]::Pattern)) } 'tray Settings opened'
    Invoke-RcAction 'Cancel'; Close-RcHistory
    $pauseSource.Close(); $pauseSource.Dispose(); $pauseSource=$null
    $results.Add(@{ app='Tray menu and pause'; result='PASS real popup Pause/Resume/Settings via notification callback; paused restart does not capture, resume captures' })

    # Real visible PowerShell console, using its supported Set-Clipboard command.
    # Handshake after foreground verification avoids attributing a startup copy
    # to whichever application happened to be active before the console opened.
    $consoleScript = Join-Path $fixtures.Path 'console-copy.ps1'
    @'
param([string] $Needle, [string] $ReadyPath, [string] $VersionPath)
$Host.UI.RawUI.WindowTitle = $Needle
$PSVersionTable.PSVersion.ToString() | Set-Content -LiteralPath $VersionPath -Encoding UTF8
$text = "$Needle Unicode 日本語 🧪`r`nsynthetic console output"
Write-Output $text
$deadline = [DateTime]::UtcNow.AddSeconds(45)
while (-not (Test-Path -LiteralPath $ReadyPath)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Console source handshake timed out.' }
    Start-Sleep -Milliseconds 50
}
Set-Clipboard -Value $text
'@ | Set-Content -LiteralPath $consoleScript -Encoding UTF8
    $consoleHosts = @(@{ name='PowerShell console (Set-Clipboard)'; path='powershell.exe'; terminal=$false })
    $terminal = Get-Command wt.exe -ErrorAction SilentlyContinue
    if ($terminal) { $consoleHosts += @{ name='Windows Terminal (PowerShell Set-Clipboard)'; path=$terminal.Source; terminal=$true } }
    else { $results.Add(@{ app='Windows Terminal'; result='NOT TESTED: wt.exe unavailable on this server image' }) }
    foreach ($consoleHost in $consoleHosts) {
    $needle = 'rcconsole' + [Guid]::NewGuid().ToString('N')
    $ready = Join-Path $fixtures.Path ($needle + '-ready')
    $consoleVersion = Join-Path $fixtures.Path ($needle + '-version.txt')
    $consoleArguments = @('-NoLogo','-NoProfile','-NoExit','-File',('"'+$consoleScript+'"'),'-Needle',$needle,'-ReadyPath',('"'+$ready+'"'),'-VersionPath',('"'+$consoleVersion+'"'))
    if ($consoleHost.terminal) { $consoleArguments = @('-w','new','--title',$needle,'powershell.exe') + $consoleArguments }
    $consoleChild = Start-Process $consoleHost.path -ArgumentList $consoleArguments -PassThru
    Wait-Rc { $script:sourceWindow = Find-Source $needle; $script:sourceWindow -ne [IntPtr]::Zero } ($consoleHost.name + ' window')
    Set-RcForeground $sourceWindow
    [IO.File]::WriteAllText($ready,'ready')
    $consoleText = "$needle Unicode 日本語 🧪`r`nsynthetic console output"
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $consoleText } 'PowerShell Set-Clipboard native Unicode'
    # Set-Clipboard is published by its actual clipboard owner, which can be
    # PowerShell's hidden helper rather than its different-process terminal host.
    $clipboardOwner = [ClipScopeRcNative]::GetAncestor([ClipScopeRcNative]::GetClipboardOwner(),2)
    [uint32]$publisherId = 0
    [void][ClipScopeRcNative]::GetWindowThreadProcessId($clipboardOwner,[ref]$publisherId)
    if ($publisherId -eq 0) { throw 'Set-Clipboard has no resolved native owner.' }
    [uint32]$hostProcessId = 0
    [void][ClipScopeRcNative]::GetWindowThreadProcessId($sourceWindow,[ref]$hostProcessId)
    $hostVersion = if ($consoleHost.terminal) { [Diagnostics.Process]::GetProcessById($hostProcessId).MainModule.FileVersionInfo.FileVersion } else { (Get-Content -LiteralPath $consoleVersion -Raw).Trim() }
    $record = Record-Capture $consoleHost.name $needle $hostVersion $publisherId
    if ($record.latest.sourceProcessId -ne $publisherId) { throw "Set-Clipboard attribution differs from native owner PID $publisherId; recorded $($record.latest.source):$($record.latest.sourceProcessId)." }
    Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Restore-FromHistory $needle
    Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -eq $consoleText } 'PowerShell exact text restoration'
    Close-RcHistory
    if ($consoleHost.terminal) {
        # Also exercise Terminal's own selection/copy, independently of the shell
        # command. This must be attributed to the visible Windows Terminal owner.
        Set-RcForeground $sourceWindow
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }
        Start-Sleep -Milliseconds 2100 # outside the intentional duplicate window
        [System.Windows.Forms.SendKeys]::SendWait('^+a^+c')
        Wait-Rc { [System.Windows.Forms.Clipboard]::GetText().Contains($needle) -and [System.Windows.Forms.Clipboard]::GetText().Contains('日本語') } 'Terminal native selection copy'
        $terminalText = Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::GetText() }
        $nativeOwner = [ClipScopeRcNative]::GetAncestor([ClipScopeRcNative]::GetClipboardOwner(),2)
        [uint32]$nativePublisherId = 0
        [void][ClipScopeRcNative]::GetWindowThreadProcessId($nativeOwner,[ref]$nativePublisherId)
        $nativeFormats = Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::GetDataObject().GetFormats($false) }
        Write-Output ('::notice title=RC Terminal native clipboard::' + (@{ ownerPid=$nativePublisherId; hostPid=$hostProcessId; formats=@($nativeFormats); textCharacters=$terminalText.Length } | ConvertTo-Json -Compress))
        $record = Record-Capture 'Windows Terminal native Ctrl+Shift+C' $needle $hostVersion $hostProcessId
        [uint32]$terminalId = 0
        [void][ClipScopeRcNative]::GetWindowThreadProcessId($sourceWindow,[ref]$terminalId)
        if ($record.latest.sourceProcessId -ne $terminalId -or $record.latest.source -ne 'WindowsTerminal') { throw "Terminal native copy attribution is $($record.latest.source):$($record.latest.sourceProcessId), expected WindowsTerminal:$terminalId." }
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Restore-FromHistory $needle
        Wait-Rc { [System.Windows.Forms.Clipboard]::GetText() -ceq $terminalText } 'Terminal native copy exact restoration'
        Close-RcHistory
    }
    [void][ClipScopeRcNative]::PostMessage($sourceWindow,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $consoleChild.WaitForExit(5000)) { $consoleChild.Kill(); $consoleChild.WaitForExit() }
    $consoleChild.Dispose(); $consoleChild=$null; $sourceWindow=[IntPtr]::Zero
    }

    $paint = Join-Path $env:WINDIR 'System32/mspaint.exe'
    if (Test-Path -LiteralPath $paint) {
        $needle = 'rcpaint' + [Guid]::NewGuid().ToString('N')
        $imageFile = Join-Path $fixtures.Path ($needle + '.png')
        $bitmap = New-Object Drawing.Bitmap 160,100
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.Clear([Drawing.Color]::FromArgb(255,17,90,160)); $bitmap.Save($imageFile,[Drawing.Imaging.ImageFormat]::Png) }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
        $paintChild = Start-Process $paint -ArgumentList ('"'+$imageFile+'"') -PassThru
        Wait-Rc { $script:sourceWindow=Find-Source $needle; $script:sourceWindow -ne [IntPtr]::Zero } 'Paint PNG fixture window'
        Copy-RealSource $sourceWindow
        Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsImage() } 'Paint native image copy'
        $record = Record-Capture 'Paint' $needle ([Diagnostics.FileVersionInfo]::GetVersionInfo($paint).FileVersion)
        if ($record.latest.kind -ne 'Image') { throw 'Paint image was not stored as an image.' }
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Restore-FromHistory $needle
        Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsImage() } 'Paint native image restoration'
        $restored = Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::GetImage() }
        try {
            if ($restored.Width -ne 160 -or $restored.Height -ne 100 -or $restored.GetPixel(80,50).ToArgb() -ne [Drawing.Color]::FromArgb(255,17,90,160).ToArgb()) { throw 'Paint roundtrip changed dimensions or sampled pixel.' }
        } finally { $restored.Dispose() }
        Close-RcHistory
        [void][ClipScopeRcNative]::PostMessage($sourceWindow,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
        if (-not $paintChild.HasExited) { [void]$paintChild.WaitForExit(5000) }
        $sourceWindow=[IntPtr]::Zero
    } else { $results.Add(@{ app='Paint'; result='NOT TESTED: executable unavailable on this server image' }) }

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
        $copied = Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::GetText() }
        $htmlCopied = Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::GetData('HTML Format') }
        $record = Record-Capture $browser.name $needle ([Diagnostics.FileVersionInfo]::GetVersionInfo($browser.path).FileVersion)
        if (-not $record.latest.hasHtml) { throw ($browser.name + ' HTML payload missing from storage.') }
        Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Restore-FromHistory $needle
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
    Set-RcForeground $sourceWindow
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Wait-Rc { [System.Windows.Forms.Clipboard]::ContainsFileDropList() } 'Explorer CF_HDROP copy'
    $record = Record-Capture 'File Explorer' 'rcfile' ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $env:WINDIR 'explorer.exe')).FileVersion)
    if ($record.latest.kind -ne 'Files' -or $record.latest.fileCount -ne 1) { throw 'Explorer file list was not stored.' }
    Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }; Restore-FromHistory 'rcfile'
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
    $results.Add(@{ app='Snipping Tool, Office, GPU/protected windows'; result='NOT TESTED by this harness; physical matrix remains open' })
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
    foreach ($result in ($results | Select-Object -Last 3)) { Write-Output ('::notice title=RC partial application evidence::' + ($result | ConvertTo-Json -Compress -Depth 8)) }
    throw
} finally {
    if ($app -and -not $app.HasExited) { Stop-RcApp $exe $app }
    if ($notepad) { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
    if ($pauseSource) { $pauseSource.Close(); $pauseSource.Dispose() }
    if ($consoleChild -and -not $consoleChild.HasExited) { $consoleChild.Kill(); $consoleChild.WaitForExit() }
    if ($sourceWindow -ne [IntPtr]::Zero) { [void][ClipScopeRcNative]::PostMessage($sourceWindow,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }
    Invoke-TestClipboard { [System.Windows.Forms.Clipboard]::Clear() }
    Remove-OwnedTestDirectory $profile
    Remove-OwnedTestDirectory $fixtures
}

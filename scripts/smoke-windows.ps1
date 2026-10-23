# Run with Windows PowerShell 5.1 in STA: powershell -Sta -File scripts/smoke-windows.ps1
# Uses an isolated CI desktop and known synthetic content. Never run against an
# existing user data directory: the script refuses to touch one.
param([string] $ExecutablePath = '', [switch] $RequireDesktop, [switch] $RetainProfile, [string] $OwnershipReceipt = '')
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) {
    if ($RequireDesktop) { throw 'An interactive desktop is required for installed acceptance.' }
    Write-Output '::notice title=ClipScope desktop smoke::SKIPPED: runner has no interactive desktop'
    exit 0
}
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
Assert-IsolatedDesktopRunner
if ($RetainProfile -and -not $OwnershipReceipt) { throw 'Retaining synthetic data requires an explicit ownership receipt path.' }
$dataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClipScope'
if (Test-Path -LiteralPath $dataDirectory) { throw 'Existing ClipScope profile is protected.' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipScopeSmokeNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
'@
# Polls until the condition holds. Clipboard reads can briefly fail with ExternalException
# while another process (including ClipScope itself) has the clipboard open; that is
# retried, never treated as success.
function Wait-Condition([scriptblock] $Condition, [string] $Description) {
    $script:phase = $Description
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        if ($script:app -and $script:app.HasExited) { throw "ClipScope exited during $Description (exit $($script:app.ExitCode))." }
        try { if (& $Condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { } catch [System.Runtime.InteropServices.ExternalException] { }
        catch [System.Management.Automation.MethodInvocationException] { if ($_.Exception.InnerException -isnot [System.Runtime.InteropServices.ExternalException]) { throw } }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
$script:app = $null
$source = $null
$profile = New-OwnedTestDirectory $dataDirectory
$passed = $false
try {
$source = New-Object System.Windows.Forms.Form
$source.Text = 'ClipScope CI Source'
$source.Width = 500; $source.Height = 200
$source.TopMost = $true
$destination = New-Object System.Windows.Forms.TextBox
$destination.Multiline = $true; $destination.Dock = [System.Windows.Forms.DockStyle]::Fill
$source.Controls.Add($destination)
$source.Show()
# Exercise the real persisted screenshot preference, never a production test hook.
'{"ScreenshotsEnabled":true}' | Set-Content -Path (Join-Path $dataDirectory 'settings.json') -Encoding UTF8
    $exe = if ($ExecutablePath) { $ExecutablePath } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ClipScope.App.exe' }
    $script:app = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    $script:window = $null
    Wait-Condition {
        $pidCondition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $nameCondition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, 'ClipScope')
        $visibleCondition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $false)
        $both = New-Object System.Windows.Automation.AndCondition -ArgumentList (,([System.Windows.Automation.Condition[]]@($pidCondition, $nameCondition, $visibleCondition)))
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $both)
        $null -ne $script:window
    } 'WinUI main window'
    function Find-Name([string] $Name) {
        return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    }
    function Invoke-Action([string] $Name) {
        Wait-Condition {
            $element = Find-Name $Name
            if ($null -eq $element -or -not $element.Current.IsEnabled) { return $false }
            try {
                $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                return $true
            } catch [System.Windows.Automation.ElementNotEnabledException] { return $false }
        } "action $Name"
    }
    function Find-Pattern([string] $Name, $Pattern) {
        $elements = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
        foreach ($element in $elements) {
            $provider = $null
            if ($element.TryGetCurrentPattern($Pattern, [ref]$provider)) { return $provider }
        }
        throw 'Required UI automation pattern is unavailable.'
    }
    # The owner is a Forms clipboard helper HWND; the real foreground title must
    # still be attributed to this visible source window.
    $source.Activate(); $source.BringToFront()
    [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    if ([ClipScopeSmokeNative]::GetForegroundWindow() -ne $source.Handle) {
        Write-Output '::notice title=ClipScope desktop smoke::SKIPPED: runner denies foreground activation; launch verified only'
        exit 0
    }
    $fixture = 'ClipScope smoke 192.168.1.15 cafe'
    [System.Windows.Forms.Clipboard]::SetText($fixture)
    Wait-Condition { $null -ne (Find-Name $fixture) } 'captured text in WinUI history'
    Wait-Condition {
        $elements = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            if ($element.Current.Name -like '*ClipScope CI Source*') { return $true }
        }
        return $false
    } 'source-window context in detail pane'
    $script:contextImage = $null
    Wait-Condition {
        $script:contextImage = Get-ChildItem (Join-Path $dataDirectory 'media') -Filter '*-context.png' -ErrorAction SilentlyContinue | Select-Object -First 1
        $null -ne $script:contextImage
    } 'optional source-window screenshot on disk'
    $image = [System.Drawing.Image]::FromFile($script:contextImage.FullName)
    try {
        if ($image.Width -lt 100 -or $image.Height -lt 80 -or $image.Width -gt 1000 -or $image.Height -gt 600) {
            throw 'Source-window screenshot has unexpected dimensions.'
        }
        $visibleFrame = New-Object ClipScopeSmokeNative+RECT
        if ([ClipScopeSmokeNative]::DwmGetWindowAttribute($source.Handle, 9, [ref]$visibleFrame, 16) -eq 0) {
            if ($image.Width -ne ($visibleFrame.Right - $visibleFrame.Left) -or $image.Height -ne ($visibleFrame.Bottom - $visibleFrame.Top)) {
                throw 'Source snapshot did not crop to the visible DWM frame.'
            }
        }
    } finally { $image.Dispose() }
    Wait-Condition {
        $images = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Image)))
        foreach ($preview in $images) {
            if ($preview.Current.Name -like 'Snapshot of *ClipScope CI Source*' -and $preview.Current.ItemStatus -eq 'Preview ready') { return $true }
        }
        return $false
    } 'source snapshot decoded in WinUI'
    [System.Windows.Forms.Clipboard]::Clear()
    $copy = Find-Name 'Copy'
    if ($null -eq $copy) { throw 'Copy action missing.' }
    $copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { [System.Windows.Forms.Clipboard]::GetText() -eq $fixture } 'historical clipboard restoration'
    $pin = Find-Name 'Pin'
    if ($null -eq $pin) { throw 'Pin action missing.' }
    $pin.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { $null -ne (Find-Name 'Unpin') } 'persisted pin state in detail pane'
    $search = Find-Name 'Search clipboard history'
    if ($null -eq $search) { throw 'Search field missing.' }
    $value = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('no-matching-clip-unique-query')
    Wait-Condition { $null -eq (Find-Name $fixture) } 'search excluding unmatched content'
    # A nonmatching pending query must not copy the previous selection.
    [System.Windows.Forms.Clipboard]::Clear()
    $search.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^{ENTER}')
    Start-Sleep -Milliseconds 400
    if ([System.Windows.Forms.Clipboard]::ContainsText()) { throw 'A stale search result was copied.' }
    # Copy a second clip, then issue commands without waiting for search debounce.
    $value.SetValue('')
    $source.Activate(); [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    [System.Windows.Forms.Clipboard]::SetText('ClipScope second item gamma')
    Wait-Condition { $null -ne (Find-Name 'ClipScope second item gamma') } 'second captured clip'
    [System.Windows.Forms.SendKeys]::SendWait('^+v')
    Wait-Condition { [ClipScopeSmokeNative]::GetForegroundWindow() -eq [IntPtr]$script:window.Current.NativeWindowHandle } 'activation for immediate commands'
    $value.SetValue('gamma'); $search.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^p')
    Wait-Condition { $null -ne (Find-Name 'Unpin') } 'pin after immediate query change'
    [System.Windows.Forms.SendKeys]::SendWait('^p')
    Wait-Condition { $null -ne (Find-Name 'Pin') } 'unpin second clip'
    $value.SetValue('192.168'); $search.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^{ENTER}')
    Wait-Condition { [System.Windows.Forms.Clipboard]::GetText() -eq $fixture } 'Ctrl+Enter after immediate query change'
    if ($destination.Text.Length -ne 0) { throw 'Ctrl+Enter pasted instead of copying.' }
    # Ctrl held in the source app across activation must still mean Copy, even
    # though the WinUI window never received that Ctrl key-down.
    [System.Windows.Forms.Clipboard]::Clear()
    $source.Activate(); [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    try {
        [ClipScopeSmokeNative]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
        [ClipScopeSmokeNative]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
        [ClipScopeSmokeNative]::keybd_event(0x56, 0, 0, [UIntPtr]::Zero)
        [ClipScopeSmokeNative]::keybd_event(0x56, 0, 2, [UIntPtr]::Zero)
        [ClipScopeSmokeNative]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero)
        Wait-Condition { [ClipScopeSmokeNative]::GetForegroundWindow() -eq [IntPtr]$script:window.Current.NativeWindowHandle } 'activation while Ctrl remains held'
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Wait-Condition { [System.Windows.Forms.Clipboard]::GetText() -eq $fixture } 'copy with Ctrl held across activation'
        if ($destination.Text.Length -ne 0) { throw 'Ctrl held across activation pasted instead of copying.' }
    } finally {
        [ClipScopeSmokeNative]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
        [ClipScopeSmokeNative]::keybd_event(0x10, 0, 2, [UIntPtr]::Zero)
    }
    Wait-Condition { $null -ne (Find-Name 'Unpin') } 'original clip still pinned'
    # Cancel must discard staged privacy edits. A failed Save must retain the
    # original shortcut and exact startup registry entry as well as the file.
    Invoke-Action 'Settings'
    Wait-Condition { $null -ne (Find-Name 'Settings sections') } 'settings open'
    (Find-Name 'Privacy').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (Find-Pattern 'Excluded apps, one executable path or process name per line' ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('ClipScopeNeverExcludeTest')
    Invoke-Action 'Cancel'
    if ([IO.File]::ReadAllText((Join-Path $dataDirectory 'settings.json')).Contains('ClipScopeNeverExcludeTest')) { throw 'Cancel persisted staged privacy edits.' }
    Invoke-Action 'Settings'
    Wait-Condition { $null -ne (Find-Name 'Shortcut key') } 'general settings'
    (Find-Pattern 'Shortcut key' ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('Y')
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { $previousStartup = if ($runKey) { $runKey.GetValue('ClipScope', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null } }
    finally { if ($runKey) { $runKey.Dispose() } }
    (Find-Pattern 'Start with Windows' ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    $beforeSave = [IO.File]::ReadAllText((Join-Path $dataDirectory 'settings.json'))
    $lockedSettings = [IO.File]::Open((Join-Path $dataDirectory 'settings.json'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Invoke-Action 'Save'
        Wait-Condition { $null -ne (Find-Name 'Could not save settings. Check the values, shortcut availability and storage permissions.') } 'failed settings save rollback'
    } finally { $lockedSettings.Dispose() }
    if ([IO.File]::ReadAllText((Join-Path $dataDirectory 'settings.json')) -cne $beforeSave) { throw 'Failed Save changed persisted settings.' }
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { $afterStartup = if ($runKey) { $runKey.GetValue('ClipScope', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null } }
    finally { if ($runKey) { $runKey.Dispose() } }
    if ($afterStartup -cne $previousStartup) { throw 'Failed Save changed startup registration.' }
    if (-not [ClipScopeSmokeNative]::RegisterHotKey([IntPtr]::Zero, 41, 0x4006, 0x59)) { throw 'Failed Save retained the reserved new hotkey.' }
    [void][ClipScopeSmokeNative]::UnregisterHotKey([IntPtr]::Zero, 41)
    # Retry succeeds, then change back to the default shortcut for the paste test.
    (Find-Pattern 'Start with Windows' ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Invoke-Action 'Save'
    Wait-Condition { $null -eq (Find-Name 'Settings sections') } 'successful settings retry'
    $source.Activate(); [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    [System.Windows.Forms.SendKeys]::SendWait('^+y')
    Wait-Condition { [ClipScopeSmokeNative]::GetForegroundWindow() -eq [IntPtr]$script:window.Current.NativeWindowHandle } 'changed global shortcut'
    Invoke-Action 'Settings'
    Wait-Condition { $null -ne (Find-Name 'Shortcut key') } 'settings after successful save'
    (Find-Pattern 'Shortcut key' ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('V')
    Invoke-Action 'Save'
    Wait-Condition { $null -eq (Find-Name 'Settings sections') } 'default shortcut saved'
    $value.SetValue('192.168')
    Wait-Condition { $null -ne (Find-Name $fixture) } 'FTS matching historical text'
    $source.Activate(); $destination.Focus()
    [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    [System.Windows.Forms.SendKeys]::SendWait('^+v')
    Wait-Condition { [ClipScopeSmokeNative]::GetForegroundWindow() -eq [IntPtr]$script:window.Current.NativeWindowHandle } 'global shortcut opening history'
    # Enter in the search box must resolve current results before paste.
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Wait-Condition { $destination.Text -eq $fixture } 'focus handoff and synthetic paste into the source application'
    [System.Windows.Forms.SendKeys]::SendWait('^+v')
    Wait-Condition { [ClipScopeSmokeNative]::GetForegroundWindow() -eq [IntPtr]$script:window.Current.NativeWindowHandle } 'history after paste'
    Wait-Condition { $null -ne (Find-Name 'Delete') } 'selected clip actions after paste'
    $delete = Find-Name 'Delete'
    $delete.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { $null -eq (Find-Name $fixture) } 'clip deletion from history'
    Wait-Condition { -not (Test-Path $script:contextImage.FullName) } 'deleted clip media cleanup'
    # Exercise the real clear-history confirmation, retaining a pinned clip.
    $value.SetValue('')
    $source.Activate(); [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    [System.Windows.Forms.Clipboard]::SetText('ClipScope clear pinned sentinel')
    Wait-Condition { $null -ne (Find-Name 'ClipScope clear pinned sentinel') } 'pinned-clear fixture capture'
    [System.Windows.Forms.SendKeys]::SendWait('^+v')
    Wait-Condition { $null -ne (Find-Name 'Pin') } 'clear fixture actions'
    Invoke-Action 'Pin'
    Wait-Condition { $null -ne (Find-Name 'Unpin') } 'clear fixture pinned'
    $source.Activate(); [void][ClipScopeSmokeNative]::SetForegroundWindow($source.Handle)
    [System.Windows.Forms.Clipboard]::SetText('ClipScope clear transient')
    Wait-Condition { $null -ne (Find-Name 'ClipScope clear transient') } 'unpinned-clear fixture capture'
    Invoke-Action 'Settings'
    Wait-Condition { $null -ne (Find-Name 'Settings sections') } 'clear settings open'
    (Find-Name 'Storage').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    # The page swaps in asynchronously, and the card header shares the button's name;
    # wait for the invokable element rather than the first element with that name.
    $script:clearAction = $null
    Wait-Condition {
        $elements = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, 'Clear unpinned history')))
        foreach ($element in $elements) {
            $provider = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$provider)) { $script:clearAction = $provider; return $true }
        }
        return $false
    } 'storage page clear action'
    $script:clearAction.Invoke()
    $script:confirmClear = $null
    Wait-Condition {
        # Popups may have a separate UIA root; inspect only our app's process.
        $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)))
        foreach ($root in $roots) {
            $script:confirmClear = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, 'Delete unpinned clips')))
            if ($script:confirmClear) { return $true }
        }
        return $false
    } 'clear confirmation'
    $script:confirmClear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { $null -ne (Find-Name 'Unpinned history cleared.') } 'clear history completion'
    Invoke-Action 'Cancel'
    Wait-Condition { $null -ne (Find-Name 'ClipScope clear pinned sentinel') } 'pinned clip preserved by clear'
    if ($null -ne (Find-Name 'ClipScope clear transient') -or $null -ne (Find-Name 'ClipScope second item gamma')) { throw 'Clear retained unpinned history.' }
    $passed = $true
    Write-Output '::notice title=ClipScope desktop smoke::PASS: launch, text capture, source-window context, screenshot PNG and decoded preview, clipboard restoration, pin, FTS search, global shortcut, focus/paste, deletion, media cleanup, immediate commands, settings Cancel, failed Save rollback shortcut replacement and clear preserving pinned clips'
} catch {
    Write-Output ("::error title=ClipScope desktop smoke::" + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A') + " (line $($_.InvocationInfo.ScriptLineNumber), phase $script:phase)")
    $diagnostic = Join-Path $env:LOCALAPPDATA 'ClipScope/logs/diagnostic.log'
    if (Test-Path $diagnostic) {
        foreach ($line in (Get-Content $diagnostic -Tail 8)) { Write-Output ("::notice title=ClipScope diagnostic::" + $line) }
    }
    throw
} finally {
    if ($script:app -and -not $script:app.HasExited) {
        $script:app.Kill()
        if (-not $script:app.WaitForExit(5000)) { throw 'Test process did not stop; profile left intact.' }
    }
    if ($source) { $source.Close(); $source.Dispose() }
    if ($script:app) { $script:app.Dispose() }
    [System.Windows.Forms.Clipboard]::Clear()
    if ($RetainProfile -and $passed) { $profile | ConvertTo-Json | Set-Content -LiteralPath $OwnershipReceipt -Encoding UTF8 }
    else { Remove-OwnedTestDirectory $profile }
}

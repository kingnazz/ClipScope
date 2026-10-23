# Run with Windows PowerShell 5.1 in STA: powershell -Sta -File scripts/smoke-windows.ps1
# Uses an isolated CI desktop and known synthetic content. Never run against an
# existing user data directory: the script refuses to touch one.
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) {
    Write-Output '::notice title=ClipScope desktop smoke::SKIPPED: runner has no interactive desktop'
    exit 0
}
$dataDirectory = Join-Path $env:LOCALAPPDATA 'ClipScope'
if (Test-Path $dataDirectory) {
    throw 'Desktop smoke requires a fresh isolated user profile; existing ClipScope data is protected.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipScopeSmokeNative {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
# Polls until the condition holds. Clipboard reads can briefly fail with ExternalException
# while another process (including ClipScope itself) has the clipboard open; that is
# retried, never treated as success.
function Wait-Condition([scriptblock] $Condition, [string] $Description) {
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
$source = New-Object System.Windows.Forms.Form
$source.Text = 'ClipScope CI Source'
$source.Width = 500; $source.Height = 200
$source.TopMost = $true
$destination = New-Object System.Windows.Forms.TextBox
$destination.Multiline = $true; $destination.Dock = [System.Windows.Forms.DockStyle]::Fill
$source.Controls.Add($destination)
$source.Show()
# Exercise the real persisted screenshot preference, never a production test hook.
[void](New-Item -Path $dataDirectory -ItemType Directory)
'{"ScreenshotsEnabled":true}' | Set-Content -Path (Join-Path $dataDirectory 'settings.json') -Encoding UTF8
try {
    $exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ClipScope.App.exe'
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
    } finally { $image.Dispose() }
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
    Write-Output '::notice title=ClipScope desktop smoke::PASS: launch, text capture, source-window context, screenshot PNG, clipboard restoration, pin, FTS search, global shortcut, focus/paste, deletion and media cleanup'
} catch {
    Write-Output ("::error title=ClipScope desktop smoke::" + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A'))
    $diagnostic = Join-Path $env:LOCALAPPDATA 'ClipScope/logs/diagnostic.log'
    if (Test-Path $diagnostic) {
        foreach ($line in (Get-Content $diagnostic -Tail 8)) { Write-Output ("::notice title=ClipScope diagnostic::" + $line) }
    }
    throw
} finally {
    if ($script:app -and -not $script:app.HasExited) { Stop-Process -Id $script:app.Id -Force }
    $source.Close(); $source.Dispose()
    [System.Windows.Forms.Clipboard]::Clear()
}

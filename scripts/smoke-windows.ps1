# Run with Windows PowerShell 5.1 in STA: powershell -Sta -File scripts/smoke-windows.ps1
# Uses an isolated CI desktop and known synthetic content. Never run against an
# existing user data directory: the script refuses to touch one.
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) {
    Write-Output '::notice title=ClipScope desktop smoke::SKIPPED: runner has no interactive desktop'
    exit 0
}
if (Test-Path (Join-Path $env:LOCALAPPDATA 'ClipScope')) {
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
function Wait-Condition([scriptblock] $Condition, [string] $Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        if ($script:app -and $script:app.HasExited) { throw "ClipScope exited during $Description (exit $($script:app.ExitCode))." }
        try { if (& $Condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
$script:app = $null
$source = New-Object System.Windows.Forms.Form
$source.Text = 'ClipScope CI Source'
$source.Width = 500; $source.Height = 200
$source.TopMost = $true
$source.Show()
try {
    $exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ClipScope.App.exe'
    $script:app = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    $script:window = $null
    Wait-Condition {
        $pidCondition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
        $nameCondition = New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, 'ClipScope')
        $both = New-Object System.Windows.Automation.AndCondition -ArgumentList (,([System.Windows.Automation.Condition[]]@($pidCondition, $nameCondition)))
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
    [System.Windows.Forms.Clipboard]::Clear()
    $copy = Find-Name 'Copy'
    if ($null -eq $copy) { throw 'Copy action missing.' }
    $copy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { [System.Windows.Forms.Clipboard]::GetText() -eq $fixture } 'historical clipboard restoration'
    $pin = Find-Name 'Pin'
    if ($null -eq $pin) { throw 'Pin action missing.' }
    $pin.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { $null -ne (Find-Name 'Unpin') } 'persisted pin state in detail pane'
    Write-Output '::notice title=ClipScope desktop smoke::PASS: launch, text capture, source-window context, clipboard restoration, pin'
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

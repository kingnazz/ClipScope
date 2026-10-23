# Visual review capture. Run with Windows PowerShell 5.1 in STA:
#   powershell -Sta -File scripts/capture-ui-windows.ps1 -OutputDirectory artifacts/ui
# Launches the built app in a fresh profile, creates synthetic clips (text, code, image,
# files, source snapshots) and saves window screenshots of the main states in both
# themes. It refuses to touch an existing ClipScope data directory. It is a review aid,
# not a functional test; scripts/smoke-windows.ps1 remains the functional gate.
param([string] $OutputDirectory = 'artifacts/ui')
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) {
    Write-Output '::notice title=ClipScope UI capture::SKIPPED: runner has no interactive desktop'
    exit 0
}
$dataDirectory = Join-Path $env:LOCALAPPDATA 'ClipScope'
if (Test-Path $dataDirectory) {
    throw 'UI capture requires a fresh isolated user profile; existing ClipScope data is protected.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipScopeUiNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
}
'@
$out = New-Item -ItemType Directory -Force -Path $OutputDirectory
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force -ErrorAction Stop } catch { Write-Output "Display resolution unchanged: $($_.Exception.Message)" }
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output "Screen: $($screen.Width)x$($screen.Height)"
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ClipScope.App.exe'

function Wait-Condition([scriptblock] $Condition, [string] $Description, [int] $Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        try { if (& $Condition) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { } catch [System.Runtime.InteropServices.ExternalException] { }
        catch [System.Management.Automation.MethodInvocationException] { if ($_.Exception.InnerException -isnot [System.Runtime.InteropServices.ExternalException]) { throw } }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
function Find-Name([string] $Name) {
    return $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
}
function Start-ClipScope([int] $Theme) {
    "{`"ScreenshotsEnabled`":true,`"Theme`":$Theme}" | Set-Content -Path (Join-Path $dataDirectory 'settings.json') -Encoding UTF8
    $script:app = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    Wait-Condition {
        $conditions = [System.Windows.Automation.Condition[]]@(
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)),
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, 'ClipScope')))
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.AndCondition -ArgumentList (,$conditions)))
        $null -ne $script:window
    } 'ClipScope window'
    $script:hwnd = [IntPtr]$script:window.Current.NativeWindowHandle
    Start-Sleep -Milliseconds 1500
}
function Stop-ClipScope {
    if ($script:app -and -not $script:app.HasExited) { Stop-Process -Id $script:app.Id -Force; $script:app.WaitForExit() }
}
function Resize-ClipScope([int] $Width, [int] $Height) {
    $x = [Math]::Max(0, [int](($screen.Width - $Width) / 2)); $y = [Math]::Max(0, [int](($screen.Height - $Height) / 2))
    [void][ClipScopeUiNative]::SetWindowPos($script:hwnd, [IntPtr]::Zero, $x, $y, $Width, $Height, 0x0004)
    Start-Sleep -Milliseconds 700
}
function Save-Window([string] $Name) {
    [void][ClipScopeUiNative]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 900
    $rect = New-Object ClipScopeUiNative+RECT
    if ([ClipScopeUiNative]::DwmGetWindowAttribute($script:hwnd, 9, [ref]$rect, 16) -ne 0) { [void][ClipScopeUiNative]::GetWindowRect($script:hwnd, [ref]$rect) }
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $out.FullName "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "Captured $Name ($width x $height)"
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Select-Row([string] $Prefix) {
    $list = Find-Name 'Clipboard history'
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    foreach ($item in $items) {
        if ($item.Current.Name -like "$Prefix*") {
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 800
            return
        }
    }
    throw "No history row starting with '$Prefix'."
}
function Set-Search([string] $Text) {
    (Find-Name 'Search clipboard history').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Milliseconds 900
}
function Open-SettingsPage([string] $Page) {
    if ($null -eq (Find-Name 'Settings sections')) {
        (Find-Name 'Settings').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-Condition { $null -ne (Find-Name 'Settings sections') } 'settings page'
    }
    $item = Find-Name $Page
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}
function Close-Settings {
    [void][ClipScopeUiNative]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Wait-Condition { $null -eq (Find-Name 'Settings sections') } 'settings to close'
}

# A realistic source window so snapshots have something to show.
$source = New-Object System.Windows.Forms.Form
$source.Width = 880; $source.Height = 560; $source.StartPosition = 'Manual'; $source.Location = New-Object System.Drawing.Point(40, 40)
$source.BackColor = [System.Drawing.Color]::White
$editor = New-Object System.Windows.Forms.TextBox
$editor.Multiline = $true; $editor.Dock = [System.Windows.Forms.DockStyle]::Fill
$editor.Font = New-Object System.Drawing.Font('Segoe UI', 12)
$editor.Text = "Licensing review`r`n`r`nMicrosoft 365 Business Premium includes Defender for Business, Intune and Entra ID P1.`r`nRenewal is due on November 30. Confirm seat counts with finance before then.`r`n`r`nOpen items:`r`n- Export active users`r`n- Compare with HR roster`r`n- Remove unused licenses"
$source.Controls.Add($editor)
$source.Show()
$filesDirectory = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP 'ClipScopeUiCapture')
$files = @('Q3 board report.pdf', 'Budget FY26.xlsx', 'Meeting notes.txt') | ForEach-Object {
    $path = Join-Path $filesDirectory.FullName $_; Set-Content -Path $path -Value 'ClipScope UI capture'; $path }

# Copies from the source window. It must really be foreground, as in normal use,
# so ClipScope attributes the window title and can take a source snapshot.
function Copy-From([string] $Title, [scriptblock] $SetClipboard) {
    $source.Text = $Title
    $source.WindowState = 'Normal'
    # Windows only lets the process that sent the last input change the foreground
    # window; a harmless F24 press makes this script that process.
    [ClipScopeUiNative]::keybd_event(0x87, 0, 0, [UIntPtr]::Zero); [ClipScopeUiNative]::keybd_event(0x87, 0, 2, [UIntPtr]::Zero)
    $source.Activate(); $source.BringToFront()
    [void][ClipScopeUiNative]::SetForegroundWindow($source.Handle)
    Start-Sleep -Milliseconds 400
    if ([ClipScopeUiNative]::GetForegroundWindow() -ne $source.Handle) { Write-Output "::warning title=ClipScope UI capture::Source window was not foreground for '$Title'" }
    & $SetClipboard
    Start-Sleep -Milliseconds 2200
}

[void](New-Item -Path $dataDirectory -ItemType Directory)
try {
    Start-ClipScope 2
    Resize-ClipScope 1360 860
    Save-Window '01-dark-empty'
    [void][ClipScopeUiNative]::ShowWindow($script:hwnd, 6)
    Start-Sleep -Milliseconds 500

    Copy-From 'Licensing review - Notepad' { [System.Windows.Forms.Clipboard]::SetText("Release checklist`r`n1. Freeze the branch`r`n2. Run the Windows smoke test`r`n3. Publish notes") }
    Copy-From 'Q3 planning - Whiteboard' {
        $bitmap = New-Object System.Drawing.Bitmap -ArgumentList 960, 540
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::FromArgb(255, 24, 39, 64))
        $brush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 96, 165, 250))
        foreach ($i in 0..6) { $g.FillRectangle($brush, 90 + $i * 115, 470 - (60 + $i * 45), 70, 60 + $i * 45) }
        $g.DrawString('Q3 revenue by month', (New-Object System.Drawing.Font('Segoe UI', 28)), [System.Drawing.Brushes]::White, 80, 40)
        $g.Dispose()
        [System.Windows.Forms.Clipboard]::SetImage($bitmap); $bitmap.Dispose()
    }
    Copy-From 'Shared documents - File Explorer' {
        $list = New-Object System.Collections.Specialized.StringCollection; foreach ($file in $files) { [void]$list.Add($file) }
        [System.Windows.Forms.Clipboard]::SetFileDropList($list)
    }
    Copy-From 'Administrator: Windows PowerShell' { [System.Windows.Forms.Clipboard]::SetText('Get-MgUser -All | Where-Object { $_.AccountEnabled } | Select-Object DisplayName, UserPrincipalName') }
    Copy-From 'Microsoft 365 admin center - Licenses' { [System.Windows.Forms.Clipboard]::SetText('Microsoft 365 Business Premium includes Defender for Business, Intune and Entra ID P1. Renewal is due on November 30; confirm seat counts with finance first.') }
    # Open ClipScope the way a user does, from the source app with the global shortcut.
    [System.Windows.Forms.SendKeys]::SendWait('^+v')
    Wait-Condition { [ClipScopeUiNative]::GetForegroundWindow() -eq $script:hwnd } 'ClipScope opened by the global shortcut'
    $source.WindowState = 'Minimized'
    Start-Sleep -Milliseconds 800
    Save-Window '02-dark-history-text-snapshot'
    Select-Row 'Image from'
    Save-Window '03-dark-image'
    Select-Row 'Files from'
    Save-Window '04-dark-files'
    Set-Search 'Get-MgUser'
    Save-Window '05-dark-search-code'
    Set-Search 'sonicwall'
    Save-Window '06-dark-no-results'
    Set-Search ''
    Open-SettingsPage 'General'
    Save-Window '07-dark-settings-general'
    Open-SettingsPage 'Privacy'
    Save-Window '08-dark-settings-privacy'
    Open-SettingsPage 'Storage'
    Save-Window '09-dark-settings-storage'
    Close-Settings
    Resize-ClipScope 920 680
    Save-Window '10-dark-narrow'
    Resize-ClipScope ([Math]::Min(1720, $screen.Width - 40)) ([Math]::Min(980, $screen.Height - 60))
    Save-Window '11-dark-wide'
    Stop-ClipScope

    Start-ClipScope 1
    Resize-ClipScope 1360 860
    Save-Window '12-light-history'
    (Find-Name 'Pin').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Condition { $null -ne (Find-Name 'Unpin') } 'pinned state'
    Save-Window '12b-light-pinned'
    Select-Row 'Image from'
    Save-Window '13-light-image'
    Select-Row 'Files from'
    Save-Window '14-light-files'
    Open-SettingsPage 'Capture'
    Save-Window '15-light-settings-capture'
    Open-SettingsPage 'Appearance'
    Save-Window '16-light-settings-appearance'
    Close-Settings
    Resize-ClipScope 920 680
    Save-Window '17-light-narrow'
    Write-Output '::notice title=ClipScope UI capture::Captured main window, image, files, search, no results, settings, narrow and wide states in dark and light themes'
} catch {
    Write-Output ("::warning title=ClipScope UI capture::" + $_.Exception.Message.Replace('%','%25').Replace("`r",'%0D').Replace("`n",'%0A'))
    try { Save-Window 'zz-failure-state' } catch { }
    $diagnostic = Join-Path $dataDirectory 'logs/diagnostic.log'
    if (Test-Path $diagnostic) { foreach ($line in (Get-Content $diagnostic -Tail 12)) { Write-Output ("::notice title=ClipScope diagnostic::" + $line) } }
    throw
} finally {
    Stop-ClipScope
    $source.Close(); $source.Dispose()
    [System.Windows.Forms.Clipboard]::Clear()
}

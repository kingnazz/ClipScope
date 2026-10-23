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
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
Assert-IsolatedDesktopRunner
$dataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClipScope'
if (Test-Path -LiteralPath $dataDirectory) { throw 'Existing ClipScope profile is protected.' }
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
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
}
'@
$out = New-Item -ItemType Directory -Force -Path $OutputDirectory
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force -ErrorAction Stop } catch { Write-Output 'Display resolution unchanged.' }
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output "Screen: $($screen.Width)x$($screen.Height)"
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ClipScope.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ClipScope.App.exe'

function Wait-Condition([scriptblock] $Condition, [string] $Description, [int] $Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        if ($script:app -and $script:app.HasExited) { throw "ClipScope exited during $Description." }
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
    if ($script:app -and -not $script:app.HasExited) {
        $script:app.Kill()
        if (-not $script:app.WaitForExit(5000)) { throw 'Test process did not stop; profile left intact.' }
    }
    if ($script:app) { $script:app.Dispose(); $script:app = $null }
}
function Resize-ClipScope([int] $Width, [int] $Height) {
    $x = [Math]::Max(0, [int](($screen.Width - $Width) / 2)); $y = [Math]::Max(0, [int](($screen.Height - $Height) / 2))
    [void][ClipScopeUiNative]::SetWindowPos($script:hwnd, [IntPtr]::Zero, $x, $y, $Width, $Height, 0x0004)
    Start-Sleep -Milliseconds 700
}
function Save-Window([string] $Name) {
    [void][ClipScopeUiNative]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 900
    if (-not $script:app -or $script:app.HasExited -or [ClipScopeUiNative]::GetForegroundWindow() -ne $script:hwnd) {
        throw 'Cannot safely capture the test window.'
    }
    $rect = New-Object ClipScopeUiNative+RECT
    if (-not [ClipScopeUiNative]::GetWindowRect($script:hwnd, [ref]$rect)) { throw 'No test window bounds.' }
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0 -or $width -gt 8192 -or $height -gt 8192 -or [long]$width * $height -gt 16000000) { throw 'Unsafe capture size.' }
    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $width, $height
    $graphics = $null; $cropped = $null
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        try { if (-not [ClipScopeUiNative]::PrintWindow($script:hwnd, $dc, 2)) { throw 'Test window rendering failed.' } }
        finally { $graphics.ReleaseHdc($dc) }
        $visible = New-Object ClipScopeUiNative+RECT
        $capture = $bitmap
        if ([ClipScopeUiNative]::DwmGetWindowAttribute($script:hwnd, 9, [ref]$visible, 16) -eq 0 -and
            $visible.Left -ge $rect.Left -and $visible.Top -ge $rect.Top -and $visible.Right -le $rect.Right -and $visible.Bottom -le $rect.Bottom -and
            $visible.Right -gt $visible.Left -and $visible.Bottom -gt $visible.Top) {
            $crop = New-Object System.Drawing.Rectangle -ArgumentList @(($visible.Left - $rect.Left), ($visible.Top - $rect.Top), ($visible.Right - $visible.Left), ($visible.Bottom - $visible.Top))
            $cropped = $bitmap.Clone($crop, $bitmap.PixelFormat); $capture = $cropped
        }
        # Reject a flat/black PrintWindow result instead of publishing misleading
        # review evidence. Never fall back to photographing the desktop.
        $colors = New-Object 'System.Collections.Generic.HashSet[int]'
        $stepX = [Math]::Max(1, [int]($capture.Width / 64)); $stepY = [Math]::Max(1, [int]($capture.Height / 64))
        for ($y = 0; $y -lt $capture.Height; $y += $stepY) {
            for ($x = 0; $x -lt $capture.Width; $x += $stepX) { [void]$colors.Add($capture.GetPixel($x, $y).ToArgb()) }
        }
        if ($colors.Count -lt 8) { throw 'Test window rendering was blank; no desktop fallback is permitted.' }
        $capture.Save((Join-Path $out.FullName "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "Captured $Name ($($capture.Width) x $($capture.Height))"
    } finally { if ($cropped) { $cropped.Dispose() }; if ($graphics) { $graphics.Dispose() }; $bitmap.Dispose() }
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
$source = $null; $editorFont = $null; $filesOwner = $null
$profile = New-OwnedTestDirectory $dataDirectory
try {
$source = New-Object System.Windows.Forms.Form
$source.Width = 880; $source.Height = 560; $source.StartPosition = 'Manual'; $source.Location = New-Object System.Drawing.Point(40, 40)
$source.BackColor = [System.Drawing.Color]::White
$editor = New-Object System.Windows.Forms.TextBox
$editor.Multiline = $true; $editor.Dock = [System.Windows.Forms.DockStyle]::Fill
$editorFont = New-Object System.Drawing.Font('Segoe UI', 12)
$editor.Font = $editorFont
$editor.Text = "Licensing review`r`n`r`nMicrosoft 365 Business Premium includes Defender for Business, Intune and Entra ID P1.`r`nRenewal is due on November 30. Confirm seat counts with finance before then.`r`n`r`nOpen items:`r`n- Export active users`r`n- Compare with HR roster`r`n- Remove unused licenses"
$source.Controls.Add($editor)
$source.Show()
$filesOwner = New-OwnedTestDirectory (Join-Path ([IO.Path]::GetTempPath()) ('ClipScopeUiCapture-' + [Guid]::NewGuid().ToString('N')))
$filesDirectory = Get-Item -LiteralPath $filesOwner.Path
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
    if ([ClipScopeUiNative]::GetForegroundWindow() -ne $source.Handle) { throw 'Runner denied source foreground activation.' }
    & $SetClipboard
    Start-Sleep -Milliseconds 2200
}

    [System.Windows.Forms.Clipboard]::Clear()
    Start-ClipScope 2
    Resize-ClipScope 1360 860
    Save-Window '01-dark-empty'
    [void][ClipScopeUiNative]::ShowWindow($script:hwnd, 6)
    Start-Sleep -Milliseconds 500

    Copy-From 'Licensing review - Notepad' { [System.Windows.Forms.Clipboard]::SetText("Release checklist`r`n1. Freeze the branch`r`n2. Run the Windows smoke test`r`n3. Publish notes") }
    Copy-From 'Q3 planning - Whiteboard' {
        $bitmap = New-Object System.Drawing.Bitmap -ArgumentList 960, 540
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $brush = $null; $font = $null
        try {
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::FromArgb(255, 24, 39, 64))
        $brush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 96, 165, 250))
        foreach ($i in 0..6) { $g.FillRectangle($brush, 90 + $i * 115, 470 - (60 + $i * 45), 70, 60 + $i * 45) }
        $font = New-Object System.Drawing.Font('Segoe UI', 28)
        $g.DrawString('Q3 revenue by month', $font, [System.Drawing.Brushes]::White, 80, 40)
        [System.Windows.Forms.Clipboard]::SetImage($bitmap)
        } finally { if ($font) { $font.Dispose() }; if ($brush) { $brush.Dispose() }; $g.Dispose(); $bitmap.Dispose() }
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
    Write-Output '::warning title=ClipScope UI capture::Capture failed; no clipboard content is logged.'
    throw 'UI review capture failed. Functional smoke remains the release gate.'
} finally {
    $stopped = $false
    try { Stop-ClipScope; $stopped = $true } finally {
    if ($source) { $source.Close(); $source.Dispose() }
    if ($editorFont) { $editorFont.Dispose() }
    [System.Windows.Forms.Clipboard]::Clear()
    Remove-OwnedTestDirectory $filesOwner
    if ($stopped) { Remove-OwnedTestDirectory $profile }
    }
}

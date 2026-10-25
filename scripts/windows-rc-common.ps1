# UI automation helpers for isolated synthetic RC tests, not product code.
. (Join-Path $PSScriptRoot 'windows-test-profile.ps1')
Assert-IsolatedDesktopRunner
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipScopeRcNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    // PowerShell coerces $null to an empty string for string parameters. An
    // IntPtr caption explicitly passes NULL, matching every caption in the class.
    [DllImport("user32.dll", EntryPoint="FindWindowW", ExactSpelling=true, CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, IntPtr title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint kind);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("psapi.dll", SetLastError=true)] public static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters info, uint size);
    [StructLayout(LayoutKind.Sequential)] public struct MemoryCounters {
        public uint Size, PageFaultCount; public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }
}
'@
function Wait-Rc([scriptblock] $Condition, [string] $Description, [int] $Seconds = 20) {
    $script:phase = $Description
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        [System.Windows.Forms.Application]::DoEvents()
        try { if (& $Condition) { return } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out: $Description" }
        Start-Sleep -Milliseconds 50
    } while ($true)
}
function Set-RcForeground([IntPtr] $Handle) {
    if ([ClipScopeRcNative]::GetForegroundWindow() -eq $Handle) { return }
    [void][ClipScopeRcNative]::SetForegroundWindow($Handle)
    if ([ClipScopeRcNative]::GetForegroundWindow() -eq $Handle) { return }
    # A real click supplies the input permission that sequential hosted test
    # processes may lack. Only explicitly selected synthetic source windows are
    # passed here. Restore the source's original topmost state immediately.
    $rect = New-Object ClipScopeRcNative+Rect
    if (-not [ClipScopeRcNative]::GetWindowRect($Handle,[ref]$rect)) { throw 'Source window bounds are unavailable.' }
    $wasTopmost = ([ClipScopeRcNative]::GetWindowLong($Handle,-20) -band 8) -ne 0
    [void][ClipScopeRcNative]::SetWindowPos($Handle,[IntPtr](-1),0,0,0,0,0x13)
    try {
        [void][ClipScopeRcNative]::SetCursorPos($rect.Left+60,$rect.Top+12)
        [ClipScopeRcNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    } finally {
        if (-not $wasTopmost) { [void][ClipScopeRcNative]::SetWindowPos($Handle,[IntPtr](-2),0,0,0,0,0x13) }
    }
    Wait-Rc { [ClipScopeRcNative]::GetForegroundWindow() -eq $Handle } 'source window activated by input'
}
function Get-RcWindow($Process) {
    $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)))
    foreach ($element in $roots) {
        if ($element.Current.Name -eq 'ClipScope' -and -not $element.Current.IsOffscreen) { return $element }
    }
    return $null
}
function Get-RcPattern([string] $Name, $Pattern) {
    if (-not $script:window) { return $null }
    $elements = $script:window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    foreach ($element in $elements) {
        if ($Pattern -eq [System.Windows.Automation.InvokePattern]::Pattern -and -not $element.Current.IsEnabled) { continue }
        $provider = $null
        if ($element.TryGetCurrentPattern($Pattern, [ref]$provider)) { return $provider }
    }
    return $null
}
function Invoke-RcAction([string] $Name) {
    $script:provider = $null
    Wait-Rc { $script:provider = Get-RcPattern $Name ([System.Windows.Automation.InvokePattern]::Pattern); $null -ne $script:provider } $Name
    $script:provider.Invoke()
    if ($Name -eq 'Save' -or $Name -eq 'Cancel') {
        Wait-Rc {
            $sections = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty,'Settings sections')))
            $null -eq $sections
        } 'Settings transaction finished'
    }
}
function Open-RcHistory($Process, [string] $Method = 'hotkey') {
    if ($Method -eq 'hotkey') {
        [ClipScopeRcNative]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x10,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x56,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x56,0,2,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x10,0,2,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
    } else {
        [void][ClipScopeRcNative]::AllowSetForegroundWindow([uint32]$Process.Id)
        $hostWindow = [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1', [IntPtr]::Zero)
        if ($hostWindow -eq [IntPtr]::Zero) { throw 'Native listener is missing.' }
        [void][ClipScopeRcNative]::PostMessage($hostWindow, 0x8003, [IntPtr]::Zero, [IntPtr]0x203)
    }
    Wait-Rc {
        $script:window = Get-RcWindow $Process
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $script:window -and $focused -and $focused.Current.Name -eq 'Search clipboard history'
    } "$Method visible and search focused"
}
function Close-RcHistory {
    $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}
function Invoke-RcTrayMenu($Process, [string] $Action) {
    # Drive the real Win32 popup through the notification callback. Physical
    # hit-testing of the shell icon remains a separate Windows 11 acceptance item.
    $hostWindow = [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1',[IntPtr]::Zero)
    [void][ClipScopeRcNative]::AllowSetForegroundWindow([uint32]$Process.Id)
    [void][ClipScopeRcNative]::PostMessage($hostWindow,0x8003,[IntPtr]::Zero,[IntPtr]0x205)
    $script:trayProvider = $null
    Wait-Rc {
        $popup = [ClipScopeRcNative]::FindWindow('#32768',[IntPtr]::Zero)
        if ($popup -eq [IntPtr]::Zero) { return $false }
        $menu = [System.Windows.Automation.AutomationElement]::FromHandle($popup)
        if ($menu.Current.ProcessId -ne $Process.Id) { return $false }
        $item = $menu.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::NameProperty,$Action)))
        if (-not $item) { return $false }
        $provider = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$provider)) { $script:trayProvider=$provider; return $true }
        return $false
    } "tray menu $Action"
    $script:trayProvider.Invoke()
}
function Get-RcProbe([string] $Profile, [string] $Needle) {
    $json = & dotnet (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/ClipScope.Diagnostics/bin/Release/net10.0/ClipScope.Diagnostics.dll') probe $Profile $Needle
    if ($LASTEXITCODE -ne 0) { throw 'Database probe failed.' }
    return ($json | ConvertFrom-Json)
}
function Stop-RcApp([string] $Executable, $Process) {
    if ($Process -and -not $Process.HasExited) {
        $request = Start-Process -FilePath $Executable -ArgumentList '--shutdown' -Wait -PassThru
        if ($request.ExitCode -ne 0 -or -not $Process.WaitForExit(15000)) { throw 'Graceful app shutdown failed.' }
        $request.Dispose()
    }
}

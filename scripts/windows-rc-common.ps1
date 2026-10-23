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
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
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
        $provider = $null
        if ($element.TryGetCurrentPattern($Pattern, [ref]$provider)) { return $provider }
    }
    return $null
}
function Invoke-RcAction([string] $Name) {
    $script:provider = $null
    Wait-Rc { $script:provider = Get-RcPattern $Name ([System.Windows.Automation.InvokePattern]::Pattern); $null -ne $script:provider } $Name
    $script:provider.Invoke()
}
function Open-RcHistory($Process, [string] $Method = 'tray') {
    if ($Method -eq 'hotkey') {
        [ClipScopeRcNative]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x10,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x56,0,0,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x56,0,2,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x10,0,2,[UIntPtr]::Zero)
        [ClipScopeRcNative]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
    } else {
        $hostWindow = [ClipScopeRcNative]::FindWindow('ClipScope.NativeHost.v1', $null)
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

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClipScope.Core;

namespace ClipScope.Windows;

public sealed class WindowContext
{
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    public SourceContext Foreground() => Inspect(Native.GetForegroundWindow());
    public SourceContext ClipboardSource(SourceContext foreground)
    {
        var owner = Native.GetClipboardOwner();
        if (owner == 0) return new SourceContext();
        var source = Inspect(owner);
        return ContextPolicy.ResolveClipboardSource(source, foreground);
    }
    public SourceContext Inspect(nint window)
    {
        if (window == 0) return new();
        var root = Native.GetAncestor(window, 2);
        if (root != 0) window = root;
        Native.GetWindowThreadProcessId(window, out var pid);
        var title = new StringBuilder(1025); Native.GetWindowText(window, title, title.Capacity);
        string? path = null; string processName = "";
        var process = Native.OpenProcess(0x1000, false, pid);
        if (process != 0)
        try
        {
            var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
            if (Native.QueryFullProcessImageName(process, 0, buffer, ref length)) path = buffer.ToString();
        }
        finally { Native.CloseHandle(process); }
        if (path is not null) processName = Path.GetFileNameWithoutExtension(path);
        else
        try { using var p = Process.GetProcessById((int)pid); processName = p.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var appName = processName;
        if (path is not null)
        lock (_gate)
        {
            if (!_appNames.TryGetValue(path, out appName))
            {
                try { appName = FileVersionInfo.GetVersionInfo(path).FileDescription ?? processName; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { appName = processName; }
                if (_appNames.Count >= 128) _appNames.Clear();
                _appNames[path] = appName;
            }
        }
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
        var monitorName = Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor) ? monitor.Device : null;
        return new() { AppName = appName, ProcessName = processName, ProcessId = (int)pid, ExecutablePath = path,
            WindowHandle = window, WindowTitle = title.ToString(), MonitorName = monitorName };
    }
    public static nint ForegroundHandle => Native.GetForegroundWindow();
    public static bool IsTarget(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)
            || Native.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == Environment.ProcessId) return false;
        var cls = new StringBuilder(256); Native.GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }
    public static bool Focus(nint hwnd)
    {
        if (!IsTarget(hwnd)) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9);
        return Native.SetForegroundWindow(hwnd);
    }
    public static bool ShowApp(nint hwnd)
    { Native.ShowWindow(hwnd, 9); return Native.SetForegroundWindow(hwnd); }
}

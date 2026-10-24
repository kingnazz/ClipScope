using System.Runtime.InteropServices;
using System.Text;

namespace ClipScope.Windows;

internal static class Native
{
    internal const uint WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312, WM_APP = 0x8000;
    internal const uint CF_TEXT = 1, CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIBV5 = 17;
    internal delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint style; public WindowProc proc; public int classExtra, windowExtra;
        public nint instance, icon, cursor, background;
        public string? menuName; public string className;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public Point point; public uint reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X,Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Value; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] internal static extern nint DefWindowProc(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message msg);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message msg);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetClipboardData(uint format, nint value);
    [DllImport("user32.dll")] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClipboardFormatName(uint format, StringBuilder name, int size);
    [DllImport("kernel32.dll")] internal static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] internal static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint handle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint size);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flag);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder title, int size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder value, int size);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string cls, string? title);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] internal static extern nint LoadIcon(nint instance, nint resource);
    [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string label);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
}

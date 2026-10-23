using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClipScope.Core;
using ClipScope.Infrastructure;

namespace ClipScope.Windows;

public sealed class NativeHost : IDisposable
{
    public const string WindowClassName = "ClipScope.NativeHost.v1";
    private const uint WorkMessage = Native.WM_APP + 1, RetryMessage = Native.WM_APP + 2, TrayMessage = Native.WM_APP + 3;
    private readonly Func<AppSettings> _settings;
    private readonly WindowContext _context;
    private readonly IDiagnosticLog _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Native.WindowProc _procedure;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private ClipboardReader? _reader;
    private nint _window;
    private uint _sequence, _processed, _taskbarCreated;
    private SourceContext _source = new();
    private DateTimeOffset _capturedAt;
    private int _retry, _hotkeyId, _reservedHotkeyId;
    private Native.NotifyIconData _tray;
    private bool _disposed;
    public event Action<CapturedClip>? Captured;
    public event Action<nint>? OpenRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? CaptureEnabledRequested;
    public event Action? ExitRequested;
    public bool TrayAvailable { get; private set; }
    public Task Ready => _ready.Task;
    public static void ActivateExisting() => Native.PostMessage(Native.FindWindow(WindowClassName, null), Native.WM_APP + 4, 0, 0);
    public NativeHost(Func<AppSettings> settings, WindowContext context, IDiagnosticLog log)
    {
        _settings = settings; _context = context; _log = log;
        _procedure = Procedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipScope clipboard STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    private void Run()
    {
        try
        {
            var cls = new Native.WindowClass { className = WindowClassName, proc = _procedure, instance = Native.GetModuleHandle(null) };
            if (Native.RegisterClass(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = Native.CreateWindowEx(0, WindowClassName, "ClipScope", 0, 0, 0, 0, 0, 0, 0, cls.instance, 0);
            if (_window == 0 || !Native.AddClipboardFormatListener(_window)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _reader = new();
            _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            _tray = new() { Size = (uint)Marshal.SizeOf<Native.NotifyIconData>(), Window = _window, Id = 1,
                Flags = 1 | 2 | 4, Callback = TrayMessage, Icon = Native.LoadIcon(0, (nint)32512),
                Tip = "ClipScope · clipboard history", Info = "", InfoTitle = "" };
            TrayAvailable = Native.Shell_NotifyIcon(0, ref _tray);
            if (!TrayAvailable) _log.Write("tray.add_failed");
            _ready.SetResult();
            int outcome;
            while ((outcome = Native.GetMessage(out var message, 0, 0, 0)) > 0)
            { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
            if (outcome < 0) _log.Write("native.message_pump_failed");
        }
        catch (Exception e) { _log.Write("native.start_failed", e); _ready.TrySetException(e); }
        finally
        {
            if (_window != 0)
            {
                Native.RemoveClipboardFormatListener(_window);
                Native.UnregisterHotKey(_window, _hotkeyId);
                if (_reservedHotkeyId != 0) Native.UnregisterHotKey(_window, _reservedHotkeyId);
                Native.Shell_NotifyIcon(2, ref _tray);
                Native.DestroyWindow(_window);
            }
        }
    }
    private nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == _taskbarCreated && _taskbarCreated != 0) TrayAvailable = Native.Shell_NotifyIcon(0, ref _tray);
            if (message == WorkMessage) while (_commands.TryDequeue(out var command)) command();
            else if (message == Native.WM_CLIPBOARDUPDATE) OnClipboardUpdate();
            else if (message == RetryMessage && (uint)wParam == _sequence) TryCapture();
            else if (message == Native.WM_APP + 4) OpenRequested?.Invoke(Native.GetForegroundWindow());
            else if (message == Native.WM_HOTKEY) OpenRequested?.Invoke(Native.GetForegroundWindow());
            else if (message == TrayMessage)
            {
                if (lParam == 0x0203) OpenRequested?.Invoke(Native.GetForegroundWindow()); // double click
                else if (lParam == 0x0205) ShowTrayMenu(); // right button up
            }
        }
        catch (Exception e) { _log.Write("native.callback_failed", e); }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }
    private void OnClipboardUpdate()
    {
        var sequence = Native.GetClipboardSequenceNumber();
        if (sequence == _processed || sequence == _sequence) return;
        _sequence = sequence; _retry = 0;
        _capturedAt = DateTimeOffset.Now;
        var settings = _settings();
        if (!settings.CaptureEnabled) return;
        var foreground = _context.Foreground();
        _source = _context.ClipboardSource(foreground);
        if (PrivacyPolicy.IsExcluded(foreground, settings) || PrivacyPolicy.IsExcluded(_source, settings)
            || _source.ProcessId == Environment.ProcessId) return;
        TryCapture();
    }
    private void TryCapture()
    {
        if (_stop.IsCancellationRequested || _reader is null || Native.GetClipboardSequenceNumber() != _sequence) return;
        var settings = _settings();
        if (!settings.CaptureEnabled || PrivacyPolicy.IsExcluded(_source, settings)
            || PrivacyPolicy.IsExcluded(_context.Foreground(), settings)) return;
        if (!Native.OpenClipboard(_window))
        {
            if (_retry >= 5) { _log.Write("clipboard.contention_exhausted"); return; }
            var delay = new[] { 10, 25, 50, 100, 200 }[_retry++];
            var expected = _sequence;
            _ = RetryAfterAsync(delay, expected);
            return;
        }
        try
        {
            if (_reader.IsPrivate() || _reader.IsOwnRestore()) { _processed = _sequence; return; }
            var payload = _reader.Read(settings);
            if (Native.GetClipboardSequenceNumber() != _sequence) { _log.Write("clipboard.changed_during_read"); return; }
            _processed = _sequence;
            if (payload.HasContent) Captured?.Invoke(new(_sequence, _capturedAt, _source, payload));
        }
        finally { Native.CloseClipboard(); }
    }
    private async Task RetryAfterAsync(int delay, uint sequence)
    {
        try { await Task.Delay(delay, _stop.Token); Native.PostMessage(_window, RetryMessage, sequence, 0); }
        catch (OperationCanceledException) { }
    }
    private void ShowTrayMenu()
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            Native.AppendMenu(menu, 0, 1, "Open ClipScope");
            var captureEnabled = _settings().CaptureEnabled;
            Native.AppendMenu(menu, 0, 2, captureEnabled ? "Pause clipboard capture" : "Resume clipboard capture");
            Native.AppendMenu(menu, 0, 3, "Settings"); Native.AppendMenu(menu, 0, 4, "Exit");
            Native.GetCursorPos(out var cursor);
            var previous = Native.GetForegroundWindow();
            Native.SetForegroundWindow(_window);
            switch (Native.TrackPopupMenu(menu, 0x0100 | 0x0002, cursor.X, cursor.Y, 0, _window, 0))
            {
                case 1: OpenRequested?.Invoke(previous); break;
                case 2: CaptureEnabledRequested?.Invoke(!captureEnabled); break;
                case 3: OpenRequested?.Invoke(previous); SettingsRequested?.Invoke(); break;
                case 4: ExitRequested?.Invoke(); break;
            }
            Native.PostMessage(_window, 0, 0, 0);
        }
        finally { Native.DestroyMenu(menu); }
    }
    private Task<T> InvokeAsync<T>(Func<T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed) { completion.SetException(new ObjectDisposedException(nameof(NativeHost))); return completion.Task; }
        _commands.Enqueue(() => { try { completion.SetResult(function()); } catch (Exception e) { completion.SetException(e); } });
        Native.PostMessage(_window, WorkMessage, 0, 0);
        return completion.Task;
    }
    public Task<bool> SetHotkeyAsync(Hotkey hotkey) => InvokeAsync(() =>
    {
        if (_reservedHotkeyId != 0) return false;
        if (!hotkey.IsValid) return false;
        var newId = _hotkeyId == 1 ? 2 : 1;
        if (!Native.RegisterHotKey(_window, newId, hotkey.Modifiers | 0x4000, hotkey.Key)) return false;
        if (_hotkeyId != 0) Native.UnregisterHotKey(_window, _hotkeyId);
        _hotkeyId = newId; return true;
    });
    public async Task<ISettingsChange?> PrepareHotkeyAsync(Hotkey hotkey)
    {
        var id = await InvokeAsync(() =>
        {
            if (!hotkey.IsValid || _reservedHotkeyId != 0) return 0;
            var next = _hotkeyId == 1 ? 2 : 1;
            if (!Native.RegisterHotKey(_window, next, hotkey.Modifiers | 0x4000, hotkey.Key)) return 0;
            return _reservedHotkeyId = next;
        });
        return id == 0 ? null : new HotkeyChange(this, id);
    }
    private sealed class HotkeyChange(NativeHost host, int id) : ISettingsChange
    {
        private bool _committed;
        public async Task CommitAsync()
        {
            await host.InvokeAsync(() =>
            {
                if (host._hotkeyId != 0) Native.UnregisterHotKey(host._window, host._hotkeyId);
                host._hotkeyId = id; host._reservedHotkeyId = 0;
                return true;
            });
            _committed = true;
        }
        public async ValueTask DisposeAsync()
        {
            if (_committed) return;
            await host.InvokeAsync(() =>
            {
                Native.UnregisterHotKey(host._window, id); host._reservedHotkeyId = 0;
                return true;
            });
        }
    }
    public async Task<bool> RestoreAsync(Clip clip, string? absoluteImagePath)
    {
        var formats = await Task.Run(() => ClipboardWriter.Prepare(clip, absoluteImagePath));
        for (var retry = 0; retry < 6; retry++)
        {
            if (await InvokeAsync(() => ClipboardWriter.Write(_window, _reader!, formats))) return true;
            await Task.Delay(20 * (retry + 1));
        }
        _log.Write("clipboard.restore_failed"); return false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel();
        if (_window != 0) { _commands.Enqueue(() => Native.PostQuitMessage(0)); Native.PostMessage(_window, WorkMessage, 0, 0); }
        // A foreign delayed renderer can hang GetClipboardData; never block shutdown.
        _thread.Join(250);
    }
}

using System.Collections.ObjectModel;
using ClipScope.App.Design;
using ClipScope.Core;
using ClipScope.Infrastructure;
using ClipScope.Windows;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClipScope.App;

internal sealed partial class MainWindow : Window
{
    private readonly DataPaths _paths;
    private readonly IDiagnosticLog _log;
    private readonly SettingsStore _settingsStore;
    private volatile AppSettings _settings;
    private readonly SqliteClipRepository _repository;
    private readonly MediaStore _media;
    private readonly WindowContext _context = new();
    private readonly CaptureCoordinator _capture;
    private readonly NativeHost _native;
    private readonly ObservableCollection<ClipRow> _rows = [];
    private readonly DispatcherQueueTimer _statusTimer;
    private CancellationTokenSource? _searchCancellation, _detailCancellation;
    private ClipFilter _filter;
    private Clip? _selected;
    private nint _pasteTarget;
    private bool _exiting;
    private int _offset;
    private readonly HistorySession _historySession = new();
    private Task<bool> _refreshTask = Task.FromResult(false);
    private bool _commandBusy;
    private const int PageSize = 100;

    public MainWindow(DataPaths paths, IDiagnosticLog log, SettingsStore settingsStore, AppSettings settings, SqliteClipRepository repository)
    {
        _paths = paths; _log = log; _settingsStore = settingsStore; _settings = settings;
        _repository = repository; _media = new(paths, log);
        Title = "ClipScope";
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClipScope.ico")); }
        catch (Exception error) { _log.Write("app.icon_failed", error); }
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2.5); _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => UpdateStatus();
        BuildShell();
        ApplyAppearance();
        SizeToDisplay();
        _capture = new(() => _settings, repository, _media, _context, log);
        _native = new(() => _settings, _context, log);
        _native.Captured += _capture.Enqueue;
        _capture.HistoryChanged += () => DispatcherQueue.TryEnqueue(() => _ = RefreshAsync());
        _native.OpenRequested += hwnd => DispatcherQueue.TryEnqueue(() => ShowHistory(hwnd));
        _native.SettingsRequested += () => DispatcherQueue.TryEnqueue(ShowSettings);
        _native.CaptureEnabledRequested += enabled => DispatcherQueue.TryEnqueue(() => _ = SetCaptureEnabledAsync(enabled));
        _native.ExitRequested += () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync());
        AppWindow.Closing += (sender, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            if (_settingsBusy) return;
            if (_settings.MinimizeToTray && _native.TrayAvailable) AppWindow.Hide(); else _ = ExitAsync();
        };
    }
    public async Task InitializeAsync()
    {
        // Repair a persisted sign-in preference after moving an unpackaged install.
        // The installer also migrates an existing Run value before first launch.
        if (_settings.LaunchAtStartup)
        {
            try { StartupIntegration.SetEnabled(true); }
            catch (Exception error) { _log.Write("startup.registration_failed", error); Notice("Launch at sign-in could not be registered. Check Settings."); }
        }
        await _native.Ready;
        if (!await _native.SetHotkeyAsync(_settings.Shortcut))
            Notice("The global shortcut is already in use. Choose another in Settings; ClipScope is available from the tray.");
        if (!_native.TrayAvailable) Notice("The tray icon is unavailable. ClipScope will exit when its window is closed.");
        UpdateRecordingState();
        await RefreshAsync();
        _ = Task.Run(() => _capture.CleanupAsync());
    }
    public bool HasTray => _native.TrayAvailable;
    public void StopNative() => _native.Dispose();
    public void ShowHistory(nint previous = 0)
    {
        if (_exiting) return;
        var candidate = previous != 0 ? previous : WindowContext.ForegroundHandle;
        if (WindowContext.IsTarget(candidate)) _pasteTarget = candidate;
        if (_settingsView is not null)
        {
            if (_settingsBusy) { ShowSettings(); return; }
            CloseSettings();
        }
        // Opening history starts from the newest clip unless a search is in progress.
        if (_search.Text.Length == 0)
        {
            if (_offset != 0) { _offset = 0; _ = RefreshAsync(selectFirst: true); }
            else if (_rows.Count > 0 && _history.SelectedIndex != 0) _history.SelectedIndex = 0;
        }
        if (_rows.Count > 0) _history.ScrollIntoView(_history.SelectedItem ?? _rows[0]);
        var now = DateTimeOffset.Now; foreach (var row in _rows) row.Refresh(now);
        AppWindow.Show(); Activate(); WindowContext.ShowApp(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _search.Focus(FocusState.Programmatic); _search.SelectAll();
    }
    private Task<bool> RefreshAsync(bool debounce = false, bool selectFirst = false)
        => _refreshTask = RefreshCoreAsync(debounce, selectFirst);

    private async Task<bool> RefreshCoreAsync(bool debounce, bool selectFirst)
    {
        if (_exiting) return false;
        var selectedIndex = _history.SelectedIndex;
        var followNewest = selectedIndex == 0 && _search.Text.Length == 0 && _offset == 0;
        var selectedId = selectFirst || followNewest ? null : (_history.SelectedItem as ClipRow)?.Clip.Id;
        var query = new SearchRequest(_search.Text, _filter, PageSize, _offset);
        var version = _historySession.BeginSearch();
        _history.IsEnabled = false;
        _selected = null; _detail.ShowEmpty(false);
        _searchCancellation?.Cancel();
        // Each operation owns/disposes its CTS after its last use.
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var ct = cancellation.Token;
        try
        {
            if (debounce) await Task.Delay(160, ct);
            var results = await Task.Run(() => _repository.SearchAsync(query, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (_exiting || version != _historySession.Version) return false;
            var now = DateTimeOffset.Now;
            foreach (var oldRow in _rows) oldRow.Dispose();
            _rows.Clear(); foreach (var result in results) _rows.Add(new(result, Absolute(result.IconPath), Absolute(result.ThumbnailPath), now));
            _historySession.CompleteSearch(version);
            _history.SelectedItem = _rows.FirstOrDefault(x => x.Clip.Id == selectedId)
                ?? (selectedId is not null && selectedIndex >= 0 && _rows.Count > 0 ? _rows[Math.Min(selectedIndex, _rows.Count - 1)] : _rows.FirstOrDefault());
            if (_history.SelectedItem is { } item) _history.ScrollIntoView(item);
            if (_rows.Count == 0) EmptyDetail();
            _history.IsEnabled = true;
            UpdateEmptyState(); UpdateRecordingState(); UpdateStatus();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e)
        {
            if (version == _historySession.Version)
            { _log.Write("search.failed", e); Notice("History could not be loaded. Check available storage and try again."); }
            return false;
        }
        finally { if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null; }
    }
    private async Task LoadSelectionAsync()
    {
        _detailCancellation?.Cancel();
        _historySession.Select((_history.SelectedItem as ClipRow)?.Clip.Id);
        _selected = null; EmptyDetail();
        var target = _historySession.Target;
        if (target is null) return;
        using var cancellation = new CancellationTokenSource();
        _detailCancellation = cancellation;
        var ct = cancellation.Token;
        try
        {
            var clip = await Task.Run(() => _repository.GetAsync(target.Id, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (_exiting || !_historySession.IsCurrent(target)) return;
            _selected = clip;
            if (clip is not null && _history.SelectedItem is ClipRow row) _detail.Show(clip, row);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.Write("preview.failed", e); Notice("This clip could not be previewed. It may have been removed."); }
        finally { if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null; }
    }
    private async Task RunHistoryCommandAsync(Func<Clip, Task> command)
    {
        if (_commandBusy || _settingsView is not null || _exiting) return;
        _commandBusy = true;
        try
        {
            var version = _historySession.Version;
            var target = _historySession.Target;
            if (target is null)
            {
                // Flush the current text/filter/page search, never the previous results.
                if (!await _refreshTask || version != _historySession.Version) return;
                target = _historySession.Target;
            }
            if (target is null || !_historySession.IsCurrent(target)) return;
            var clip = await Task.Run(() => _repository.GetAsync(target.Id));
            if (clip is null || _exiting || !_historySession.IsCurrent(target)) return;
            await command(clip);
        }
        catch (Exception e) { _log.Write("history.command_failed", e); Notice("The clip could not be used. It may have been removed or its media may be missing."); }
        finally { _commandBusy = false; }
    }
    private Task RestoreAsync(bool paste)
    {
        var destination = _pasteTarget;
        return RunHistoryCommandAsync(async clip =>
        {
            if (!await _native.RestoreAsync(clip, clip.PrimaryMediaPath is null ? null : _paths.ResolveMedia(clip.PrimaryMediaPath)))
            { Notice("The clipboard is busy. Try Copy again."); return; }
            if (_exiting) return;
            if (paste)
            {
                if (!WindowContext.IsTarget(destination)) { Notice("Copied. Activate a destination application and paste with Ctrl+V."); return; }
                AppWindow.Hide();
                if (!await PasteService.PasteAsync(destination))
                { ShowHistory(); Notice("Copied. Windows could not paste automatically; use Ctrl+V in the destination."); }
            }
            else FlashStatus("Copied to clipboard");
        });
    }
    private Task RestoreCurrentAsync(bool paste) => RestoreAsync(paste);
    private Task PinAsync() => RunHistoryCommandAsync(async clip =>
    {
        await Task.Run(() => _repository.SetPinnedAsync(clip.Id, !clip.IsPinned));
        await RefreshAsync(); FlashStatus(clip.IsPinned ? "Unpinned" : "Pinned");
    });
    private Task DeleteAsync() => RunHistoryCommandAsync(async clip =>
    {
        await Task.Run(async () => _media.Delete(await _repository.DeleteAsync(clip.Id)));
        await RefreshAsync(); FlashStatus("Clip deleted");
    });
    private Task TogglePauseAsync() => SetCaptureEnabledAsync(!_settings.CaptureEnabled);
    private async Task SetCaptureEnabledAsync(bool enabled)
    {
        if (_exiting) return;
        await _settingsGate.WaitAsync();
        try
        {
            var updated = _settings with { CaptureEnabled = enabled };
            // Pause immediately; resume only after persistence succeeds.
            if (!enabled) { _settings = updated; UpdateRecordingState(); UpdateEmptyState(); }
            await _settingsStore.SaveAsync(updated);
            _settings = updated; UpdateRecordingState(); UpdateEmptyState();
        }
        catch (Exception e)
        {
            _settings = _settings with { CaptureEnabled = false }; UpdateRecordingState(); UpdateEmptyState();
            _log.Write("settings.pause_failed", e); Notice("Recording state could not be saved. Recording remains paused for this session.");
        }
        finally { _settingsGate.Release(); }
    }
    private void ApplyAppearance()
    {
        _root.RequestedTheme = _settings.Theme switch
        { AppTheme.Dark => ElementTheme.Dark, AppTheme.Light => ElementTheme.Light, _ => ElementTheme.Default };
        UpdateCaptionButtons();
    }
    private string? Absolute(string? relative)
    { try { return relative is null ? null : _paths.ResolveMedia(relative); } catch (ArgumentException) { return null; } }
    private void EmptyDetail() => _detail.ShowEmpty(_rows.Count == 0);
    private void Notice(string message) { if (_exiting) return; _notice.Message = message; _notice.Severity = InfoBarSeverity.Warning; _notice.IsOpen = true; }
    private void FlashStatus(string message) { if (_exiting) return; _status.Text = message; _statusTimer.Stop(); _statusTimer.Start(); }
    private async Task ExitAsync()
    {
        if (_exiting) return;
        if (_settingsBusy || _settingsGate.CurrentCount == 0) { Notice("Wait for settings to finish saving before exiting."); return; }
        _exiting = true;
        _statusTimer.Stop();
        _historySession.BeginSearch();
        _searchCancellation?.Cancel(); _detailCancellation?.Cancel();
        _native.Captured -= _capture.Enqueue;
        _native.Dispose();
        try { await _capture.DisposeAsync(); } catch (Exception e) { _log.Write("capture.shutdown_timeout", e); }
        _log.Write("app.shutdown"); Close(); Application.Current.Exit();
    }
}

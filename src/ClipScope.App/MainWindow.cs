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
    private string _resultsQuery = "";
    private const int PageSize = 100;

    public MainWindow(DataPaths paths, IDiagnosticLog log, SettingsStore settingsStore, AppSettings settings, SqliteClipRepository repository)
    {
        _paths = paths; _log = log; _settingsStore = settingsStore; _settings = settings;
        _repository = repository; _media = new(paths, log);
        Title = "ClipScope";
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
        _native.PauseRequested += () => DispatcherQueue.TryEnqueue(() => _ = TogglePauseAsync());
        _native.ExitRequested += () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync());
        AppWindow.Closing += (sender, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            if (_settings.MinimizeToTray && _native.TrayAvailable) AppWindow.Hide(); else _ = ExitAsync();
        };
    }
    public async Task InitializeAsync()
    {
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
        var candidate = previous != 0 ? previous : WindowContext.ForegroundHandle;
        if (WindowContext.IsTarget(candidate)) _pasteTarget = candidate;
        if (_settingsView is not null) CloseSettings();
        // Opening history starts from the newest clip unless a search is in progress.
        if (_search.Text.Length == 0 && _rows.Count > 0 && _history.SelectedIndex != 0) _history.SelectedIndex = 0;
        if (_rows.Count > 0) _history.ScrollIntoView(_history.SelectedItem ?? _rows[0]);
        var now = DateTimeOffset.Now; foreach (var row in _rows) row.Refresh(now);
        AppWindow.Show(); Activate(); WindowContext.ShowApp(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _search.Focus(FocusState.Programmatic); _search.SelectAll();
    }
    private async Task RefreshAsync(bool debounce = false, bool selectFirst = false)
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new(); var ct = cancellation.Token;
        try
        {
            if (debounce) await Task.Delay(160, ct);
            var text = _search.Text;
            var query = new SearchRequest(text, _filter, PageSize, _offset);
            var results = await Task.Run(() => _repository.SearchAsync(query, ct), ct);
            ct.ThrowIfCancellationRequested();
            var selectedIndex = _history.SelectedIndex;
            // While the newest clip is selected in unfiltered history, keep following the newest.
            var followNewest = selectedIndex == 0 && text.Length == 0 && _offset == 0;
            var selectedId = selectFirst || followNewest ? null : (_history.SelectedItem as ClipRow)?.Clip.Id ?? _selected?.Id;
            var now = DateTimeOffset.Now;
            _rows.Clear(); foreach (var result in results) _rows.Add(new(result, Absolute(result.IconPath), Absolute(result.ThumbnailPath), now));
            _resultsQuery = text;
            // Keep the selected clip; after it disappears (deletion) keep the same position.
            _history.SelectedItem = _rows.FirstOrDefault(x => x.Clip.Id == selectedId)
                ?? (selectedId is not null && selectedIndex >= 0 && _rows.Count > 0 ? _rows[Math.Min(selectedIndex, _rows.Count - 1)] : _rows.FirstOrDefault());
            if (_history.SelectedItem is { } item) _history.ScrollIntoView(item);
            if (_rows.Count == 0) { _selected = null; EmptyDetail(); }
            UpdateEmptyState(); UpdateRecordingState(); UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.Write("search.failed", e); Notice("History could not be loaded. Check available storage and try again."); }
    }
    private async Task LoadSelectionAsync()
    {
        _detailCancellation?.Cancel(); _detailCancellation?.Dispose();
        var cancellation = _detailCancellation = new(); var ct = cancellation.Token;
        if (_history.SelectedItem is not ClipRow row) return;
        _selected = null;
        try
        {
            var clip = await Task.Run(() => _repository.GetAsync(row.Clip.Id, ct), ct); ct.ThrowIfCancellationRequested();
            _selected = clip; if (clip is null) { EmptyDetail(); return; }
            _detail.Show(clip, row);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.Write("preview.failed", e); Notice("This clip could not be previewed. It may have been removed."); }
    }
    private async Task RestoreAsync(bool paste)
    {
        if (_history.SelectedItem is not ClipRow row) return;
        if (_selected?.Id != row.Clip.Id) await LoadSelectionAsync();
        var clip = _selected; if (clip is null) return;
        try
        {
            if (!await _native.RestoreAsync(clip, clip.PrimaryMediaPath is null ? null : _paths.ResolveMedia(clip.PrimaryMediaPath)))
            { Notice("The clipboard is busy. Try Copy again."); return; }
            if (paste)
            {
                if (!WindowContext.IsTarget(_pasteTarget)) { Notice("Copied. Activate a destination application and paste with Ctrl+V."); return; }
                AppWindow.Hide();
                if (!await PasteService.PasteAsync(_pasteTarget))
                { ShowHistory(); Notice("Copied. Windows could not paste automatically; use Ctrl+V in the destination."); }
            }
            else { FlashStatus("Copied to clipboard"); }
        }
        catch (Exception e) { _log.Write("restore.failed", e); Notice("This clip could not be restored. Its media may be missing or damaged."); }
    }
    // Enter resolves the current search before pasting, so typing then Enter never pastes a stale result.
    private async Task RestoreCurrentAsync(bool paste)
    {
        if (_search.Text != _resultsQuery) await RefreshAsync(selectFirst: true);
        if (_history.SelectedItem is ClipRow row && _selected?.Id != row.Clip.Id) await LoadSelectionAsync();
        await RestoreAsync(paste);
    }
    private async Task PinAsync()
    {
        if (_selected is not { } clip) return;
        try { await Task.Run(() => _repository.SetPinnedAsync(clip.Id, !clip.IsPinned)); await RefreshAsync(); FlashStatus(clip.IsPinned ? "Unpinned" : "Pinned"); }
        catch (Exception e) { _log.Write("pin.failed", e); Notice("The pin could not be saved."); }
    }
    private async Task DeleteAsync()
    {
        if (_selected is not { } clip) return;
        try { await Task.Run(async () => _media.Delete(await _repository.DeleteAsync(clip.Id))); await RefreshAsync(); FlashStatus("Clip deleted"); }
        catch (Exception e) { _log.Write("delete.failed", e); Notice("The clip could not be deleted."); }
    }
    private async Task TogglePauseAsync()
    {
        try
        {
            var updated = _settings with { CaptureEnabled = !_settings.CaptureEnabled };
            // Stop immediately, before disk I/O; failed persistence never resumes capture.
            _settings = updated; UpdateRecordingState(); UpdateEmptyState(); await _settingsStore.SaveAsync(updated);
        }
        catch (Exception e) { _log.Write("settings.pause_failed", e); Notice("Recording state changed for this session, but could not be saved."); }
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
    private void Notice(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Warning; _notice.IsOpen = true; }
    private void FlashStatus(string message) { _status.Text = message; _statusTimer.Stop(); _statusTimer.Start(); }
    private async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true;
        _native.Dispose();
        try { await _capture.DisposeAsync(); } catch (Exception e) { _log.Write("capture.shutdown_timeout", e); }
        _log.Write("app.shutdown"); Close(); Application.Current.Exit();
    }
}

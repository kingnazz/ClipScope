using System.Collections.ObjectModel;
using ClipScope.Core;
using ClipScope.Infrastructure;
using ClipScope.Windows;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;

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
    private readonly Grid _root = new() { Padding = new Thickness(20), RowSpacing = 16 };
    private readonly TextBox _search = new() { PlaceholderText = "Search clipboard history", MinWidth = 240, MaxLength = 4096, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _history = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
    private readonly StackPanel _detail = new() { Spacing = 16 };
    private readonly TextBlock _status = new() { Opacity = 0.7, FontSize = 12, Text = "Starting clipboard capture…" };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly Button _pause = new();
    private CancellationTokenSource? _searchCancellation, _detailCancellation;
    private ClipFilter _filter;
    private Clip? _selected;
    private nint _pasteTarget;
    private bool _exiting, _settingsOpen;
    private int _offset;

    public MainWindow(DataPaths paths, IDiagnosticLog log, SettingsStore settingsStore, AppSettings settings, SqliteClipRepository repository)
    {
        _paths = paths; _log = log; _settingsStore = settingsStore; _settings = settings;
        _repository = repository; _media = new(paths, log);
        Title = "ClipScope";
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1240, 800));
        Content = _root;
        BuildLayout(); ApplyAppearance();
        _capture = new(() => _settings, repository, _media, _context, log);
        _native = new(() => _settings, _context, log);
        _native.Captured += _capture.Enqueue;
        _capture.HistoryChanged += () => DispatcherQueue.TryEnqueue(() => _ = RefreshAsync());
        _native.OpenRequested += hwnd => DispatcherQueue.TryEnqueue(() => ShowHistory(hwnd));
        _native.SettingsRequested += () => DispatcherQueue.TryEnqueue(() => _ = ShowSettingsAsync());
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
        AppWindow.Show(); Activate(); WindowContext.ShowApp(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _search.Focus(FocusState.Programmatic);
    }
    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 20 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(190) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var branding = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        branding.Children.Add(new FontIcon { Glyph = "\uE8C8", FontSize = 25, Foreground = new SolidColorBrush(Color.FromArgb(255, 95, 155, 245)) });
        branding.Children.Add(new TextBlock { Text = "ClipScope", FontSize = 23, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        header.Children.Add(branding); Grid.SetColumn(_search, 1); header.Children.Add(_search);
        var settings = Button("Settings", async () => await ShowSettingsAsync()); Grid.SetColumn(settings, 2); header.Children.Add(settings);
        _root.Children.Add(header); Grid.SetRow(_notice, 1); _root.Children.Add(_notice);
        var body = new Grid { ColumnSpacing = 20 };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(160) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(390) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        var sidebar = new StackPanel { Spacing = 6 };
        sidebar.Children.Add(new TextBlock { Text = "YOUR CLIPBOARD", FontSize = 11, Opacity = 0.6, Margin = new Thickness(10, 6, 0, 10) });
        var filters = new List<ToggleButton>();
        foreach (var (filter, glyph, label) in new[] {
            (ClipFilter.All, "\uE8A9", "All clips"), (ClipFilter.Text, "\uE8D2", "Text"),
            (ClipFilter.Images, "\uEB9F", "Images"), (ClipFilter.Files, "\uE8B7", "Files"), (ClipFilter.Pinned, "\uE718", "Pinned") })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 }); row.Children.Add(new TextBlock { Text = label });
            var toggle = new ToggleButton { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12), IsChecked = filter == ClipFilter.All };
            filters.Add(toggle); sidebar.Children.Add(toggle);
            toggle.Click += (_, _) => { foreach (var other in filters) other.IsChecked = other == toggle; _filter = filter; _offset = 0; _ = RefreshAsync(); };
        }
        sidebar.Children.Add(new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0,16,0,16) });
        _pause.HorizontalAlignment = HorizontalAlignment.Stretch; _pause.Click += async (_, _) => await TogglePauseAsync(); sidebar.Children.Add(_pause);
        sidebar.Children.Add(new TextBlock { Text = "Stored on this device", FontSize = 12, Opacity = 0.5, Margin = new Thickness(10, 12, 0, 0) });
        body.Children.Add(sidebar);
        var historyGrid = new Grid { RowSpacing = 8 };
        historyGrid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        historyGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _history.ItemsSource = _rows;
        _history.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Padding="8,12" ColumnSpacing="10" MinWidth="320">
                <Grid.ColumnDefinitions><ColumnDefinition Width="28"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <Image Source="{Binding Icon}" Width="24" Height="24" VerticalAlignment="Top"/>
                <StackPanel Grid.Column="1" Spacing="6">
                  <Grid><TextBlock Text="{Binding App}" FontWeight="SemiBold" FontSize="13" MaxWidth="195" HorizontalAlignment="Left" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding Pin}" FontSize="11" Opacity="0.6" HorizontalAlignment="Right"/></Grid>
                  <TextBlock Text="{Binding Preview}" FontSize="14" MaxLines="3" TextWrapping="Wrap" TextTrimming="CharacterEllipsis"/>
                  <Image Source="{Binding Thumbnail}" MaxHeight="76" MaxWidth="270" HorizontalAlignment="Left" Stretch="Uniform"/>
                  <TextBlock Text="{Binding Caption}" Opacity="0.55" FontSize="11"/>
                </StackPanel>
              </Grid>
            </DataTemplate>
            """);
        _history.SelectionChanged += async (_, _) => await LoadSelectionAsync();
        _history.DoubleTapped += async (_, _) => await RestoreAsync(true);
        _history.KeyDown += async (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; await RestoreAsync(true); } };
        historyGrid.Children.Add(_history);
        var pagination = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pagination.Children.Add(Button("← Newer", async () => { _offset = Math.Max(0, _offset - 100); await RefreshAsync(); }));
        pagination.Children.Add(Button("Older →", async () => { if (_rows.Count == 100) { _offset += 100; await RefreshAsync(); } }));
        Grid.SetRow(pagination, 1); historyGrid.Children.Add(pagination);
        Grid.SetColumn(historyGrid, 1); body.Children.Add(historyGrid);
        var detailBorder = new Border { Background = Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22),
            Child = new ScrollViewer { Content = _detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Grid.SetColumn(detailBorder, 2); body.Children.Add(detailBorder);
        Grid.SetRow(body, 2); _root.Children.Add(body);
        Grid.SetRow(_status, 3); _root.Children.Add(_status);
        AutomationProperties.SetName(_search, "Search clipboard history");
        _search.TextChanged += (_, _) => { _offset = 0; _ = RefreshAsync(true); };
        _search.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Down && _rows.Count > 0) { _history.SelectedIndex = Math.Max(0, _history.SelectedIndex); _history.Focus(FocusState.Keyboard); args.Handled = true; }
            if (args.Key == VirtualKey.Enter) { args.Handled = true; await RefreshAsync(); await LoadSelectionAsync(); await RestoreAsync(true); }
            if (args.Key == VirtualKey.Escape) AppWindow.Hide();
        };
    }
    private async Task RefreshAsync(bool debounce = false)
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new(); var ct = cancellation.Token;
        try
        {
            if (debounce) await Task.Delay(160, ct);
            var query = new SearchRequest(_search.Text, _filter, 100, _offset);
            var results = await Task.Run(() => _repository.SearchAsync(query, ct), ct);
            ct.ThrowIfCancellationRequested();
            var selectedId = _selected?.Id;
            _rows.Clear(); foreach (var result in results) _rows.Add(new(result, Absolute(result.IconPath), Absolute(result.ThumbnailPath)));
            _history.SelectedItem = _rows.FirstOrDefault(x => x.Clip.Id == selectedId) ?? _rows.FirstOrDefault();
            if (_rows.Count == 0) { _selected = null; EmptyDetail(); }
            UpdateRecordingState();
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
        _detail.Children.Clear(); _detail.Children.Add(Metadata("Loading clip…"));
        try
        {
            var clip = await Task.Run(() => _repository.GetAsync(row.Clip.Id, ct), ct); ct.ThrowIfCancellationRequested();
            _selected = clip; if (clip is null) { EmptyDetail(); return; }
            _detail.Children.Clear();
            _detail.Children.Add(new TextBlock { Text = clip.Kind.ToString().ToUpperInvariant(), FontSize = 11, Opacity = 0.55 });
            _detail.Children.Add(new TextBlock { Text = clip.Source.AppName, FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            actions.Children.Add(Button("Copy", async () => await RestoreAsync(false)));
            actions.Children.Add(Button("Paste ↵", async () => await RestoreAsync(true)));
            actions.Children.Add(Button(clip.IsPinned ? "Unpin" : "Pin", async () => await PinAsync()));
            actions.Children.Add(Button("Delete", async () => await DeleteAsync()));
            _detail.Children.Add(actions);
            if (clip.Kind == ClipKind.Image && clip.PrimaryMediaPath is { } image) AddImage(image);
            var text = clip.Kind == ClipKind.Files ? string.Join(Environment.NewLine, clip.Files) : clip.PlainText;
            if (text is not null)
            {
                _detail.Children.Add(new TextBlock { Text = text[..Math.Min(text.Length, 32768)], IsTextSelectionEnabled = true,
                    FontFamily = new FontFamily(clip.Kind == ClipKind.Text ? "Cascadia Mono, Consolas" : "Segoe UI Variable"), FontSize = 14, TextWrapping = TextWrapping.Wrap });
                if (text.Length > 32768) _detail.Children.Add(Metadata("Preview shortened. Copy restores the full content."));
            }
            _detail.Children.Add(new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush") });
            _detail.Children.Add(Metadata($"Copied {clip.CreatedUtc.ToOffset(clip.LocalOffset):f}\nUTC {clip.CreatedUtc:O}\n{clip.Source.WindowTitle}\n{clip.Source.ProcessName} · PID {clip.Source.ProcessId}\n{clip.Source.ExecutablePath}\n{clip.Source.MonitorName}\nFormats: {string.Join(", ", clip.Formats)}"));
            if (clip.ContextScreenshotPath is { } context) { _detail.Children.Add(Metadata("SOURCE WINDOW")); AddImage(context); }
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
            else { _status.Text = "Copied to clipboard"; }
        }
        catch (Exception e) { _log.Write("restore.failed", e); Notice("This clip could not be restored. Its media may be missing or damaged."); }
    }
    private async Task PinAsync()
    {
        if (_selected is not { } clip) return;
        try { await Task.Run(() => _repository.SetPinnedAsync(clip.Id, !clip.IsPinned)); await RefreshAsync(); }
        catch (Exception e) { _log.Write("pin.failed", e); Notice("The pin could not be saved."); }
    }
    private async Task DeleteAsync()
    {
        if (_selected is not { } clip) return;
        try { await Task.Run(async () => _media.Delete(await _repository.DeleteAsync(clip.Id))); await RefreshAsync(); }
        catch (Exception e) { _log.Write("delete.failed", e); Notice("The clip could not be deleted."); }
    }
    private async Task TogglePauseAsync()
    {
        try
        {
            var updated = _settings with { CaptureEnabled = !_settings.CaptureEnabled };
            // Stop immediately, before disk I/O; failed persistence never resumes capture.
            _settings = updated; UpdateRecordingState(); await _settingsStore.SaveAsync(updated);
        }
        catch (Exception e) { _log.Write("settings.pause_failed", e); Notice("Recording state changed for this session, but could not be saved."); }
    }
    private void UpdateRecordingState()
    {
        _pause.Content = _settings.CaptureEnabled ? "Pause capture" : "Resume capture";
        _status.Text = $"{(_settings.CaptureEnabled ? "Recording" : "Paused")} · {_rows.Count} clips shown · {_settings.Shortcut} · Local only";
    }
    private void ApplyAppearance() => _root.RequestedTheme = _settings.Theme switch
    { AppTheme.Dark => ElementTheme.Dark, AppTheme.Light => ElementTheme.Light, _ => ElementTheme.Default };
    private void AddImage(string relative)
    {
        var path = Absolute(relative); if (path is null) { _detail.Children.Add(Metadata("Image unavailable")); return; }
        var image = new Image { Source = new BitmapImage(new Uri(path)) { DecodePixelWidth = 960 }, MaxHeight = 420, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        image.ImageFailed += (_, _) => { image.Visibility = Visibility.Collapsed; };
        _detail.Children.Add(image);
    }
    private string? Absolute(string? relative)
    { try { return relative is null ? null : _paths.ResolveMedia(relative); } catch (ArgumentException) { return null; } }
    private void EmptyDetail()
    { _detail.Children.Clear(); _detail.Children.Add(new TextBlock { Text = "Your clipboard, with context.", FontSize = 24, TextWrapping = TextWrapping.Wrap }); _detail.Children.Add(Metadata("Copy text, an image or files in another application. Select a clip to inspect its source and restore it.")); }
    private void Notice(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Warning; _notice.IsOpen = true; }
    private static TextBlock Metadata(string text) => new() { Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static Button Button(string label, Func<Task> action)
    { var button = new Button { Content = label }; button.Click += async (_, _) => await action(); return button; }
    private async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true;
        _native.Dispose();
        try { await _capture.DisposeAsync(); } catch (Exception e) { _log.Write("capture.shutdown_timeout", e); }
        _log.Write("app.shutdown"); Close(); Application.Current.Exit();
    }
 }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ClipRow(ClipSummary clip, string? icon, string? thumbnail)
{
    public ClipSummary Clip { get; } = clip;
    public string? Icon { get; } = icon;
    public string? Thumbnail { get; } = thumbnail;
    public string App => Clip.SourceApp;
    public string Preview => Clip.Preview;
    public string Pin => Clip.PinLabel;
    public string Caption => $"{Clip.LocalTime} · {Clip.TypeLabel}";
}

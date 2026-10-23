using System.Runtime.InteropServices;
using ClipScope.App.Design;
using ClipScope.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace ClipScope.App;

// Shell layout: title bar with search, filter sidebar, history list, detail pane and footer.
internal sealed partial class MainWindow
{
    private readonly Grid _root = new();
    private readonly TitleBar _titleBar = new();
    private readonly Grid _searchHost = new();
    private readonly TextBox _search = new();
    private readonly ListView _filters = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _history = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(Ui.SidebarWidth) };
    private readonly ColumnDefinition _historyColumn = new() { Width = new GridLength(4, GridUnitType.Star), MinWidth = 300, MaxWidth = 520 };
    private readonly Grid _body = new();
    private ClipDetailView _detail = null!;
    private Grid _recordingPanel = null!;
    private StackPanel _searchKeys = null!, _emptyState = null!, _emptyKeys = null!, _hints = null!, _recordingText = null!, _pager = null!;
    private TextBlock _status = null!, _emptyTitle = null!, _emptyBody = null!, _recordingTitle = null!, _recordingCaption = null!, _pageLabel = null!;
    private FontIcon _emptyGlyph = null!;
    private Ellipse _recordingDot = null!;
    private Button _pause = null!, _newer = null!, _older = null!, _settingsButton = null!;
    private readonly List<TextBlock> _filterLabels = [];
    private bool _compact, _ctrlHeld;
    private XamlRoot? _observedXamlRoot;
    private (double Scale, int Width, int Height) _displayMetrics;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    private void BuildShell()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Content = _root;
        BuildTitleBar();
        _notice.Margin = new Thickness(Ui.Space4, 0, Ui.Space4, Ui.Space2);
        Grid.SetRow(_notice, 1); _root.Children.Add(_notice);
        _body.ColumnDefinitions.Add(_sidebarColumn);
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _body.Children.Add(BuildSidebar());
        var surface = BuildContentSurface(); Grid.SetColumn(surface, 1); _body.Children.Add(surface);
        Grid.SetRow(_body, 2); _root.Children.Add(_body);
        _root.SizeChanged += (_, args) => Relayout(args.NewSize.Width);
        _root.KeyDown += OnShellKeyDown;
        // Ctrl as seen by this window: set and cleared by the key events it receives, and
        // reset whenever activation changes, so a Ctrl released in another app (for example
        // after the global shortcut) never reads as held here.
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) => { if (IsCtrlKey(args.Key)) _ctrlHeld = true; }), true);
        _root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, args) => { if (IsCtrlKey(args.Key)) _ctrlHeld = false; }), true);
        Activated += (_, _) => _ctrlHeld = false;
        _root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        _root.Loaded += (_, _) =>
        {
            if (_observedXamlRoot is not null) return;
            _observedXamlRoot = _root.XamlRoot;
            if (_observedXamlRoot is not null) _observedXamlRoot.Changed += OnXamlRootChanged;
            UpdateDisplayMetrics();
        };
        AppWindow.Changed += (_, args) => { if (args.DidPositionChange || args.DidPresenterChange) UpdateDisplayMetrics(); };
        Closed += (_, _) =>
        {
            if (_observedXamlRoot is not null) _observedXamlRoot.Changed -= OnXamlRootChanged;
            _detail.ShowEmpty(true);
            foreach (var row in _rows) row.Dispose();
        };
        AutomationProperties.SetLandmarkType(_body, AutomationLandmarkType.Main);
    }

    private void BuildTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _titleBar.Title = "ClipScope";
        _titleBar.LeftHeader = new FontIcon { Glyph = Glyph.Brand, FontSize = 16, Style = Ui.Style("CsAccentIconStyle"), Margin = new Thickness(Ui.Space3, 0, 0, 0) };
        _titleBar.BackRequested += (_, _) => CloseSettings();

        _search.Style = Ui.Style("CsSearchBoxStyle");
        _search.PlaceholderText = "Search clipboard history";
        _search.MaxLength = 4096;
        _search.Padding = new Thickness(36, 7, 112, 6);
        AutomationProperties.SetName(_search, "Search clipboard history");
        AutomationProperties.SetHelpText(_search, "Type to filter. Up and Down choose a clip, Enter pastes it, Control+Enter copies it.");
        AutomationProperties.SetLandmarkType(_search, AutomationLandmarkType.Search);
        var glyph = Ui.Icon(Glyph.Search, 14, "CsSecondaryIconStyle");
        glyph.HorizontalAlignment = HorizontalAlignment.Left; glyph.Margin = new Thickness(Ui.Space3, 0, 0, 0); glyph.IsHitTestVisible = false;
        _searchKeys = Ui.Keycaps(ClipPresentation.ShortcutKeys(_settings.Shortcut));
        _searchKeys.HorizontalAlignment = HorizontalAlignment.Right; _searchKeys.Margin = new Thickness(0, 0, Ui.Space3, 0); _searchKeys.IsHitTestVisible = false;
        _searchHost.Width = 480;
        _searchHost.Children.Add(_search); _searchHost.Children.Add(glyph); _searchHost.Children.Add(_searchKeys);
        _titleBar.Content = _searchHost;

        _settingsButton = Ui.SubtleIconButton(Glyph.Settings, "Settings", "Settings (Ctrl+,)");
        _settingsButton.Margin = new Thickness(0, 0, Ui.Space1, 0);
        _settingsButton.Click += (_, _) => ShowSettings();
        _titleBar.RightHeader = _settingsButton;
        _root.Children.Add(_titleBar);
        SetTitleBar(_titleBar);

        _search.TextChanged += (_, _) =>
        {
            var empty = _search.Text.Length == 0;
            _searchKeys.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _search.Padding = new Thickness(36, 7, empty ? 112 : 8, 6);
            _offset = 0; _ = RefreshAsync(true, selectFirst: true);
        };
        _search.KeyDown += OnSearchKeyDown;
    }

    private Grid BuildSidebar()
    {
        var sidebar = new Grid { Padding = new Thickness(Ui.Space1, Ui.Space1, Ui.Space1, Ui.Space3) };
        sidebar.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _filters.ItemContainerTransitions = [];
        _filters.ItemContainerStyle = Ui.Style("CsNavItemStyle");
        AutomationProperties.SetName(_filters, "Filters");
        AutomationProperties.SetLandmarkType(_filters, AutomationLandmarkType.Navigation);
        var index = 1;
        foreach (var (filter, glyph, label) in new[] {
            (ClipFilter.All, Glyph.AllClips, "All clips"), (ClipFilter.Text, Glyph.Text, "Text"),
            (ClipFilter.Images, Glyph.Image, "Images"), (ClipFilter.Files, Glyph.Files, "Files"), (ClipFilter.Pinned, Glyph.Pinned, "Pinned") })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space3 };
            content.Children.Add(Ui.Icon(glyph, 16));
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            _filterLabels.Add(text); content.Children.Add(text);
            var item = new ListViewItem { Content = content, Tag = filter };
            AutomationProperties.SetName(item, label);
            ToolTipService.SetToolTip(item, $"{label} (Ctrl+{index++})");
            _filters.Items.Add(item);
        }
        _filters.SelectedIndex = 0;
        _filters.SelectionChanged += (_, args) =>
        {
            if (_filters.SelectedItem is not ListViewItem { Tag: ClipFilter filter })
            { if (args.RemovedItems.FirstOrDefault() is { } previous) _filters.SelectedItem = previous; return; }
            if (filter == _filter) return;
            _filter = filter; _offset = 0; _ = RefreshAsync(selectFirst: true);
        };
        sidebar.Children.Add(_filters);

        var recording = _recordingPanel = new Grid { ColumnSpacing = Ui.Space3, Padding = new Thickness(Ui.Space3, Ui.Space3, Ui.Space1, 0), Margin = new Thickness(Ui.Space1, 0, Ui.Space1, 0) };
        recording.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        recording.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        recording.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _recordingDot = new Ellipse { VerticalAlignment = VerticalAlignment.Center, Style = Ui.Style("CsRecordingDotStyle") };
        _recordingDot.Margin = new Thickness(Ui.Space1, 0, 0, 0);
        _recordingText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _recordingTitle = new TextBlock { Style = Ui.Style("CsBodyStrongTextStyle"), FontSize = 13 };
        _recordingCaption = Ui.Text("CsTertiaryTextStyle", "Local only");
        _recordingText.Children.Add(_recordingTitle); _recordingText.Children.Add(_recordingCaption);
        _pause = Ui.SubtleIconButton(Glyph.Pause, "Pause recording", "Pause recording", 14);
        _pause.Click += async (_, _) => await TogglePauseAsync();
        Grid.SetColumn(_recordingText, 1); Grid.SetColumn(_pause, 2);
        recording.Children.Add(_recordingDot); recording.Children.Add(_recordingText); recording.Children.Add(_pause);
        var footer = new StackPanel { Spacing = Ui.Space2 };
        footer.Children.Add(Ui.Divider()); footer.Children.Add(recording);
        footer.Margin = new Thickness(Ui.Space2, 0, Ui.Space2, 0);
        AutomationProperties.SetName(footer, "Recording status");
        Grid.SetRow(footer, 1); sidebar.Children.Add(footer);
        return sidebar;
    }

    private Border BuildContentSurface()
    {
        var content = new Grid();
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(_historyColumn);
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(6, GridUnitType.Star), MinWidth = 360 });

        var history = new Grid();
        _history.ItemsSource = _rows;
        _history.ItemTemplate = (DataTemplate)XamlReader.Load(ClipRow.Template);
        _history.ItemContainerStyle = Ui.Style("CsHistoryItemStyle");
        _history.ItemContainerTransitions = [];
        _history.Padding = new Thickness(0, Ui.Space2, 0, Ui.Space2);
        _history.TabNavigation = KeyboardNavigationMode.Once;
        AutomationProperties.SetName(_history, "Clipboard history");
        _history.SelectionChanged += async (_, _) => await LoadSelectionAsync();
        _history.DoubleTapped += async (_, _) => await RestoreAsync(true);
        _history.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter) { args.Handled = true; await RestoreAsync(!IsCtrlDown()); }
            else if (args.Key == VirtualKey.Delete) { args.Handled = true; await DeleteAsync(); }
        };
        history.Children.Add(_history);
        history.Children.Add(BuildEmptyState());
        content.Children.Add(history);

        var divider = Ui.Divider(vertical: true); Grid.SetColumn(divider, 1); Grid.SetRowSpan(divider, 1); content.Children.Add(divider);

        _detail = new ClipDetailView(Absolute)
        {
            PasteRequested = () => RestoreAsync(true), CopyRequested = () => RestoreAsync(false),
            PinRequested = PinAsync, DeleteRequested = DeleteAsync
        };
        Grid.SetColumn(_detail.Root, 2); content.Children.Add(_detail.Root);

        var footer = BuildFooter(); Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 3); content.Children.Add(footer);
        return new Border { Style = Ui.Style("CsContentSurfaceStyle"), Child = content };
    }

    private StackPanel BuildEmptyState()
    {
        _emptyState = new StackPanel { Spacing = Ui.Space3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = Ui.Pad(Ui.Space5), Visibility = Visibility.Collapsed };
        _emptyGlyph = Ui.Icon(Glyph.Brand, 22, "CsSecondaryIconStyle");
        _emptyState.Children.Add(new Border { Style = Ui.Style("CsEmptyGlyphTileStyle"), Child = _emptyGlyph, Margin = new Thickness(0, 0, 0, Ui.Space1) });
        _emptyTitle = new TextBlock { Style = Ui.Style("CsEmptyTitleTextStyle"), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        _emptyBody = new TextBlock { Style = Ui.Style("CsEmptyBodyTextStyle") };
        _emptyKeys = Ui.Keycaps(["Ctrl", "C"]); _emptyKeys.HorizontalAlignment = HorizontalAlignment.Center; _emptyKeys.Margin = new Thickness(0, Ui.Space1, 0, 0);
        _emptyState.Children.Add(_emptyTitle); _emptyState.Children.Add(_emptyBody); _emptyState.Children.Add(_emptyKeys);
        _emptyState.Transitions = [new EntranceThemeTransition { FromVerticalOffset = 8 }];
        return _emptyState;
    }

    private Grid BuildFooter()
    {
        var footer = new Grid { Padding = new Thickness(Ui.Space4, 0, Ui.Space4, 0), MinHeight = 36, ColumnSpacing = Ui.Space4 };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var line = Ui.Divider(); line.VerticalAlignment = VerticalAlignment.Top; line.Margin = new Thickness(-Ui.Space4, 0, -Ui.Space4, 0);
        Grid.SetColumnSpan(line, 3); footer.Children.Add(line);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space2, VerticalAlignment = VerticalAlignment.Center };
        _status = Ui.Text("CsCaptionTextStyle");
        _status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        _newer = Ui.SubtleIconButton(Glyph.ChevronLeft, "Newer clips", "Newer clips", 12);
        _older = Ui.SubtleIconButton(Glyph.ChevronRight, "Older clips", "Older clips", 12);
        foreach (var button in new[] { _newer, _older }) { button.MinWidth = 28; button.MinHeight = 28; button.Padding = Ui.Pad(Ui.Space1 + 2); }
        _newer.Click += async (_, _) => { _offset = Math.Max(0, _offset - PageSize); await RefreshAsync(selectFirst: true); };
        _older.Click += async (_, _) => { if (_rows.Count == PageSize) { _offset += PageSize; await RefreshAsync(selectFirst: true); } };
        _pageLabel = Ui.Text("CsTertiaryTextStyle"); _pageLabel.VerticalAlignment = VerticalAlignment.Center; _pageLabel.Margin = new Thickness(Ui.Space1, 0, Ui.Space1, 0);
        _pager.Children.Add(_newer); _pager.Children.Add(_pageLabel); _pager.Children.Add(_older);
        left.Children.Add(_status); left.Children.Add(_pager);
        footer.Children.Add(left);

        _hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space4, VerticalAlignment = VerticalAlignment.Center };
        _hints.Children.Add(Ui.KeyHint("Paste", "Enter pastes the selected clip", "↵"));
        _hints.Children.Add(Ui.KeyHint("Copy", "Control plus Enter copies without pasting", "Ctrl", "↵"));
        _hints.Children.Add(Ui.KeyHint("Pin", "Control plus P pins or unpins", "Ctrl", "P"));
        _hints.Children.Add(Ui.KeyHint("Close", "Escape closes ClipScope", "Esc"));
        Grid.SetColumn(_hints, 2); footer.Children.Add(_hints);
        return footer;
    }

    private void Relayout(double width)
    {
        _searchHost.Width = Math.Clamp(width * 0.4, 240, 560);
        var compact = width < Ui.CompactWidthThreshold;
        // Hints are a convenience; hide the least important ones first as space shrinks.
        for (var i = 0; i < _hints.Children.Count; i++)
            _hints.Children[i].Visibility = width >= 1180 || i == 0 || (i == 1 && width >= 960) ? Visibility.Visible : Visibility.Collapsed;
        if (compact == _compact) return;
        _compact = compact;
        _sidebarColumn.Width = new GridLength(compact ? Ui.CompactSidebarWidth : Ui.SidebarWidth);
        foreach (var label in _filterLabels) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // Compact: the pause/resume button alone carries the state (its glyph and tooltip).
        _recordingText.Visibility = _recordingDot.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _recordingPanel.Padding = compact ? new Thickness(0, Ui.Space2, 0, 0) : new Thickness(Ui.Space3, Ui.Space3, Ui.Space1, 0);
        _recordingPanel.Margin = compact ? new Thickness(0) : new Thickness(Ui.Space1, 0, Ui.Space1, 0);
        _recordingPanel.ColumnSpacing = compact ? 0 : Ui.Space3;
        Grid.SetColumn(_pause, compact ? 0 : 2); Grid.SetColumnSpan(_pause, compact ? 3 : 1);
        _pause.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _historyColumn.MinWidth = compact ? 280 : 300;
    }

    private void UpdateEmptyState()
    {
        var empty = _rows.Count == 0;
        _emptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        var query = _search.Text.Trim();
        _emptyKeys.Visibility = Visibility.Collapsed;
        if (query.Length > 0)
        {
            _emptyGlyph.Glyph = Glyph.Search;
            _emptyTitle.Text = $"No clips found for “{(query.Length > 60 ? query[..60] + "…" : query)}”";
            _emptyBody.Text = _filter == ClipFilter.All ? "Try fewer words, or search by app or window title." : "Try another filter, fewer words, or search by app or window title.";
        }
        else if (_offset > 0)
        {
            _emptyGlyph.Glyph = Glyph.Clock; _emptyTitle.Text = "No older clips"; _emptyBody.Text = "You have reached the end of your history.";
        }
        else if (_filter != ClipFilter.All)
        {
            (_emptyGlyph.Glyph, _emptyTitle.Text, _emptyBody.Text) = _filter switch
            {
                ClipFilter.Text => (Glyph.Text, "No text clips yet", "Text you copy will appear here."),
                ClipFilter.Images => (Glyph.Image, "No images yet", "Screenshots and images you copy will appear here."),
                ClipFilter.Files => (Glyph.Files, "No files yet", "Files and folders you copy in File Explorer will appear here."),
                _ => (Glyph.Pinned, "No pinned clips", "Pin clips you reuse. Pinned clips are kept when older history is cleaned up.")
            };
        }
        else
        {
            _emptyGlyph.Glyph = Glyph.Brand;
            _emptyTitle.Text = "Your clipboard history will appear here";
            _emptyBody.Text = _settings.CaptureEnabled ? "Copy something in another app to get started." : "Recording is paused. Resume recording to start saving what you copy.";
            _emptyKeys.Visibility = _settings.CaptureEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateRecordingState()
    {
        var recording = _settings.CaptureEnabled;
        _recordingDot.Style = Ui.Style(recording ? "CsRecordingDotStyle" : "CsPausedDotStyle");
        _recordingTitle.Text = recording ? "Recording" : "Paused";
        _recordingCaption.Text = recording ? "Local only" : "New copies aren't saved";
        _pause.Content = Ui.Icon(recording ? Glyph.Pause : Glyph.Play, 14);
        var action = recording ? "Pause recording" : "Resume recording";
        AutomationProperties.SetName(_pause, action); ToolTipService.SetToolTip(_pause, action);
        ToolTipService.SetToolTip(_recordingDot, recording ? "Recording clipboard history" : "Recording is paused");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_exiting || _status is null) return;
        _statusTimer.Stop();
        var count = _rows.Count;
        var query = _search.Text.Trim();
        _status.Text = query.Length > 0
            ? count == 1 ? "1 result" : $"{count:N0}{(count == PageSize ? "+" : "")} results"
            : count == 0 ? (_settings.CaptureEnabled ? "Ready" : "Recording paused")
            : count == 1 ? "1 clip" : $"{count:N0}{(count == PageSize ? "+" : "")} clips";
        FrameworkElementAutomationPeer.FromElement(_status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        var paged = _offset > 0 || count == PageSize;
        _pager.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
        if (paged)
        {
            _pageLabel.Text = count == 0 ? "" : $"{_offset + 1:N0}–{_offset + count:N0}";
            _newer.IsEnabled = _offset > 0; _older.IsEnabled = count == PageSize;
        }
    }

    private void UpdateShortcutHints()
    {
        _searchKeys.Children.Clear();
        foreach (var key in ClipPresentation.ShortcutKeys(_settings.Shortcut)) _searchKeys.Children.Add(Ui.Keycap(key));
    }

    private void UpdateCaptionButtons()
    {
        if (_exiting) return;
        // Caption buttons follow the in-app theme choice, including overrides of the system theme.
        AppWindow.TitleBar.PreferredTheme = _root.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    private void SizeToDisplay()
    {
        var scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1280 * scale), (int)(work.Width * 0.92));
        var height = Math.Min((int)(820 * scale), (int)(work.Height * 0.92));
        AppWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        UpdateDisplayMetrics();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateDisplayMetrics();
    private void UpdateDisplayMetrics()
    {
        if (_exiting) return;
        var scale = _root.XamlRoot?.RasterizationScale ?? Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var metrics = (scale, work.Width, work.Height);
        if (metrics == _displayMetrics) return;
        _displayMetrics = metrics;
        var minimum = WindowGeometry.MinimumSize(scale, work.Width, work.Height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minimum.Width;
            presenter.PreferredMinimumHeight = minimum.Height;
        }
        _detail?.UpdateImageScale(scale);
    }

    // Neither the thread's synchronized key state (stale after the global shortcut) nor the
    // physical state (already released when a fast Ctrl+Enter is processed) is reliable here.
    private bool IsCtrlDown() => _ctrlHeld;
    private static bool IsCtrlKey(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl;

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Down or VirtualKey.Up or VirtualKey.PageDown or VirtualKey.PageUp when _rows.Count > 0 && _historySession.IsReady:
                // Command-palette navigation: the caret stays in search while the selection moves.
                var step = args.Key switch { VirtualKey.Down => 1, VirtualKey.Up => -1, VirtualKey.PageDown => 8, _ => -8 };
                _history.SelectedIndex = Math.Clamp(_history.SelectedIndex + step, 0, _rows.Count - 1);
                _history.ScrollIntoView(_history.SelectedItem);
                args.Handled = true;
                break;
            case VirtualKey.Enter:
                args.Handled = true;
                await RestoreCurrentAsync(!IsCtrlDown());
                break;
            case VirtualKey.Escape:
                args.Handled = true;
                if (_search.Text.Length > 0) _search.Text = ""; else AppWindow.Hide();
                break;
        }
    }

    private void OnShellKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var ctrl = IsCtrlDown();
        if (_settingsView is not null)
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; CloseSettings(); }
            return;
        }
        if (args.Key == VirtualKey.Escape) { args.Handled = true; AppWindow.Hide(); return; }
        if (!ctrl) return;
        switch (args.Key)
        {
            case VirtualKey.F or VirtualKey.L or VirtualKey.K:
                _search.Focus(FocusState.Keyboard); _search.SelectAll(); args.Handled = true; break;
            case VirtualKey.P:
                args.Handled = true; _ = PinAsync(); break;
            case (VirtualKey)188: // Ctrl+,
                args.Handled = true; ShowSettings(); break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number5:
                args.Handled = true; _filters.SelectedIndex = args.Key - VirtualKey.Number1; break;
        }
    }
}

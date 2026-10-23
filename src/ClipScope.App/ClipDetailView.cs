using ClipScope.App.Design;
using ClipScope.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClipScope.App;

// Selected clip: who/where/when header, actions, content preview, source snapshot and details.
internal sealed class ClipDetailView
{
    private const int MaxPreviewCharacters = 32768, MaxListedFiles = 200;
    private readonly Func<string?, string?> _resolve;
    private CancellationTokenSource _imageCancellation = new();
    private readonly List<(Image Image, int Width, int Height, double MaxHeight)> _images = [];
    private readonly Grid _root = new();
    private readonly Grid _header = new() { Padding = new Thickness(Ui.Space5, Ui.Space5, Ui.Space5, Ui.Space4), RowSpacing = Ui.Space4 };
    private readonly Border _iconTile = new() { Width = 44, Height = 44 };
    private readonly Image _icon = new() { Width = 28, Height = 28, Stretch = Stretch.Uniform };
    private readonly FontIcon _fallbackIcon = Ui.Icon(Glyph.App, 20, "CsSecondaryIconStyle");
    private readonly TextBlock _app = Ui.Text("CsHeaderTextStyle");
    private readonly TextBlock _context = Ui.Text("CsBodySecondaryTextStyle", wrap: true);
    private readonly TextBlock _meta = Ui.Text("CsTertiaryTextStyle");
    private readonly FontIcon _pinnedBadge = Ui.Icon(Glyph.Pinned, 12, "CsAccentIconStyle");
    private readonly Button _paste = new() { Style = Ui.Style("AccentButtonStyle"), Content = Ui.IconLabel(Glyph.Paste, "Paste"), MinWidth = 96 };
    private readonly Button _copy = new() { Content = Ui.IconLabel(Glyph.Copy, "Copy"), MinWidth = 88 };
    private readonly Button _pin = Ui.SubtleIconButton(Glyph.Pin, "Pin", "Pin (Ctrl+P)");
    private readonly Button _delete = Ui.SubtleIconButton(Glyph.Delete, "Delete", "Delete");
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _content = new() { Spacing = Ui.Space5, Padding = new Thickness(Ui.Space5, 0, Ui.Space5, Ui.Space5) };
    private readonly StackPanel _placeholder = new() { Spacing = Ui.Space3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = Ui.Pad(Ui.Space5) };
    private readonly TextBlock _placeholderTitle = new() { Style = Ui.Style("CsEmptyTitleTextStyle") };
    private readonly TextBlock _placeholderBody = new() { Style = Ui.Style("CsEmptyBodyTextStyle") };

    public Func<Task>? PasteRequested, CopyRequested, PinRequested, DeleteRequested;
    public FrameworkElement Root => _root;

    public ClipDetailView(Func<string?, string?> resolve)
    {
        _resolve = resolve;
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(_root, "Selected clip");
        BuildHeader();
        _scroller.Content = _content;
        Grid.SetRow(_scroller, 1); _root.Children.Add(_scroller);
        BuildPlaceholder();
        ShowEmpty(historyEmpty: true);
    }

    private void BuildHeader()
    {
        _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _header.ColumnSpacing = Ui.Space4;
        _iconTile.Style = Ui.Style("CsIconTileStyle");
        _iconTile.VerticalAlignment = VerticalAlignment.Top;
        var iconGrid = new Grid(); iconGrid.Children.Add(_fallbackIcon); iconGrid.Children.Add(_icon);
        _iconTile.Child = iconGrid;
        _header.Children.Add(_iconTile);

        var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space2 };
        _pinnedBadge.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(_pinnedBadge, "Pinned");
        titleRow.Children.Add(_app); titleRow.Children.Add(_pinnedBadge);
        _context.MaxLines = 2; _context.TextTrimming = TextTrimming.CharacterEllipsis;
        _context.IsTextSelectionEnabled = true;
        identity.Children.Add(titleRow); identity.Children.Add(_context); identity.Children.Add(_meta);
        Grid.SetColumn(identity, 1); _header.Children.Add(identity);

        var actions = new Grid { ColumnSpacing = Ui.Space2 };
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetName(_paste, "Paste");
        AutomationProperties.SetHelpText(_paste, "Pastes into the application you were using. Shortcut: Enter.");
        ToolTipService.SetToolTip(_paste, "Paste into the previous app (Enter)");
        AutomationProperties.SetName(_copy, "Copy");
        AutomationProperties.SetHelpText(_copy, "Puts this clip on the clipboard without pasting. Shortcut: Control+Enter.");
        ToolTipService.SetToolTip(_copy, "Copy to the clipboard (Ctrl+Enter)");
        _paste.Click += async (_, _) => { if (PasteRequested is { } run) await run(); };
        _copy.Click += async (_, _) => { if (CopyRequested is { } run) await run(); };
        _pin.Click += async (_, _) => { if (PinRequested is { } run) await run(); };
        _delete.Click += async (_, _) => { if (DeleteRequested is { } run) await run(); };
        Grid.SetColumn(_copy, 1); Grid.SetColumn(_pin, 3); Grid.SetColumn(_delete, 4);
        actions.Children.Add(_paste); actions.Children.Add(_copy); actions.Children.Add(_pin); actions.Children.Add(_delete);
        Grid.SetRow(actions, 1); Grid.SetColumnSpan(actions, 2); _header.Children.Add(actions);
        _root.Children.Add(_header);
    }

    private void BuildPlaceholder()
    {
        var tile = new Border { Style = Ui.Style("CsEmptyGlyphTileStyle"), Child = Ui.Icon(Glyph.Info, 22, "CsSecondaryIconStyle"), Margin = new Thickness(0, 0, 0, Ui.Space1) };
        _placeholder.Children.Add(tile);
        _placeholder.Children.Add(_placeholderTitle);
        _placeholder.Children.Add(_placeholderBody);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Ui.Space1, 0, 0) };
        keys.Children.Add(Ui.KeyHint("Browse", "Up and Down arrows browse clips", "↑", "↓"));
        keys.Children.Add(Ui.KeyHint("Paste", "Enter pastes the selected clip", "↵"));
        _placeholder.Children.Add(keys);
        Grid.SetRowSpan(_placeholder, 2);
        _root.Children.Add(_placeholder);
    }

    public void ShowEmpty(bool historyEmpty)
    {
        ResetImages();
        _icon.Source = null;
        _header.Visibility = _scroller.Visibility = Visibility.Collapsed;
        _content.Children.Clear();
        _placeholder.Visibility = Visibility.Visible;
        _placeholderTitle.Text = historyEmpty ? "Nothing selected" : "Select a clip";
        _placeholderBody.Text = historyEmpty
            ? "Each clip shows what you copied, the app and window it came from, and when."
            : "See its full content, source app and window, then paste it back.";
    }

    public void Show(Clip clip, ClipRow row)
    {
        ResetImages();
        _placeholder.Visibility = Visibility.Collapsed;
        _header.Visibility = _scroller.Visibility = Visibility.Visible;
        var app = ClipPresentation.AppName(clip.Source.AppName, clip.Source.ProcessName);
        var now = DateTimeOffset.Now;
        var timestamp = ClipPresentation.Timestamp(clip.CreatedUtc, clip.LocalOffset, now);
        _icon.Source = row.IconSource;
        _icon.Visibility = row.IconSource is null ? Visibility.Collapsed : Visibility.Visible;
        _fallbackIcon.Visibility = row.IconSource is null ? Visibility.Visible : Visibility.Collapsed;
        _app.Text = app;
        _context.Text = clip.Source.WindowTitle.Length > 0 ? clip.Source.WindowTitle : "No window title";
        _context.Opacity = clip.Source.WindowTitle.Length > 0 ? 1 : 0.7;
        ToolTipService.SetToolTip(_context, clip.Source.WindowTitle.Length > 0 ? clip.Source.WindowTitle : null);
        _pinnedBadge.Visibility = clip.IsPinned ? Visibility.Visible : Visibility.Collapsed;
        var text = clip.Kind == ClipKind.Files ? null : clip.PlainText;
        _meta.Text = string.Join(" · ", new[] { timestamp, KindLabel(clip),
            clip.Kind == ClipKind.Files ? ClipPresentation.FileSummary(clip.Files) : text is not null ? ClipPresentation.TextSummary(text) : null }.OfType<string>());
        var pinAction = clip.IsPinned ? "Unpin" : "Pin";
        _pin.Content = Ui.Icon(clip.IsPinned ? Glyph.Unpin : Glyph.Pin, 16);
        AutomationProperties.SetName(_pin, pinAction);
        ToolTipService.SetToolTip(_pin, $"{pinAction} (Ctrl+P)");
        AutomationProperties.SetName(_delete, "Delete");

        _content.Children.Clear();
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
        if (clip.Kind == ClipKind.Image && clip.PrimaryMediaPath is { } image) _content.Children.Add(ImagePreview(image));
        if (clip.Kind == ClipKind.Files) _content.Children.Add(FileList(clip.Files));
        else if (text is not null && (clip.Kind != ClipKind.Image || text.Length > 0))
        {
            var block = new TextBlock
            {
                Style = Ui.Style(ClipPresentation.LooksLikeCode(text) ? "CsCodeTextStyle" : "CsPreviewTextStyle"),
                Text = text[..Math.Min(text.Length, MaxPreviewCharacters)]
            };
            var card = new Border { Style = Ui.Style("CsCardStyle"), Padding = Ui.Pad(Ui.Space4, Ui.Space3 + 2), Child = block };
            _content.Children.Add(card);
            if (text.Length > MaxPreviewCharacters)
                _content.Children.Add(Note(Glyph.Info, "Preview shortened. Paste and Copy restore the full content."));
        }
        if (clip.ContextScreenshotPath is { } context) _content.Children.Add(SourceSnapshot(context, clip, app, timestamp, row));
        _content.Children.Add(Details(clip, app));
    }

    private static string KindLabel(Clip clip) => clip.Kind switch
    {
        ClipKind.Image => "Image",
        ClipKind.Files => clip.Files.Length == 1 ? "File" : "Files",
        _ => clip.Html is not null || clip.Rtf is not null ? "Rich text" : "Text"
    };

    private FrameworkElement ImagePreview(string relative)
    {
        var panel = new StackPanel { Spacing = Ui.Space2 };
        var frame = new Border { Style = Ui.Style("CsCardStyle"), Padding = Ui.Pad(Ui.Space3), HorizontalAlignment = HorizontalAlignment.Left };
        var caption = Ui.Text("CsTertiaryTextStyle");
        var image = LoadImage(relative, 1600, 440, "Clipboard image preview", size => caption.Text = $"{size.Width:N0} × {size.Height:N0} pixels");
        if (image is null) { panel.Children.Add(Note(Glyph.Warning, "This image is no longer available on disk.")); return panel; }
        frame.Child = image;
        caption.Text = "Image";
        panel.Children.Add(frame); panel.Children.Add(caption);
        return panel;
    }

    private static FrameworkElement FileList(string[] files)
    {
        var card = new Border { Style = Ui.Style("CsCardStyle"), Padding = Ui.Pad(Ui.Space2) };
        var list = new StackPanel { Spacing = 2 };
        foreach (var path in files.Take(MaxListedFiles))
        {
            // Categorize by name only: probing the file system here could block on network paths.
            var category = ClipPresentation.CategorizeFile(path);
            var row = new Grid { ColumnSpacing = Ui.Space3, Padding = Ui.Pad(Ui.Space2, Ui.Space2) };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var tile = new Border { Style = Ui.Style("CsIconTileStyle"), Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Child = Ui.Icon(Glyph.ForFile(category), 16, "CsSecondaryIconStyle") };
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.Text("CsBodyStrongTextStyle", name.Length > 0 ? name : path); title.IsTextSelectionEnabled = true;
            var folder = Ui.Text("CsTertiaryTextStyle", Path.GetDirectoryName(path) ?? ""); folder.IsTextSelectionEnabled = true;
            ToolTipService.SetToolTip(folder, path);
            labels.Children.Add(title); labels.Children.Add(folder);
            Grid.SetColumn(labels, 1);
            row.Children.Add(tile); row.Children.Add(labels);
            list.Children.Add(row);
        }
        if (files.Length > MaxListedFiles) list.Children.Add(Note(Glyph.Info, $"{files.Length - MaxListedFiles:N0} more items are not listed. Copy restores all of them."));
        card.Child = list;
        return card;
    }

    private FrameworkElement SourceSnapshot(string relative, Clip clip, string app, string timestamp, ClipRow row)
    {
        var section = new StackPanel { Spacing = Ui.Space2 };
        section.Children.Add(SectionLabel(Glyph.Snapshot, "Source snapshot", "Where you copied this from"));
        var card = new Border { Style = Ui.Style("CsCardStyle") };
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var image = LoadImage(relative, 1600, 380, $"Snapshot of {app}: {clip.Source.WindowTitle}");
        var stage = new Grid { Padding = Ui.Pad(Ui.Space3), Background = null };
        if (image is null) stage.Children.Add(Note(Glyph.Warning, "The snapshot file is no longer available."));
        else { image.HorizontalAlignment = HorizontalAlignment.Center; stage.Children.Add(image); }
        layout.Children.Add(stage);

        var caption = new Grid { ColumnSpacing = Ui.Space3, Padding = Ui.Pad(Ui.Space4, Ui.Space3) };
        caption.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        caption.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Image { Source = row.IconSource, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var labels = new StackPanel { Spacing = 2 };
        var title = Ui.Text("CsBodyStrongTextStyle", clip.Source.WindowTitle.Length > 0 ? clip.Source.WindowTitle : app, wrap: true); title.FontSize = 13; title.IsTextSelectionEnabled = true;
        labels.Children.Add(title);
        labels.Children.Add(Ui.Text("CsTertiaryTextStyle", $"{app} · {timestamp}"));
        Grid.SetColumn(labels, 1);
        caption.Children.Add(icon); caption.Children.Add(labels);
        var top = Ui.Divider(); top.VerticalAlignment = VerticalAlignment.Top;
        var captionHost = new Grid(); captionHost.Children.Add(caption); captionHost.Children.Add(top);
        Grid.SetRow(captionHost, 1); layout.Children.Add(captionHost);
        card.Child = layout;
        section.Children.Add(card);
        return section;
    }

    private static FrameworkElement Details(Clip clip, string app)
    {
        var section = new StackPanel { Spacing = Ui.Space2 };
        section.Children.Add(SectionLabel(Glyph.Info, "Details", null));
        var grid = new Grid { ColumnSpacing = Ui.Space4, RowSpacing = Ui.Space2, Padding = new Thickness(Ui.Space1, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var local = clip.CreatedUtc.ToOffset(clip.LocalOffset);
        var offset = clip.LocalOffset;
        var rows = new (string Key, string? Value)[]
        {
            ("Copied", $"{local:dddd, MMMM d, yyyy HH:mm:ss} (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm})"),
            ("UTC", clip.CreatedUtc.ToString("O")),
            ("Application", app),
            ("Process", clip.Source.ProcessName.Length > 0 ? $"{clip.Source.ProcessName} · PID {clip.Source.ProcessId}" : null),
            ("Path", clip.Source.ExecutablePath),
            ("Window", clip.Source.WindowTitle.Length > 0 ? clip.Source.WindowTitle : null),
            ("Monitor", clip.Source.MonitorName),
            ("Formats", clip.Formats.Length > 0 ? string.Join(", ", clip.Formats) : null),
        };
        foreach (var (key, value) in rows.Where(x => !string.IsNullOrWhiteSpace(x.Value)))
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var label = Ui.Text("CsTertiaryTextStyle", key);
            var text = Ui.Text("CsCaptionTextStyle", value!, wrap: true); text.IsTextSelectionEnabled = true;
            AutomationProperties.SetName(text, $"{key}: {value}");
            Grid.SetRow(label, grid.RowDefinitions.Count - 1); Grid.SetRow(text, grid.RowDefinitions.Count - 1); Grid.SetColumn(text, 1);
            grid.Children.Add(label); grid.Children.Add(text);
        }
        section.Children.Add(grid);
        return section;
    }

    private static Grid SectionLabel(string glyph, string title, string? trailing)
    {
        var row = new Grid { ColumnSpacing = Ui.Space2 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = Ui.Icon(glyph, 12, "CsSecondaryIconStyle");
        var label = Ui.Text("CsSectionLabelStyle", title);
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        Grid.SetColumn(label, 1); row.Children.Add(icon); row.Children.Add(label);
        if (trailing is not null) { var hint = Ui.Text("CsTertiaryTextStyle", trailing); Grid.SetColumn(hint, 2); row.Children.Add(hint); }
        return row;
    }

    private static Grid Note(string glyph, string text)
    {
        var row = new Grid { ColumnSpacing = Ui.Space2 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Ui.Icon(glyph, 12, "CsTertiaryIconStyle"); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 3, 0, 0);
        var label = Ui.Text("CsTertiaryTextStyle", text, wrap: true); Grid.SetColumn(label, 1);
        row.Children.Add(icon); row.Children.Add(label);
        return row;
    }

    private void ResetImages()
    {
        _imageCancellation.Cancel(); _imageCancellation.Dispose(); _imageCancellation = new();
        foreach (var entry in _images) entry.Image.Source = null;
        _images.Clear();
    }
    public void UpdateImageScale(double scale)
    {
        foreach (var (image, width, height, maxHeight) in _images)
        {
            image.MaxWidth = width / scale;
            image.MaxHeight = Math.Min(maxHeight, height / scale);
        }
    }
    private FrameworkElement? LoadImage(string relative, int decodeWidth, double maxHeight, string accessibleName, Action<PngPreview>? decoded = null)
    {
        var path = _resolve(relative);
        if (path is null) return null;
        var image = new Image { Stretch = Stretch.Uniform, MaxHeight = maxHeight, HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } };
        AutomationProperties.SetName(image, accessibleName);
        AutomationProperties.SetItemStatus(image, "Preview loading");
        var host = new Grid();
        var unavailable = Note(Glyph.Warning, "Image preview unavailable; the file may have been removed or damaged.");
        unavailable.Visibility = Visibility.Collapsed;
        host.Children.Add(image); host.Children.Add(unavailable);
        var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Physical };
        var ct = _imageCancellation.Token;
        // The awaited decode cannot miss an ImageOpened event from a cached URI.
        _ = LoadAsync();
        return host;
        async Task LoadAsync()
        {
            var loaded = await StoredImage.LoadAsync(bitmap, path, decodeWidth, ct);
            if (ct.IsCancellationRequested) return;
            if (loaded is null)
            {
                AutomationProperties.SetItemStatus(image, "Preview unavailable");
                image.Visibility = Visibility.Collapsed;
                unavailable.Visibility = Visibility.Visible;
                return;
            }
            _images.Add((image, loaded.Width, loaded.Height, maxHeight));
            decoded?.Invoke(loaded);
            UpdateImageScale(Math.Max(1, _root.XamlRoot?.RasterizationScale ?? 1));
            image.Source = bitmap;
            image.Opacity = 1;
            AutomationProperties.SetItemStatus(image, "Preview ready");
        }
    }
}

using System.Diagnostics;
using ClipScope.App.Design;
using ClipScope.Core;
using ClipScope.Infrastructure;
using ClipScope.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;

namespace ClipScope.App;

// In-window settings page. Edits are staged and applied together with Save, as before:
// validation, shortcut registration and startup changes roll back on failure.
internal sealed partial class MainWindow
{
    private Grid? _settingsView;
    private bool _settingsBusy, _startupRepairRequired;
    private readonly SemaphoreSlim _settingsGate = new(1);

    private void ShowSettings()
    {
        if (_exiting) return;
        AppWindow.Show(); Activate(); WindowContext.ShowApp(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (_settingsView is not null) return;
        try
        {
            _settingsView = BuildSettings();
            Grid.SetRow(_settingsView, 2); _root.Children.Add(_settingsView);
            _body.Visibility = Visibility.Collapsed;
            _searchHost.Visibility = _settingsButton.Visibility = Visibility.Collapsed;
            _titleBar.IsBackButtonVisible = true;
            _titleBar.Subtitle = "Settings";
        }
        catch (Exception e) { _log.Write("settings.open_failed", e); Notice("Settings could not be opened."); CloseSettings(); }
    }

    private void CloseSettings()
    {
        if (_settingsBusy) return;
        if (_settingsView is not null) _root.Children.Remove(_settingsView);
        _settingsView = null;
        _body.Visibility = Visibility.Visible;
        _searchHost.Visibility = _settingsButton.Visibility = Visibility.Visible;
        _titleBar.IsBackButtonVisible = false;
        _titleBar.Subtitle = "";
        ApplyAppearance(); // discards an unsaved theme preview
        _search.Focus(FocusState.Programmatic);
    }

    private Grid BuildSettings()
    {
        var original = _settings;
        var view = new Grid();
        view.ColumnDefinitions.Add(new() { Width = new GridLength(Ui.SidebarWidth + Ui.Space6) });
        view.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });

        // General
        var startup = SettingsLayout.Toggle(original.LaunchAtStartup);
        var minimize = SettingsLayout.Toggle(original.MinimizeToTray);
        var modifierButtons = new[] { (2u, "Ctrl"), (4u, "Shift"), (1u, "Alt"), (8u, "Win") }
            .Select(pair => (pair.Item1, Button: new ToggleButton { Content = pair.Item2, IsChecked = (original.Shortcut.Modifiers & pair.Item1) != 0, MinWidth = 52, Padding = Ui.Pad(Ui.Space2, 5) })).ToArray();
        var key = new TextBox { Text = ((char)original.Shortcut.Key).ToString(), MaxLength = 1, Width = 48, TextAlignment = TextAlignment.Center, CharacterCasing = CharacterCasing.Upper };
        AutomationProperties.SetName(key, "Shortcut key");
        var shortcut = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Ui.Space1 };
        foreach (var entry in modifierButtons) { AutomationProperties.SetName(entry.Button, $"{entry.Button.Content} modifier"); shortcut.Children.Add(entry.Button); }
        shortcut.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Ui.Space1, 0, Ui.Space1, 0), Style = Ui.Style("CsCaptionTextStyle") });
        shortcut.Children.Add(key);
        AutomationProperties.SetName(shortcut, "Activation shortcut");
        var general = SettingsLayout.Page("General", "How ClipScope starts and how you open it.");
        general.Children.Add(SettingsLayout.Card(Glyph.Keyboard, "Activation shortcut", "Opens clipboard history from any app. Choose one or more modifiers and a letter or number.", shortcut));
        general.Children.Add(SettingsLayout.Card(Glyph.Startup, "Start with Windows", "Starts quietly in the notification area when you sign in, so history is always recorded.", startup));
        general.Children.Add(SettingsLayout.Card(Glyph.Tray, "Keep running when closed", "Closing the window hides ClipScope to the notification area. Exit from the tray icon.", minimize));

        // Capture
        var capture = SettingsLayout.Toggle(original.CaptureEnabled);
        var screenshots = SettingsLayout.Toggle(original.ScreenshotsEnabled);
        var maxText = SettingsLayout.Number(original.MaxTextBytes / 1024, 1, 16384);
        var maxImage = SettingsLayout.Number(original.MaxImageBytes / 1048576, 1, 128);
        AutomationProperties.SetName(maxText, "Largest text to save, in KiB per format");
        AutomationProperties.SetName(maxImage, "Largest image to save, in MiB");
        var captureGroup = SettingsLayout.Page("Capture", "What ClipScope saves when you copy.");
        captureGroup.Children.Add(SettingsLayout.Card(Glyph.Brand, "Record clipboard history", "When off, nothing you copy is saved. You can also pause from the sidebar or the tray icon.", capture));
        captureGroup.Children.Add(SettingsLayout.Card(Glyph.Snapshot, "Source snapshots", "Captures only the window you copied from, so each clip shows where it came from. Snapshots can include anything else visible in that window. Off by default.", screenshots));
        captureGroup.Children.Add(SettingsLayout.Group("Limits"));
        captureGroup.Children.Add(SettingsLayout.Card(Glyph.Text, "Largest text to save", "Per clipboard format. Bigger text representations are skipped.", SettingsLayout.WithUnit(maxText, "KiB")));
        captureGroup.Children.Add(SettingsLayout.Card(Glyph.Image, "Largest image to save", "Bigger clipboard images are skipped.", SettingsLayout.WithUnit(maxImage, "MiB")));

        // Privacy
        var exclusions = new TextBox
        {
            Text = string.Join(Environment.NewLine, original.ExcludedApplications), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 120, MaxHeight = 240, MaxLength = 32768, FontFamily = new(Ui.MonoFont), FontSize = 13,
            PlaceholderText = @"C:\Program Files\KeePassXC\KeePassXC.exe" + Environment.NewLine + "1password"
        };
        ScrollViewer.SetVerticalScrollBarVisibility(exclusions, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(exclusions, "Excluded apps, one executable path or process name per line");
        var privacy = SettingsLayout.Page("Privacy", "Keep sensitive apps out of your history.");
        privacy.Children.Add(SettingsLayout.Card(Glyph.Shield, "Excluded apps",
            "One executable path or process name per line. Nothing copied from these apps is stored: no content, no window details and no snapshot.", null, exclusions));
        privacy.Children.Add(Guidance("Consider excluding password managers and credential tools such as 1Password, Bitwarden, KeePass and KeePassXC. Verify each app's executable path in Task Manager, because names vary."));
        privacy.Children.Add(SettingsLayout.Group("Snapshot protection"));
        privacy.Children.Add(SettingsLayout.Card(Glyph.Lock, "Snapshots respect exclusions and protected windows",
            "Snapshots are never taken of excluded apps, of windows Windows marks as protected from capture, or after the source window changes. Unknown source apps are skipped automatically.", null));
        privacy.Children.Add(Guidance("Adding an exclusion does not remove history that was already saved. Delete those clips individually or clear history in Storage."));

        // Storage
        var days = SettingsLayout.Number(original.RetentionDays, 1, 3650);
        var budget = SettingsLayout.Number(original.MaxMediaBytes / 1048576, 16, 102400);
        AutomationProperties.SetName(days, "Keep history for, in days");
        AutomationProperties.SetName(budget, "Image and snapshot budget, in MiB");
        var location = Ui.Text("CsCaptionTextStyle", _paths.Root); location.IsTextSelectionEnabled = true;
        location.TextTrimming = TextTrimming.CharacterEllipsis; ToolTipService.SetToolTip(location, _paths.Root);
        var open = new Button { Content = Ui.IconLabel(Glyph.OpenFolder, "Open folder") };
        AutomationProperties.SetName(open, "Open storage folder");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(_paths.Root) { UseShellExecute = true })?.Dispose(); }
            catch (Exception e) { _log.Write("storage.open_failed", e); }
        };
        var clear = new Button { Content = Ui.IconLabel(Glyph.Delete, "Clear history…") };
        AutomationProperties.SetName(clear, "Clear unpinned history");
        var clearStatus = Ui.Text("CsCaptionTextStyle", wrap: true);
        clearStatus.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(clearStatus, AutomationLiveSetting.Polite);
        void SetClearStatus(string value)
        {
            clearStatus.Visibility = Visibility.Visible; clearStatus.Text = value;
            AutomationProperties.SetName(clearStatus, value);
            FrameworkElementAutomationPeer.FromElement(clearStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        var confirm = new Button { Content = "Delete unpinned clips", Style = Ui.Style("AccentButtonStyle") };
        var confirmPanel = new StackPanel { Spacing = Ui.Space3, MaxWidth = 300 };
        confirmPanel.Children.Add(new TextBlock { Text = "Delete all unpinned clips?", Style = Ui.Style("CsBodyStrongTextStyle") });
        confirmPanel.Children.Add(new TextBlock { Text = "Their images and snapshots are deleted too. Pinned clips are kept. This can't be undone.", TextWrapping = TextWrapping.Wrap, Style = Ui.Style("CsCaptionTextStyle") });
        confirmPanel.Children.Add(confirm);
        var flyout = new Flyout { Content = confirmPanel };
        clear.Flyout = flyout;
        confirm.Click += async (_, _) =>
        {
            if (!confirm.IsEnabled || _settingsBusy) return;
            flyout.Hide(); clear.IsEnabled = confirm.IsEnabled = false; SetClearStatus("Clearing…");
            try
            {
                await Task.Run(async () =>
                {
                    foreach (var clip in (await _repository.ListForMaintenanceAsync()).Where(x => !x.IsPinned))
                        if (await _repository.DeleteUnpinnedWithMediaAsync(clip.Id) is { } references) _media.Delete(references);
                });
                SetClearStatus("Unpinned history cleared."); await RefreshAsync();
            }
            catch (Exception e) { _log.Write("storage.clear_failed", e); SetClearStatus("Could not clear history. Check storage permissions and try again."); }
            finally { clear.IsEnabled = confirm.IsEnabled = true; }
        };
        var storage = SettingsLayout.Page("Storage", "How long history is kept and where it lives. Everything stays on this PC.");
        storage.Children.Add(SettingsLayout.Card(Glyph.Calendar, "Keep history for", "Older clips are removed automatically. Pinned clips are always kept.", SettingsLayout.WithUnit(days, "days")));
        storage.Children.Add(SettingsLayout.Card(Glyph.Storage, "Image and snapshot budget", "When exceeded, the oldest unpinned media is removed first. Pinned media can exceed the budget.", SettingsLayout.WithUnit(budget, "MiB")));
        storage.Children.Add(SettingsLayout.Card(Glyph.OpenFolder, "Storage location", "Database, settings, images and snapshots. Not encrypted; protected by your Windows account and disk encryption.", open, location));
        storage.Children.Add(SettingsLayout.Group("Danger zone"));
        storage.Children.Add(SettingsLayout.Card(Glyph.Warning, "Clear unpinned history", "Permanently deletes every clip that is not pinned, with its images and snapshots.", clear, clearStatus, "CsCautionIconStyle"));

        // Appearance
        var theme = new RadioButtons { MaxColumns = 1 };
        foreach (var label in new[] { "Use system setting", "Light", "Dark" }) theme.Items.Add(label);
        theme.SelectedIndex = (int)original.Theme;
        AutomationProperties.SetName(theme, "App theme");
        theme.SelectionChanged += (_, _) =>
        {
            // Live preview; Cancel restores the saved theme.
            var preview = (AppTheme)Math.Max(0, theme.SelectedIndex);
            _root.RequestedTheme = preview switch { AppTheme.Dark => ElementTheme.Dark, AppTheme.Light => ElementTheme.Light, _ => ElementTheme.Default };
            UpdateCaptionButtons();
        };
        var appearance = SettingsLayout.Page("Appearance", "Choose how ClipScope looks.");
        appearance.Children.Add(SettingsLayout.Card(Glyph.Theme, "App theme", "Use system setting follows your Windows light or dark mode.", null, theme));

        // Navigation
        var pages = new (string Glyph, string Label, StackPanel Page)[]
        {
            (Glyph.Settings, "General", general), (Glyph.Capture, "Capture", captureGroup), (Glyph.Shield, "Privacy", privacy),
            (Glyph.Storage, "Storage", storage), (Glyph.Theme, "Appearance", appearance)
        };
        var nav = new ListView { SelectionMode = ListViewSelectionMode.Single, ItemContainerStyle = Ui.Style("CsNavItemStyle"), ItemContainerTransitions = [] };
        AutomationProperties.SetName(nav, "Settings sections");
        AutomationProperties.SetLandmarkType(nav, AutomationLandmarkType.Navigation);
        foreach (var (glyph, label, _) in pages)
        {
            var item = new ListViewItem { Content = Ui.IconLabel(glyph, label, Ui.Space3) };
            ((FontIcon)((StackPanel)item.Content).Children[0]).FontSize = 16;
            AutomationProperties.SetName(item, label);
            nav.Items.Add(item);
        }
        var navHost = new Grid { Padding = new Thickness(Ui.Space1, Ui.Space1, Ui.Space1, Ui.Space3) };
        navHost.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        navHost.RowDefinitions.Add(new() { Height = GridLength.Auto });
        navHost.Children.Add(nav);
        var about = new StackPanel { Spacing = 2, Margin = new Thickness(Ui.Space4, 0, Ui.Space3, 0) };
        about.Children.Add(Ui.Text("CsCaptionTextStyle", "ClipScope " + typeof(ClipScopeApplication).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0]));
        about.Children.Add(Ui.Text("CsTertiaryTextStyle", "Local-only clipboard history. No accounts, network or telemetry.", wrap: true));
        Grid.SetRow(about, 1); navHost.Children.Add(about);
        view.Children.Add(navHost);

        // Left-aligned like Windows Settings, with a comfortable maximum line length.
        var presenter = new ContentPresenter { ContentTransitions = [new EntranceThemeTransition { FromVerticalOffset = 12 }], HorizontalAlignment = HorizontalAlignment.Left };
        var scroller = new ScrollViewer { Content = presenter, Padding = new Thickness(Ui.Space6, Ui.Space5, Ui.Space6, Ui.Space6), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroller.SizeChanged += (_, args) => presenter.Width = Math.Max(0, Math.Min(880, args.NewSize.Width - 2 * Ui.Space6));
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedIndex < 0) return;
            presenter.Content = pages[nav.SelectedIndex].Page;
            scroller.ChangeView(null, 0, null, disableAnimation: true);
        };
        nav.SelectedIndex = 0;

        var error = new TextBlock { Style = Ui.Style("CsErrorTextStyle"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var save = new Button { Content = "Save", Style = Ui.Style("AccentButtonStyle"), MinWidth = 96 };
        var cancel = new Button { Content = "Cancel", MinWidth = 96 };
        cancel.Click += (_, _) => CloseSettings();
        var footer = new Grid { Padding = Ui.Pad(Ui.Space5, Ui.Space3), ColumnSpacing = Ui.Space2 };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var line = Ui.Divider(); line.VerticalAlignment = VerticalAlignment.Top; line.Margin = new Thickness(-Ui.Space5, -Ui.Space3, -Ui.Space5, 0);
        Grid.SetColumnSpan(line, 3);
        Grid.SetColumn(cancel, 1); Grid.SetColumn(save, 2);
        footer.Children.Add(line); footer.Children.Add(error); footer.Children.Add(cancel); footer.Children.Add(save);

        var surfaceGrid = new Grid();
        surfaceGrid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        surfaceGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        surfaceGrid.Children.Add(scroller);
        Grid.SetRow(footer, 1); surfaceGrid.Children.Add(footer);
        var surface = new Border { Style = Ui.Style("CsContentSurfaceStyle"), Child = surfaceGrid };
        Grid.SetColumn(surface, 1); view.Children.Add(surface);

        save.Click += async (_, _) =>
        {
            if (_settingsBusy) return;
            _settingsBusy = true; nav.IsEnabled = scroller.IsEnabled = save.IsEnabled = cancel.IsEnabled = false; error.Text = "";
            await _settingsGate.WaitAsync();
            try
            {
                var hotkey = new Hotkey(modifierButtons.Where(x => x.Button.IsChecked == true).Aggregate(0u, (value, x) => value | x.Item1),
                    key.Text.Length == 1 ? char.ToUpperInvariant(key.Text[0]) : 0u);
                if (!hotkey.IsValid) { error.Text = "Choose at least one modifier and a letter or number for the shortcut."; nav.SelectedIndex = 0; return; }
                var current = _settings;
                var updated = current with {
                    LaunchAtStartup = startup.IsOn, MinimizeToTray = minimize.IsOn, Shortcut = hotkey,
                    CaptureEnabled = capture.IsOn == original.CaptureEnabled ? current.CaptureEnabled : capture.IsOn, ScreenshotsEnabled = screenshots.IsOn,
                    MaxTextBytes = checked((int)maxText.Value * 1024), MaxImageBytes = checked((int)maxImage.Value * 1048576),
                    RetentionDays = checked((int)days.Value), MaxMediaBytes = checked((long)budget.Value * 1048576),
                    ExcludedApplications = exclusions.Text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Theme = (AppTheme)Math.Max(0, theme.SelectedIndex)
                };
                updated.Validate();
                await SettingsTransaction.SaveAsync(updated,
                    async () => hotkey == current.Shortcut ? null : await _native.PrepareHotkeyAsync(hotkey)
                        ?? throw new InvalidOperationException("The shortcut could not be reserved."),
                    () => !_startupRepairRequired && updated.LaunchAtStartup == current.LaunchAtStartup ? null : StartupIntegration.Prepare(updated.LaunchAtStartup),
                    value => _settingsStore.SaveAsync(value));
                _startupRepairRequired = false;
                _settings = updated; UpdateRecordingState(); UpdateShortcutHints(); UpdateEmptyState();
                _settingsBusy = false; CloseSettings(); FlashStatus("Settings saved");
                _ = Task.Run(async () =>
                {
                    var result = await _capture.CleanupAsync();
                    if (result?.PinnedBudgetExceeded == true) DispatcherQueue.TryEnqueue(() => Notice("Pinned media exceeds the storage budget. Unpin or delete clips to free space."));
                });
            }
            catch (Exception e)
            {
                _log.Write("settings.save_failed", e);
                if (e is SettingsRollbackException) _startupRepairRequired = true;
                error.Text = e is SettingsRollbackException
                    ? "Windows could not restore its startup registration. Check Start with Windows and retry Save before exiting."
                    : "Could not save settings. Check the values, shortcut availability and storage permissions.";
                FrameworkElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            finally { _settingsGate.Release(); _settingsBusy = false; nav.IsEnabled = scroller.IsEnabled = save.IsEnabled = cancel.IsEnabled = true; }
        };
        view.Loaded += (_, _) => (nav.ContainerFromIndex(0) as ListViewItem)?.Focus(FocusState.Programmatic);
        return view;
    }

    private static Grid Guidance(string text)
    {
        var row = new Grid { ColumnSpacing = Ui.Space2, Margin = new Thickness(Ui.Space1, Ui.Space1, 0, Ui.Space2), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Ui.Icon(Glyph.Info, 12, "CsTertiaryIconStyle"); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 3, 0, 0);
        var label = new TextBlock { Text = text, Style = Ui.Style("CsCaptionTextStyle"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        row.Children.Add(icon); row.Children.Add(label);
        return row;
    }
}

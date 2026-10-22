using ClipScope.Core;
using ClipScope.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClipScope.App;

internal sealed partial class MainWindow
{
    private async Task ShowSettingsAsync()
    {
        if (_settingsOpen) return; _settingsOpen = true;
        try
        {
            var original = _settings;
            var panel = new StackPanel { Spacing = 14, MinWidth = 460, MaxWidth = 560 };
            void Section(string text) => panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0,16,0,0) });
            ToggleSwitch Toggle(string label, bool value)
            { var control = new ToggleSwitch { Header = label, IsOn = value }; panel.Children.Add(control); return control; }
            NumberBox Number(string label, double value, double min, double max)
            { var control = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact }; panel.Children.Add(control); return control; }
            Section("General");
            var startup = Toggle("Launch when I sign in to Windows", original.LaunchAtStartup);
            var minimize = Toggle("Keep running in the tray when closed", original.MinimizeToTray);
            panel.Children.Add(Metadata("Global shortcut · choose modifiers and a letter or number"));
            var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var modifierBoxes = new[] { (2u, "Ctrl"), (4u, "Shift"), (1u, "Alt"), (8u, "Win") }
                .Select(pair => (pair.Item1, Box: new CheckBox { Content = pair.Item2, IsChecked = (original.Shortcut.Modifiers & pair.Item1) != 0 })).ToArray();
            foreach (var entry in modifierBoxes) modifiers.Children.Add(entry.Box);
            var key = new TextBox { Text = ((char)original.Shortcut.Key).ToString(), MaxLength = 1, Width = 50 };
            modifiers.Children.Add(key); panel.Children.Add(modifiers);
            Section("Capture");
            var capture = Toggle("Save clipboard history", original.CaptureEnabled);
            var screenshots = Toggle("Save contextual source-window screenshots", original.ScreenshotsEnabled);
            panel.Children.Add(Metadata("Screenshots are off by default. They may include information elsewhere in the source window. Exclusions always take precedence."));
            var maxText = Number("Maximum text size per representation (KiB)", original.MaxTextBytes / 1024, 1, 16384);
            var maxImage = Number("Maximum clipboard image size (MiB)", original.MaxImageBytes / 1048576, 1, 128);
            Section("Privacy");
            var exclusions = new TextBox { Header = "Excluded executable paths or process names · one per line", Text = string.Join(Environment.NewLine, original.ExcludedApplications),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 220, MaxLength = 32768 };
            panel.Children.Add(exclusions);
            panel.Children.Add(Metadata("Consider excluding 1Password, Bitwarden, KeePass, KeePassXC and credential tools. Verify each installed executable's path in Task Manager; names vary. Unknown source processes are skipped automatically."));
            Section("Storage");
            var days = Number("Retain history for this many days (pinned clips survive)", original.RetentionDays, 1, 3650);
            var budget = Number("Media storage budget (MiB; pinned media can exceed it)", original.MaxMediaBytes / 1048576, 16, 102400);
            panel.Children.Add(Metadata(_paths.Root));
            var clear = new Button { Content = "Clear unpinned history" }; var confirmClear = false;
            clear.Click += async (_, _) =>
            {
                if (!confirmClear) { clear.Content = "Confirm: delete all unpinned clips"; confirmClear = true; return; }
                clear.IsEnabled = false;
                try
                {
                    await Task.Run(async () =>
                    {
                        foreach (var clip in (await _repository.ListForMaintenanceAsync()).Where(x => !x.IsPinned))
                            if (await _repository.DeleteIfUnpinnedAsync(clip.Id)) _media.Delete(clip.MediaPaths);
                    });
                    clear.Content = "Unpinned history cleared"; await RefreshAsync();
                }
                catch (Exception e) { _log.Write("storage.clear_failed", e); clear.Content = "Could not clear history"; }
            };
            panel.Children.Add(clear);
            Section("Appearance");
            var theme = new ComboBox { Header = "Theme", ItemsSource = Enum.GetNames<AppTheme>(), SelectedIndex = (int)original.Theme, MinWidth = 180 }; panel.Children.Add(theme);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed) }; panel.Children.Add(error);
            var dialog = new ContentDialog { Title = "Settings", XamlRoot = _root.XamlRoot,
                Content = new ScrollViewer { Content = panel, MaxHeight = 540, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = _root.ActualTheme };
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                var deferral = args.GetDeferral(); args.Cancel = true;
                var shortcutChanged = false;
                try
                {
                    var shortcut = new Hotkey(modifierBoxes.Where(x => x.Box.IsChecked == true).Aggregate(0u, (value, x) => value | x.Item1),
                        key.Text.Length == 1 ? char.ToUpperInvariant(key.Text[0]) : 0u);
                    var updated = original with {
                        LaunchAtStartup = startup.IsOn, MinimizeToTray = minimize.IsOn, Shortcut = shortcut,
                        CaptureEnabled = capture.IsOn, ScreenshotsEnabled = screenshots.IsOn,
                        MaxTextBytes = checked((int)maxText.Value * 1024), MaxImageBytes = checked((int)maxImage.Value * 1048576),
                        RetentionDays = checked((int)days.Value), MaxMediaBytes = checked((long)budget.Value * 1048576),
                        ExcludedApplications = exclusions.Text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        Theme = (AppTheme)theme.SelectedIndex
                    };
                    updated.Validate();
                    if (shortcut != original.Shortcut)
                    {
                        if (!await _native.SetHotkeyAsync(shortcut)) { error.Text = "That shortcut is in use. Choose another."; return; }
                        shortcutChanged = true;
                    }
                    StartupIntegration.SetEnabled(updated.LaunchAtStartup);
                    await _settingsStore.SaveAsync(updated);
                    _settings = updated; ApplyAppearance(); UpdateRecordingState(); args.Cancel = false;
                    _ = Task.Run(async () =>
                    {
                        var result = await _capture.CleanupAsync();
                        if (result?.PinnedBudgetExceeded == true) DispatcherQueue.TryEnqueue(() => Notice("Pinned media exceeds the storage budget. Unpin or delete clips to free space."));
                    });
                }
                catch (Exception e)
                {
                    _log.Write("settings.save_failed", e);
                    if (shortcutChanged) await _native.SetHotkeyAsync(original.Shortcut);
                    try { StartupIntegration.SetEnabled(original.LaunchAtStartup); } catch (Exception rollback) { _log.Write("startup.rollback_failed", rollback); }
                    error.Text = "Could not save settings. Check the values and storage permissions.";
                }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync();
        }
        catch (Exception e) { _log.Write("settings.open_failed", e); Notice("Settings could not be opened."); }
        finally { _settingsOpen = false; }
    }
}

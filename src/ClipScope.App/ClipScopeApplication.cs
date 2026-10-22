using ClipScope.Core;
using ClipScope.Infrastructure;
using ClipScope.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClipScope.App;

internal sealed class ClipScopeApplication : Application
{
    private MainWindow? _window;
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = new DataPaths(); var log = new LocalDiagnosticLog(paths);
        log.Write("app.startup");
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        try
        {
            var settingsStore = new SettingsStore(paths, log);
            var settings = await settingsStore.LoadAsync();
            var repository = new SqliteClipRepository(paths, log);
            await Task.Run(() => repository.InitializeAsync());
            _window = new MainWindow(paths, log, settingsStore, settings, repository);
            await _window.InitializeAsync();
            if (!Environment.GetCommandLineArgs().Contains("--background") || !_window.HasTray) _window.ShowHistory();
        }
        catch (Exception error)
        {
            log.Write("app.startup_failed", error);
            var fallback = new Window { Title = "ClipScope · startup problem" };
            fallback.Content = new TextBlock { Text = "ClipScope could not start safely. Recording is stopped. Check storage permissions and the local diagnostic log before trying again.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32) };
            fallback.Activate();
            _window?.StopNative();
        }
    }
}

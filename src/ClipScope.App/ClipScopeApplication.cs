using ClipScope.Core;
using ClipScope.Infrastructure;
using ClipScope.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace ClipScope.App;

public sealed partial class ClipScopeApplication : Application, IXamlMetadataProvider
{
    private MainWindow? _window;
    private Window? _fallback;
    private IDiagnosticLog? _log;
    private XamlControlsXamlMetaDataProvider? _metadata;
    private XamlControlsXamlMetaDataProvider Metadata => _metadata ??= new();
    public IXamlType GetXamlType(Type type) => Metadata.GetXamlType(type);
    public IXamlType GetXamlType(string name) => Metadata.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => Metadata.GetXmlnsDefinitions();
    public ClipScopeApplication()
    {
        UnhandledException += (_, args) => _log?.Write("app.unhandled", args.Exception);
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = new DataPaths(); var log = new LocalDiagnosticLog(paths); _log = log;
        log.Write("app.startup");
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            log.Write("app.resources_initialized");
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
            var fallback = _fallback = new Window { Title = "ClipScope · startup problem" };
            fallback.Content = new TextBlock { Text = "ClipScope could not start safely. Recording is stopped. Check storage permissions and the local diagnostic log before trying again.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32) };
            fallback.Activate();
            _window?.StopNative();
        }
    }
}

using ClipScope.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClipScope.App;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--shutdown")) return NativeHost.RequestExitExisting() ? 0 : 1;
        using var singleInstance = new Mutex(true, @"Local\ClipScope.Instance.v1", out var created);
        if (!created)
        {
            // Sign-in must not bring an already-running instance to the foreground.
            return arguments.Contains("--background") || NativeHost.ActivateExisting() ? 0 : 1;
        }
        NativeHost.SetApplicationIdentity();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ClipScopeApplication();
        });
        return 0;
    }
}

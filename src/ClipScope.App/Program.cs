using ClipScope.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClipScope.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\ClipScope.Instance.v1", out var created);
        if (!created) { NativeHost.ActivateExisting(); return; }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ClipScopeApplication();
        });
    }
}

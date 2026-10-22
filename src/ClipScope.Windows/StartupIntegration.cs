using Microsoft.Win32;

namespace ClipScope.Windows;

public static class StartupIntegration
{
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
            key.SetValue("ClipScope", $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue("ClipScope", false);
    }
}

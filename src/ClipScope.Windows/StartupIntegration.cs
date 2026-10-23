using Microsoft.Win32;
using ClipScope.Infrastructure;

namespace ClipScope.Windows;

public static class StartupIntegration
{
    public static ISettingsChange Prepare(bool enabled) => new StartupChange(enabled);

    private sealed class StartupChange : ISettingsChange
    {
        private readonly RegistryKey _key;
        private readonly object? _previous;
        private readonly RegistryValueKind _kind;
        private bool _committed;
        public StartupChange(bool enabled)
        {
            _key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            try
            {
                _previous = _key.GetValue("ClipScope", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                _kind = _previous is null ? RegistryValueKind.String : _key.GetValueKind("ClipScope");
                Write(_key, enabled);
            }
            catch { _key.Dispose(); throw; }
        }
        public Task CommitAsync() { _committed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_committed)
                {
                    if (_previous is null) _key.DeleteValue("ClipScope", false);
                    else _key.SetValue("ClipScope", _previous, _kind);
                }
            }
            finally { _key.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        Write(key, enabled);
    }
    private static void Write(RegistryKey key, bool enabled)
    {
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
            key.SetValue("ClipScope", $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue("ClipScope", false);
    }
}

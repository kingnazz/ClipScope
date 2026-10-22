namespace ClipScope.Core;

public sealed record Hotkey(uint Modifiers = 0x0002 | 0x0004, uint Key = 0x56)
{
    public bool IsValid => Modifiers is > 0 and <= 15 && Key is >= 0x30 and <= 0x5A;
    public override string ToString() => $"{((Modifiers & 2) != 0 ? "Ctrl + " : "")}{((Modifiers & 4) != 0 ? "Shift + " : "")}{((Modifiers & 1) != 0 ? "Alt + " : "")}{((Modifiers & 8) != 0 ? "Win + " : "")}{(char)Key}";
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public bool CaptureEnabled { get; init; } = true;
    public bool ScreenshotsEnabled { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public bool LaunchAtStartup { get; init; }
    public Hotkey Shortcut { get; init; } = new();
    public int MaxTextBytes { get; init; } = 1024 * 1024;
    public int MaxImageBytes { get; init; } = 32 * 1024 * 1024;
    public int RetentionDays { get; init; } = 30;
    public long MaxMediaBytes { get; init; } = 512L * 1024 * 1024;
    public AppTheme Theme { get; init; }
    public string[] ExcludedApplications { get; init; } = [];

    public AppSettings Validate()
    {
        if (Version != 1 || Shortcut is null || !Shortcut.IsValid || !Enum.IsDefined(Theme)
            || MaxTextBytes is < 1024 or > 16 * 1024 * 1024 || MaxImageBytes is < 1024 * 1024 or > 128 * 1024 * 1024
            || RetentionDays is < 1 or > 3650 || MaxMediaBytes is < 16L * 1024 * 1024 or > 100L * 1024 * 1024 * 1024
            || ExcludedApplications is null || ExcludedApplications.Length > 256
            || ExcludedApplications.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 4096))
            throw new ArgumentException("Settings are outside the supported limits.");
        return this;
    }
}

public static class PrivacyPolicy
{
    public static bool IsExcluded(SourceContext source, AppSettings settings)
    {
        // Fail closed when the owning process cannot be identified.
        if (!source.IsResolved) return true;
        return settings.ExcludedApplications.Any(entry =>
        {
            var value = entry.Trim().Trim('"');
            if (value.Contains('\\') || value.Contains('/'))
            {
                if (source.ExecutablePath is not null) return string.Equals(value, source.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                // If access to the path is denied, conservatively exclude a matching
                // executable basename rather than silently bypassing a path rule.
                var basename = value[(Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/')) + 1)..];
                if (basename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) basename = basename[..^4];
                return string.Equals(basename, source.ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            var name = value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
            return string.Equals(name, source.ProcessName, StringComparison.OrdinalIgnoreCase);
        });
    }
}

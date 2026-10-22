using System.Text.Json;
using System.Text.RegularExpressions;
using ClipScope.Core;

namespace ClipScope.Infrastructure;

public sealed class DataPaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "clips.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Media => Path.Combine(Root, "media");
    public string Thumbnails => Path.Combine(Root, "thumbnails");
    public string Logs => Path.Combine(Root, "logs");
    public DataPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipScope"));
        foreach (var path in new[] { Root, Media, Thumbnails, Logs }) Directory.CreateDirectory(path);
    }
    public string ResolveMedia(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Media + Path.DirectorySeparatorChar, comparison)
            && !path.StartsWith(Thumbnails + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Media path is outside the application media directories.");
        return path;
    }
}

public sealed class SettingsStore(DataPaths paths, IDiagnosticLog log)
{
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(paths.Settings)) return new AppSettings();
            var text = await File.ReadAllTextAsync(paths.Settings, ct);
            return (JsonSerializer.Deserialize<AppSettings>(text) ?? throw new JsonException()).Validate();
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            log.Write("settings.invalid", e);
            // Do not silently re-enable recording if exclusions cannot be recovered.
            return new AppSettings { CaptureEnabled = false };
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        settings.Validate();
        await _gate.WaitAsync(ct);
        var temp = paths.Settings + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, Json), ct);
            File.Move(temp, paths.Settings, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); _gate.Release(); }
    }
}

public sealed partial class MediaStore(DataPaths paths, IDiagnosticLog log)
{
    public DataPaths Paths => paths;
    public async Task<string> WriteAsync(Guid id, string role, byte[] bytes, CancellationToken ct = default)
    {
        if (role is not ("content" or "context" or "thumb" or "icon")) throw new ArgumentException("Unknown media role.");
        var relative = Path.Combine(role == "thumb" ? "thumbnails" : "media", $"{id:N}-{role}.png");
        var destination = paths.ResolveMedia(relative);
        var temp = destination + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, destination, true);
            return relative;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Delete(IEnumerable<string> references)
    {
        foreach (var reference in references.Distinct())
            try { File.Delete(paths.ResolveMedia(reference)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { log.Write("media.delete_failed", e); }
    }
    public long Size(string relative)
    {
        try { return new FileInfo(paths.ResolveMedia(relative)) is { Exists: true } file ? file.Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }
    public void RemoveOrphans(ISet<string> retained, DateTimeOffset olderThan)
    {
        foreach (var folder in new[] { paths.Media, paths.Thumbnails })
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!OwnedName().IsMatch(Path.GetFileName(file)) || File.GetLastWriteTimeUtc(file) >= olderThan.UtcDateTime) continue;
            var relative = Path.GetRelativePath(paths.Root, file);
            if (!retained.Contains(relative)) Delete([relative]);
        }
    }
    [GeneratedRegex("^[0-9a-f]{32}-(content|context|thumb|icon)\\.png(\\.tmp)?$")]
    private static partial Regex OwnedName();
}

public sealed class LocalDiagnosticLog(DataPaths paths) : IDiagnosticLog
{
    private readonly object _gate = new();
    public void Write(string eventName, Exception? error = null)
    {
        // Callers use fixed event names; exception messages/stack traces may contain
        // clipboard values, filenames or window titles and are never written.
        lock (_gate)
        try
        {
            var file = Path.Combine(paths.Logs, "diagnostic.log");
            Rotate(file);
            File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {eventName} {error?.GetType().Name} {error?.HResult:X8}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Diagnostics must never take down capture. */ }
    }
    private static void Rotate(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length <= 1_048_576) return;
        for (var i = 2; i >= 1; i--)
            if (File.Exists(file + "." + i)) File.Move(file + "." + i, file + "." + (i + 1), true);
        File.Move(file, file + ".1", true);
    }
}

namespace ClipScope.Core;

public enum ClipKind { Text, Image, Files }
public enum ClipFilter { All, Text, Images, Files, Pinned }
public enum AppTheme { System, Light, Dark }

public sealed record SourceContext
{
    public string AppName { get; init; } = "Unknown application";
    public string ProcessName { get; init; } = "";
    public int ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public long WindowHandle { get; init; }
    public string WindowTitle { get; init; } = "";
    public string? MonitorName { get; init; }
    public string? IconPath { get; init; }
    public bool IsResolved => ProcessId > 0 && ProcessName.Length > 0;
    public string Identity => $"{ExecutablePath ?? ProcessName}|{ProcessId}|{WindowHandle}|{WindowTitle}";
}

public sealed record ClipboardPayload
{
    public string? PlainText { get; init; }
    // HTML is the complete CF_HTML payload, including byte-offset headers.
    public string? Html { get; init; }
    // Latin-1 is used as a reversible mapping of the native RTF bytes.
    public string? Rtf { get; init; }
    public string[] Files { get; init; } = [];
    public byte[]? Dib { get; init; }
    public string[] Formats { get; init; } = [];
    public ClipKind Kind => Files.Length > 0 ? ClipKind.Files : Dib is not null ? ClipKind.Image : ClipKind.Text;
    public bool HasContent => PlainText is not null || Html is not null || Rtf is not null || Dib is not null || Files.Length > 0;
}

public sealed record CapturedClip(uint Sequence, DateTimeOffset CapturedAt, SourceContext Source, ClipboardPayload Payload);

public sealed record Clip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan LocalOffset { get; init; }
    public uint SequenceNumber { get; init; }
    public ClipKind Kind { get; init; }
    public string? PlainText { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    public string[] Files { get; init; } = [];
    public string[] Formats { get; init; } = [];
    public string ContentHash { get; init; } = "";
    public SourceContext Source { get; init; } = new();
    public string? PrimaryMediaPath { get; init; }
    public string? ThumbnailPath { get; init; }
    public string? ContextScreenshotPath { get; init; }
    public bool IsPinned { get; init; }
    public string SearchText => string.Join('\n', new[] { PlainText, Source.AppName, Source.ProcessName, Source.WindowTitle }
        .Concat(Files).Where(x => !string.IsNullOrEmpty(x)));
    public IEnumerable<string> MediaPaths => new[] { PrimaryMediaPath, ThumbnailPath, ContextScreenshotPath, Source.IconPath }.OfType<string>();
}

public sealed record ClipSummary(Guid Id, ClipKind Kind, string Preview, DateTimeOffset CreatedUtc,
    string SourceApp, string WindowTitle, bool IsPinned, string? ThumbnailPath, string? IconPath, bool HasContextScreenshot = false)
{
    public string LocalTime => CreatedUtc.ToLocalTime().ToString("MMM d · HH:mm");
    public string TypeLabel => Kind.ToString();
    public string PinLabel => IsPinned ? "Pinned" : "";
}

public sealed record SearchRequest(string Text = "", ClipFilter Filter = ClipFilter.All, int Limit = 100, int Offset = 0);
public sealed record RetentionResult(int RemovedClips, long MediaBytes, bool PinnedBudgetExceeded);

public interface IClipRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> AddAsync(Clip clip, TimeSpan duplicateWindow, CancellationToken ct = default);
    Task<Clip?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ClipSummary>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task SetPinnedAsync(Guid id, bool pinned, CancellationToken ct = default);
    Task<bool> AttachContextAsync(Guid id, string screenshot, string thumbnail, string? icon, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> DeleteIfUnpinnedAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Clip>> ListForMaintenanceAsync(CancellationToken ct = default);
}

public interface IDiagnosticLog { void Write(string eventName, Exception? error = null); }

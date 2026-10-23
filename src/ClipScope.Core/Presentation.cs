using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipScope.Core;

public enum FileCategory { Folder, Document, Spreadsheet, Presentation, Pdf, Image, Video, Audio, Archive, Code, Executable, Text, Other }

// Pure formatting shared by the history list and detail pane. No UI dependencies,
// so behavior is covered by portable tests.
public static partial class ClipPresentation
{
    private static readonly string[] TitleSeparators = [" - ", " — ", " – ", " | "];

    public static string AppName(string? appName, string? processName = null)
        => !string.IsNullOrWhiteSpace(appName) ? appName.Trim()
            : !string.IsNullOrWhiteSpace(processName) ? processName.Trim() : "Unknown application";

    /// <summary>Compact age for history rows: Now, 4m, 3h, Yesterday, Mon, Oct 12, Oct 12, 2024.</summary>
    public static string RelativeTime(DateTimeOffset created, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = created.ToOffset(now.Offset);
        var age = now - local;
        if (age < TimeSpan.FromSeconds(45)) return "Now";
        if (age < TimeSpan.FromMinutes(60)) return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))}m";
        if (local.Date == now.Date) return $"{(int)age.TotalHours}h";
        if (local.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (local.Date > now.Date.AddDays(-7)) return local.ToString("ddd", culture);
        return local.Year == now.Year ? local.ToString("MMM d", culture) : local.ToString("MMM d, yyyy", culture);
    }

    /// <summary>Readable timestamp in the clip's original local offset: Today at 14:32.</summary>
    public static string Timestamp(DateTimeOffset createdUtc, TimeSpan offset, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = createdUtc.ToOffset(offset);
        var today = now.ToOffset(offset).Date;
        var time = local.ToString("t", culture);
        if (local.Date == today) return $"Today at {time}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday at {time}";
        return local.Year == today.Year ? $"{local.ToString("ddd, MMM d", culture)} at {time}" : $"{local.ToString("MMM d, yyyy", culture)} at {time}";
    }

    /// <summary>The document or page part of a window title, without a trailing application name.</summary>
    public static string WindowContext(string? windowTitle, string? appName)
    {
        var title = (windowTitle ?? "").Trim();
        var app = (appName ?? "").Trim();
        if (title.Length == 0 || string.Equals(title, app, StringComparison.OrdinalIgnoreCase)) return "";
        if (app.Length == 0) return title;
        foreach (var separator in TitleSeparators)
        {
            var index = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;
            var suffix = title[(index + separator.Length)..].Trim();
            // "Inbox - Outlook" and "Page - Google Chrome" for app "Chrome" both shorten.
            if (suffix.Contains(app, StringComparison.OrdinalIgnoreCase) || app.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                return title[..index].Trim();
        }
        return title;
    }

    /// <summary>Single-line preview text: collapses whitespace so rows stay scannable.</summary>
    public static string PreviewLine(string? text, int maxLength = 280)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var collapsed = Whitespace().Replace(text.Trim(), " ");
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "…";
    }

    /// <summary>A conservative heuristic for choosing a monospace preview. Prose stays proportional.</summary>
    public static bool LooksLikeCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sample = text.Length > 4096 ? text[..4096] : text;
        var lines = sample.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return false;
        if (lines.Any(x => CommandLine().IsMatch(x))) return true;
        var structural = lines.Count(x =>
        {
            var trimmed = x.Trim();
            return x.StartsWith('\t') || x.StartsWith("    ", StringComparison.Ordinal)
                || trimmed.EndsWith(';') || trimmed.EndsWith('{') || trimmed is "}" or "};" or ")" or "]"
                || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#!", StringComparison.Ordinal)
                || trimmed.StartsWith('<') && trimmed.EndsWith('>');
        });
        if (lines.Length >= 2 && structural * 2 >= lines.Length) return true;
        if (lines.Length == 1)
        {
            var line = lines[0].Trim();
            if (line.Contains(' ') && SentenceEnd().IsMatch(line) && !line.Contains(';')) return false;
            var symbols = line.Count(c => "{}[]();=<>$|&\\:".Contains(c));
            return line.Length >= 6 && (CodeToken().IsMatch(line) || symbols >= Math.Max(3, line.Length / 8));
        }
        return false;
    }

    public static string TextSummary(string text, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var lines = text.Count(c => c == '\n') + 1;
        var characters = text.Length.ToString("N0", culture);
        return lines > 1 ? $"{characters} characters · {lines.ToString("N0", culture)} lines" : $"{characters} characters";
    }

    public static string FileSummary(IReadOnlyCollection<string> files)
        => files.Count == 1 ? "1 item" : $"{files.Count.ToString("N0", CultureInfo.CurrentCulture)} items";

    public static FileCategory CategorizeFile(string path)
    {
        if (path.EndsWith('\\') || path.EndsWith('/') || Path.GetExtension(path).Length == 0) return path.Length == 0 ? FileCategory.Other : FileCategory.Folder;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".doc" or ".docx" or ".odt" or ".rtf" or ".pages" => FileCategory.Document,
            ".xls" or ".xlsx" or ".csv" or ".ods" or ".numbers" => FileCategory.Spreadsheet,
            ".ppt" or ".pptx" or ".odp" or ".key" => FileCategory.Presentation,
            ".pdf" => FileCategory.Pdf,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" or ".tif" or ".tiff" or ".ico" => FileCategory.Image,
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".wmv" or ".webm" => FileCategory.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => FileCategory.Audio,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".cab" => FileCategory.Archive,
            ".cs" or ".js" or ".ts" or ".py" or ".ps1" or ".json" or ".xml" or ".yaml" or ".yml" or ".html" or ".css" or ".sql" or ".sh" or ".cpp" or ".h" or ".go" or ".rs" or ".java" => FileCategory.Code,
            ".exe" or ".msi" or ".bat" or ".cmd" or ".lnk" => FileCategory.Executable,
            ".txt" or ".md" or ".log" or ".ini" => FileCategory.Text,
            _ => FileCategory.Other
        };
    }

    public static string[] ShortcutKeys(Hotkey shortcut)
    {
        List<string> keys = [];
        if ((shortcut.Modifiers & 2) != 0) keys.Add("Ctrl");
        if ((shortcut.Modifiers & 4) != 0) keys.Add("Shift");
        if ((shortcut.Modifiers & 1) != 0) keys.Add("Alt");
        if ((shortcut.Modifiers & 8) != 0) keys.Add("Win");
        keys.Add(((char)shortcut.Key).ToString());
        return [.. keys];
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[.!?…:]$|[.!?]\s")] private static partial Regex SentenceEnd();
    [GeneratedRegex(@"^\s*(PS [A-Z]:\\[^>]*>|\$ |>\s|\$[A-Za-z_]\w*\s*=|(sudo|git|npm|npx|dotnet|winget|docker|kubectl|curl|pip|az|gh|ssh)\s+-{0,2}\w|(Get|Set|New|Remove|Add|Invoke|Start|Stop|Test|Import|Export|Update|Select|Where|ForEach|Write|Connect|Enable|Disable|Install|Clear|Copy|Move|Rename|Restart|Resolve|ConvertTo|ConvertFrom|Measure|Sort|Find|Register|Grant)-[A-Z][A-Za-z]+(\s|$))")]
    private static partial Regex CommandLine();
    [GeneratedRegex(@"=>|::|&&|\|\||\(\)|\b(function|const|let|var|def|class|return|import|using|public|private|SELECT|FROM|WHERE)\b.*[({=;]")]
    private static partial Regex CodeToken();
}

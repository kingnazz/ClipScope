using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipScope.Core;

public static partial class ContentPolicy
{
    public static string NormalizeText(string value)
    {
        var text = value.TrimEnd('\0').Replace("\r\n", "\n").Replace('\r', '\n');
        try { return text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return text; }
    }

    public static string Hash(ClipboardPayload payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        // Preserve differences in rich representations and ordered file lists.
        Add(Encoding.UTF8.GetBytes(payload.PlainText is null ? "\0" : NormalizeText(payload.PlainText)));
        Add(Encoding.UTF8.GetBytes(payload.Html ?? "\0"));
        Add(Encoding.Latin1.GetBytes(payload.Rtf ?? "\0"));
        foreach (var path in payload.Files) Add(Encoding.UTF8.GetBytes(path));
        Add(payload.Dib ?? []);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool IsDuplicate(Clip previous, Clip incoming, TimeSpan window) =>
        previous.ContentHash == incoming.ContentHash && previous.Source.Identity == incoming.Source.Identity
        && incoming.CreatedUtc >= previous.CreatedUtc && incoming.CreatedUtc - previous.CreatedUtc <= window;

    public static string HtmlText(string html)
    {
        // CF_HTML offsets are byte offsets, never UTF-16 indices. Invalid headers
        // are tolerated for display/search only; the original representation survives.
        var fragment = html;
        var bytes = Encoding.UTF8.GetBytes(html);
        var start = Offset(html, "StartFragment");
        var end = Offset(html, "EndFragment");
        if (start >= 0 && end >= start && end <= bytes.Length)
            fragment = Encoding.UTF8.GetString(bytes, start, end - start);
        try { return System.Net.WebUtility.HtmlDecode(Tags().Replace(fragment, " ")).Trim(); }
        catch (RegexMatchTimeoutException) { return "HTML clipboard content"; }
    }

    private static int Offset(string html, string key)
    {
        var at = html.IndexOf(key + ":", StringComparison.Ordinal);
        if (at < 0) return -1;
        var tail = html.AsSpan(at + key.Length + 1);
        var end = tail.IndexOfAny('\r', '\n');
        return int.TryParse(end < 0 ? tail : tail[..end], out var value) ? value : -1;
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.None, 50)]
    private static partial Regex Tags();
}

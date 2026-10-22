using System.Runtime.InteropServices;
using System.Text;
using ClipScope.Core;

namespace ClipScope.Windows;

internal sealed class ClipboardReader
{
    internal readonly uint HtmlFormat = Native.RegisterClipboardFormat("HTML Format");
    internal readonly uint RtfFormat = Native.RegisterClipboardFormat("Rich Text Format");
    internal readonly uint RestoreFormat = Native.RegisterClipboardFormat("ClipScope.Restore.v1");
    private readonly uint _excluded = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _history = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    internal readonly byte[] Cookie = Guid.NewGuid().ToByteArray();

    public bool IsPrivate() => Native.IsClipboardFormatAvailable(_excluded)
        || (ReadBytes(_history, 4) is { Length: >= 4 } flag && BitConverter.ToUInt32(flag) == 0);
    public bool IsOwnRestore() => ReadBytes(RestoreFormat, Cookie.Length) is { } bytes && bytes.AsSpan().SequenceEqual(Cookie);

    public ClipboardPayload Read(AppSettings settings)
    {
        List<string> formats = [];
        uint format = 0;
        while ((format = Native.EnumClipboardFormats(format)) != 0 && formats.Count < 128)
        {
            var name = new StringBuilder(256);
            formats.Add(Native.GetClipboardFormatName(format, name, name.Capacity) > 0 ? name.ToString() : $"CF_{format}");
        }
        string? text = null;
        if (ReadBytes(Native.CF_UNICODETEXT, settings.MaxTextBytes) is { } unicode)
        {
            if (unicode.Length % 2 == 0) text = Encoding.Unicode.GetString(unicode).Split('\0')[0];
        }
        else if (ReadBytes(Native.CF_TEXT, settings.MaxTextBytes) is { } ansi)
        {
            // Windows normally synthesizes CF_UNICODETEXT for legacy ANSI owners.
            // Respect the system ANSI code page when synthesis is unavailable.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(ansi).Split('\0')[0];
        }
        var html = Decode(HtmlFormat, settings.MaxTextBytes, Encoding.UTF8);
        var rtf = Decode(RtfFormat, settings.MaxTextBytes, Encoding.Latin1);
        var dib = ReadBytes(Native.CF_DIBV5, settings.MaxImageBytes) ?? ReadBytes(Native.CF_DIB, settings.MaxImageBytes);
        var files = ReadFiles(settings.MaxTextBytes);
        return new() { PlainText = text, Html = html, Rtf = rtf, Dib = dib, Files = files, Formats = formats.ToArray() };
    }
    private static string? Decode(uint format, int limit, Encoding encoding) =>
        ReadBytes(format, limit) is { } bytes ? encoding.GetString(bytes).TrimEnd('\0') : null;
    internal static byte[]? ReadBytes(uint format, int limit)
    {
        if (!Native.IsClipboardFormatAvailable(format)) return null;
        var handle = Native.GetClipboardData(format);
        if (handle == 0) return null;
        var size = Native.GlobalSize(handle);
        if (size == 0 || size > (nuint)limit) return null;
        var pointer = Native.GlobalLock(handle);
        if (pointer == 0) return null;
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { Native.GlobalUnlock(handle); }
    }
    private static string[] ReadFiles(int byteLimit)
    {
        if (!Native.IsClipboardFormatAvailable(Native.CF_HDROP)) return [];
        var handle = Native.GetClipboardData(Native.CF_HDROP);
        if (handle == 0) return [];
        var count = Native.DragQueryFile(handle, uint.MaxValue, null, 0);
        if (count > 1024) return [];
        List<string> files = []; var chars = 0;
        for (uint i = 0; i < count; i++)
        {
            var length = Native.DragQueryFile(handle, i, null, 0);
            if (length is 0 or > 32767 || (chars += (int)length + 1) > byteLimit / 2) return [];
            var value = new StringBuilder((int)length + 1);
            Native.DragQueryFile(handle, i, value, (uint)value.Capacity); files.Add(value.ToString());
        }
        return files.ToArray();
    }
}

using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ClipScope.Core;

namespace ClipScope.Windows;

internal static class ClipboardWriter
{
    internal static Dictionary<uint, byte[]> Prepare(Clip clip, string? imagePath)
    {
        Dictionary<uint, byte[]> formats = [];
        if (clip.PlainText is { } text) formats[Native.CF_UNICODETEXT] = Encoding.Unicode.GetBytes(text + '\0');
        if (clip.Html is { } html) formats[Native.RegisterClipboardFormat("HTML Format")] = Encoding.UTF8.GetBytes(html + '\0');
        if (clip.Rtf is { } rtf) formats[Native.RegisterClipboardFormat("Rich Text Format")] = Encoding.Latin1.GetBytes(rtf + '\0');
        if (clip.Files.Length > 0)
        {
            var paths = Encoding.Unicode.GetBytes(string.Join('\0', clip.Files) + "\0\0");
            var bytes = new byte[20 + paths.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
            paths.CopyTo(bytes, 20); formats[Native.CF_HDROP] = bytes;
        }
        if (imagePath is not null)
        {
            using var image = Image.FromFile(imagePath);
            using var bitmap = new Bitmap(image);
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Bmp);
            formats[Native.CF_DIB] = stream.ToArray()[14..];
        }
        if (formats.Count == 0) throw new InvalidOperationException("Clip has no restorable representation.");
        return formats;
    }
    internal static bool Write(nint window, ClipboardReader reader, Dictionary<uint, byte[]> formats)
    {
        if (!Native.OpenClipboard(window)) return false;
        try
        {
            // Allocate every handle before touching the current clipboard.
            Dictionary<uint, nint> handles = [];
            try
            {
                foreach (var pair in formats.Append(new KeyValuePair<uint, byte[]>(reader.RestoreFormat, reader.Cookie)))
                {
                    var memory = Native.GlobalAlloc(0x0002, (nuint)pair.Value.Length);
                    if (memory == 0) throw new OutOfMemoryException();
                    handles[pair.Key] = memory;
                    var pointer = Native.GlobalLock(memory);
                    if (pointer == 0) throw new OutOfMemoryException();
                    try { Marshal.Copy(pair.Value, 0, pointer, pair.Value.Length); }
                    finally { Native.GlobalUnlock(memory); }
                }
                if (!Native.EmptyClipboard()) return false;
                // Mark first so a partial restoration is never captured as new history.
                foreach (var format in new[] { reader.RestoreFormat }.Concat(formats.Keys))
                {
                    if (Native.SetClipboardData(format, handles[format]) == 0) return false;
                    handles[format] = 0; // ownership transfers to the OS only on success
                }
                return true;
            }
            finally { foreach (var handle in handles.Values.Where(x => x != 0)) Native.GlobalFree(handle); }
        }
        finally { Native.CloseClipboard(); }
    }
}

public static class PasteService
{
    public static async Task<bool> PasteAsync(nint target)
    {
        if (!WindowContext.Focus(target)) return false;
        // Wait for the activating hotkey/Enter to be released; never release keys
        // belonging to the user and never inject while Shift/Alt/Win is held.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x0D }.All(key => (Native.GetAsyncKeyState(key) & 0x8000) == 0)) break;
            await Task.Delay(25);
        }
        if (Native.GetForegroundWindow() != target
            || new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x0D }.Any(key => (Native.GetAsyncKeyState(key) & 0x8000) != 0)) return false;
        static Native.Input Key(ushort key, bool up = false) => new()
        { Type = 1, Value = new Native.InputUnion { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } };
        Native.Input[] inputs = [Key(0x11), Key(0x56), Key(0x56, true), Key(0x11, true)];
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            Native.Input[] release = [Key(0x56, true), Key(0x11, true)];
            Native.SendInput((uint)release.Length, release, Marshal.SizeOf<Native.Input>());
        }
        return sent == inputs.Length;
    }
}

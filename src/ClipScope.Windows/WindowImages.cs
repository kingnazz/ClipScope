using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ClipScope.Core;

namespace ClipScope.Windows;

public static class WindowImages
{
    public static (byte[] Original, byte[] Thumbnail) DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("Unsupported DIB header.");
        var header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        var height = Math.Abs((long)rawHeight);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        if (header is < 40 or > 124 || header > dib.Length || width <= 0 || height <= 0
            || width > 32768 || height > 32768 || width * height > 40_000_000 || depth is not (1 or 4 or 8 or 16 or 24 or 32)
            || compression is not (0 or 3 or 6)) throw new InvalidDataException("Unsupported or oversized image.");
        var colors = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (colors > 256) throw new InvalidDataException("Invalid image palette.");
        if (colors == 0 && depth <= 8) colors = 1u << depth;
        var masks = header == 40 && compression is 3 or 6 ? (compression == 6 ? 16 : 12) : 0;
        var offset = checked(14 + header + masks + (int)colors * 4);
        var stride = (((long)width * depth + 31) / 32) * 4;
        if (offset >= dib.Length + 14 || stride * height > dib.Length + 14L - offset)
            throw new InvalidDataException("Invalid image data length.");
        var bmp = new byte[14 + dib.Length]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), offset); dib.CopyTo(bmp, 14);
        using var stream = new MemoryStream(bmp);
        using var image = Image.FromStream(stream, true, true);
        using var bitmap = new Bitmap(image);
        return (Png(bitmap), Thumbnail(bitmap));
    }
    public static (byte[] Original, byte[] Thumbnail)? CaptureWindow(SourceContext source)
    {
        var hwnd = (nint)source.WindowHandle;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != source.ProcessId || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || !Native.IsWindowVisible(hwnd)) return null;
        if (Native.GetWindowDisplayAffinity(hwnd, out var affinity) && affinity != 0) return null;
        if (!Native.GetWindowRect(hwnd, out var rect)) return null;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 16_000_000) return null;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!Native.PrintWindow(hwnd, dc, 2)) return null; }
            finally { graphics.ReleaseHdc(dc); }
        }
        using var visible = CropToVisibleFrame(bitmap, hwnd, rect);
        using var scaled = Scale(visible ?? bitmap, 2560);
        return (Png(scaled), Thumbnail(scaled));
    }
    // GetWindowRect includes the invisible DWM resize borders, which PrintWindow leaves black.
    private static Bitmap? CropToVisibleFrame(Bitmap bitmap, nint hwnd, Native.Rect rect)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<Native.Rect>()) != 0) return null;
        var left = Math.Max(frame.Left, rect.Left) - rect.Left; var top = Math.Max(frame.Top, rect.Top) - rect.Top;
        var right = Math.Min(frame.Right, rect.Right) - rect.Left; var bottom = Math.Min(frame.Bottom, rect.Bottom) - rect.Top;
        if (right - left <= 0 || bottom - top <= 0 || (left == 0 && top == 0 && right == bitmap.Width && bottom == bitmap.Height)) return null;
        return bitmap.Clone(new Rectangle(left, top, right - left, bottom - top), bitmap.PixelFormat);
    }
    public static byte[]? ApplicationIcon(string? executable)
    {
        if (executable is null) return null;
        using var icon = Icon.ExtractAssociatedIcon(executable);
        if (icon is null) return null;
        using var bitmap = icon.ToBitmap();
        return Png(bitmap);
    }
    private static byte[] Png(Bitmap bitmap)
    { using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    private static byte[] Thumbnail(Bitmap bitmap)
    { using var thumb = Scale(bitmap, 320); return Png(thumb); }
    private static Bitmap Scale(Bitmap source, int max)
    {
        var ratio = Math.Min(1, (double)max / Math.Max(source.Width, source.Height));
        var target = new Bitmap(Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)));
        using var graphics = Graphics.FromImage(target); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, target.Width, target.Height); return target;
    }
}

namespace ClipScope.Core;

public readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom);
public readonly record struct PixelCrop(int X, int Y, int Width, int Height);

public static class WindowGeometry
{
    // Both rectangles are physical screen pixels (including negative monitor
    // coordinates). Never expand PrintWindow's bitmap or sample the desktop.
    public static PixelCrop VisibleCrop(PixelBounds window, PixelBounds? frame)
    {
        var width = (long)window.Right - window.Left;
        var height = (long)window.Bottom - window.Top;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || width * height > 16_000_000)
            throw new ArgumentOutOfRangeException(nameof(window));
        var full = new PixelCrop(0, 0, (int)width, (int)height);
        if (frame is not { } f || f.Left < window.Left || f.Top < window.Top
            || f.Right > window.Right || f.Bottom > window.Bottom || f.Right <= f.Left || f.Bottom <= f.Top)
            return full;
        return new(f.Left - window.Left, f.Top - window.Top, f.Right - f.Left, f.Bottom - f.Top);
    }

    public static (int Width, int Height) MinimumSize(double scale, int workWidth, int workHeight)
    {
        if (!double.IsFinite(scale) || scale <= 0 || workWidth <= 0 || workHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        return ((int)Math.Min(workWidth, Math.Ceiling(820 * scale)), (int)Math.Min(workHeight, Math.Ceiling(520 * scale)));
    }
}

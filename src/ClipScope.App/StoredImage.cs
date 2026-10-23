using Microsoft.UI.Xaml.Media.Imaging;
using ClipScope.Core;

namespace ClipScope.App;

internal static class StoredImage
{
    // Decode from a short-lived, delete-sharing stream. BitmapImage retains pixels,
    // not a file URI/cache handle, so deleting a displayed clip can remove its media.
    public static async Task<PngPreview?> LoadAsync(BitmapImage bitmap, string path, int maximumSide, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[24];
            await file.ReadExactlyAsync(header, ct);
            var preview = PngPreview.Read(header, maximumSide);
            if (preview is null) return null;
            bitmap.DecodePixelWidth = preview.DecodeWidth;
            bitmap.DecodePixelHeight = preview.DecodeHeight;
            file.Position = 0;
            using var stream = file.AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            return bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 ? preview : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }
}

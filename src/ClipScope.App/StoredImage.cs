using Microsoft.UI.Xaml.Media.Imaging;

namespace ClipScope.App;

internal static class StoredImage
{
    // Decode from a short-lived, delete-sharing stream. BitmapImage retains pixels,
    // not a file URI/cache handle, so deleting a displayed clip can remove its media.
    public static async Task<bool> LoadAsync(BitmapImage bitmap, string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var stream = file.AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or OperationCanceledException or System.Runtime.InteropServices.COMException) { return false; }
    }
}

using System.Buffers.Binary;

namespace ClipScope.Core;

public sealed record PngPreview(int Width, int Height, int DecodeWidth, int DecodeHeight)
{
    public static PngPreview? Read(ReadOnlySpan<byte> header, int maximumSide)
    {
        if (maximumSide <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSide));
        if (header.Length < 24 || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(header[8..]) != 13 || !header[12..16].SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        if (width is <= 0 or > 32768 || height is <= 0 or > 32768 || (long)width * height > 40_000_000) return null;
        var ratio = Math.Min(1, (double)maximumSide / Math.Max(width, height));
        return new(width, height, Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)));
    }
}

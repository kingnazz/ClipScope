using System.Buffers.Binary;
using ClipScope.Core;

namespace ClipScope.Core.Tests;

public class PngPreviewTests
{
    [Fact]
    public void TallAndWideImagesBoundBothDecodeDimensionsWithoutUpscaling()
    {
        Assert.Equal(new PngPreview(32768, 1, 1600, 1), PngPreview.Read(Header(32768, 1), 1600));
        Assert.Equal(new PngPreview(1, 32768, 1, 1600), PngPreview.Read(Header(1, 32768), 1600));
        Assert.Equal(new PngPreview(32, 24, 32, 24), PngPreview.Read(Header(32, 24), 1600));
    }
    [Fact]
    public void InvalidOrOversizedMediaIsRejectedBeforeNativeDecode()
    {
        foreach (var size in new[] { (0, 5), (-1, 5), (5, int.MaxValue), (32768, 32768) })
            Assert.Null(PngPreview.Read(Header(size.Item1, size.Item2), 1600));
        Assert.Null(PngPreview.Read(new byte[23], 1600));
        var header = Header(100, 100); header[0] = 0;
        Assert.Null(PngPreview.Read(header, 1600));
        header = Header(100, 100); header[12] = 0;
        Assert.Null(PngPreview.Read(header, 1600));
    }
    private static byte[] Header(int width, int height)
    {
        byte[] header = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), height);
        return header;
    }
}

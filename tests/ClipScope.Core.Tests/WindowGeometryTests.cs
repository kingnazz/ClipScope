using ClipScope.Core;

namespace ClipScope.Core.Tests;

public class WindowGeometryTests
{
    [Fact]
    public void VisibleFrameCropUsesBitmapCoordinatesOnNegativeMonitor()
    {
        Assert.Equal(new PixelCrop(8, 0, 784, 592),
            WindowGeometry.VisibleCrop(new(-1608, -8, -808, 592), new(-1600, -8, -816, 584)));
    }
    [Fact]
    public void UnavailableInvalidOrExpandedDwmBoundsKeepOriginalCapture()
    {
        var window = new PixelBounds(10, 20, 810, 620);
        foreach (var frame in new PixelBounds?[] { null, new(0, 0, 820, 640), new(40, 40, 20, 20), new(40, 40, 40, 40), window })
            Assert.Equal(new PixelCrop(0, 0, 800, 600), WindowGeometry.VisibleCrop(window, frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowGeometry.VisibleCrop(new(int.MinValue, 0, int.MaxValue, 100), null));
    }
    [Theory]
    [InlineData(1, 820, 520)]
    [InlineData(1.25, 1025, 650)]
    [InlineData(1.5, 1230, 780)]
    [InlineData(2, 1640, 1040)]
    public void MinimumFollowsCurrentMonitorScale(double scale, int width, int height)
        => Assert.Equal((width, height), WindowGeometry.MinimumSize(scale, 3840, 2160));
    [Fact]
    public void MinimumNeverExceedsTheCurrentWorkArea()
        => Assert.Equal((900, 600), WindowGeometry.MinimumSize(2, 900, 600));
}

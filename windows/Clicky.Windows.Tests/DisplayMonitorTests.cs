using Clicky.Windows.Display;

namespace Clicky.Windows.Tests;

public sealed class DisplayMonitorTests
{
    [Fact]
    public void NegativeOriginMonitor_UsesHalfOpenPhysicalBounds()
    {
        DisplayMonitor displayMonitor = new("DISPLAY2", new PixelRect(-2560, -200, 0, 1240),
            new PixelRect(-2560, -200, 0, 1200), 144, 144, false);

        Assert.True(displayMonitor.Contains(-2560, -200));
        Assert.True(displayMonitor.Contains(-1, 1239));
        Assert.False(displayMonitor.Contains(0, 100));
        Assert.False(displayMonitor.Contains(-100, 1240));
        Assert.Equal(1.5, displayMonitor.DpiScaleX);
        Assert.Equal(1.5, displayMonitor.DpiScaleY);
    }

    [Fact]
    public void DifferentAxisDpi_PreservesBothScales()
    {
        DisplayMonitor displayMonitor = new("DISPLAY1", new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040), 120, 144, true);

        Assert.Equal(1.25, displayMonitor.DpiScaleX);
        Assert.Equal(1.5, displayMonitor.DpiScaleY);
    }

    [Theory]
    [InlineData(0, 96)]
    [InlineData(96, 0)]
    public void ZeroDpi_IsRejected(uint dpiX, uint dpiY)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayMonitor("DISPLAY1",
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), dpiX, dpiY, true));
    }
}

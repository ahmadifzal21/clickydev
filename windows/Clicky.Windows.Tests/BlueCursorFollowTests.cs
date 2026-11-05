using System.Windows;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;

namespace Clicky.Windows.Tests;

public sealed class BlueCursorFollowTests
{
    [Theory]
    // 150% monitor left of a negative-origin primary layout.
    [InlineData(-2560, -200, 144u, 144u, -2560, -200, 0, 0)]
    [InlineData(-2560, -200, 144u, 144u, -1060, 400, 1000, 400)]
    // Mixed per-axis DPI on a monitor above the primary.
    [InlineData(1920, -1080, 120u, 144u, 2045, -900, 100, 120)]
    public void PhysicalPixelsToOverlayDips_UsesMonitorOriginAndItsOwnDpi(
        int monitorLeft, int monitorTop, uint dpiX, uint dpiY,
        double physicalX, double physicalY, double expectedDipX, double expectedDipY)
    {
        PixelRect bounds = new(monitorLeft, monitorTop, monitorLeft + 2560, monitorTop + 1440);
        DisplayMonitor displayMonitor = new("TESTDISPLAY", bounds, bounds, dpiX, dpiY, false);

        Point overlayPoint = OverlayCoordinateMapper.PhysicalPixelsToOverlayDips(displayMonitor, physicalX, physicalY);

        Assert.Equal(expectedDipX, overlayPoint.X, 6);
        Assert.Equal(expectedDipY, overlayPoint.Y, 6);
    }

    [Fact]
    public void Spring_FirstStepPlacesCursorAtTargetWithoutAnimating()
    {
        CursorFollowSpring cursorFollowSpring = new();
        Assert.Equal(new Point(300, 200), cursorFollowSpring.Step(new Point(300, 200), 0.5));
    }

    [Fact]
    public void Spring_LagsThenOvershootsThenSettlesExactlyOnTarget()
    {
        CursorFollowSpring cursorFollowSpring = new();
        cursorFollowSpring.Step(new Point(0, 0), 0);

        double firstFrameX = cursorFollowSpring.Step(new Point(100, 0), 1.0 / 60).X;
        Assert.InRange(firstFrameX, 0.01, 99);

        double furthestX = firstFrameX;
        for (int frame = 0; frame < 120; frame++)
            furthestX = Math.Max(furthestX, cursorFollowSpring.Step(new Point(100, 0), 1.0 / 60).X);

        Assert.True(furthestX > 100, "dampingFraction 0.6 is underdamped, so the Mac cursor overshoots slightly.");
        Assert.Equal(new Point(100, 0), cursorFollowSpring.Position);
    }

    [Fact]
    public void Spring_StalledFrameDoesNotFlingCursorPastTarget()
    {
        CursorFollowSpring cursorFollowSpring = new();
        cursorFollowSpring.Step(new Point(0, 0), 0);
        double stalledFrameX = cursorFollowSpring.Step(new Point(100, 0), 30).X;
        Assert.InRange(stalledFrameX, 0, 130);
    }
}

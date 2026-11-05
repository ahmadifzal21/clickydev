using System.Windows;
using Clicky.Windows.Companion;
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

    [Theory]
    [InlineData(100, 0.6)]
    [InlineData(800, 1.0)]
    [InlineData(3000, 1.4)]
    public void FlightArc_DurationScalesWithDistanceWithinMacLimits(double distance, double expectedSeconds)
    {
        PointingFlightArc flightArc = new(new Point(0, 0), new Point(distance, 0));
        Assert.Equal(expectedSeconds, flightArc.DurationSeconds, 6);
    }

    [Fact]
    public void FlightArc_StartsAtStartArcsUpwardFacingTravelAndLandsAtRest()
    {
        PointingFlightArc flightArc = new(new Point(0, 500), new Point(1000, 500));

        PointingFlightFrame firstFrame = flightArc.FrameAt(0);
        Assert.Equal(new Point(0, 500), firstFrame.Position);
        Assert.Equal(1, firstFrame.Scale, 6);

        PointingFlightFrame midFrame = flightArc.FrameAt(flightArc.DurationSeconds / 2);
        // Apex is half the control-point lift: min(1000 * 0.2, 80) / 2 above the straight line.
        Assert.Equal(500, midFrame.Position.X, 6);
        Assert.Equal(460, midFrame.Position.Y, 6);
        Assert.Equal(90, midFrame.RotationDegrees, 6);
        Assert.Equal(1.3, midFrame.Scale, 6);

        PointingFlightFrame landedFrame = flightArc.FrameAt(flightArc.DurationSeconds);
        Assert.True(flightArc.IsFinished(flightArc.DurationSeconds));
        Assert.Equal(new Point(1000, 500), landedFrame.Position);
        Assert.Equal(PointingFlightArc.RestingRotationDegrees, landedFrame.RotationDegrees);
        Assert.Equal(1, landedFrame.Scale);
    }

    [Theory]
    [InlineData(500, 300, 508, 312)]
    [InlineData(0, 0, 20, 20)]
    [InlineData(1919, 1079, 1900, 1060)]
    public void LandingSpot_SitsBesideTargetAndStaysInsideTheOverlay(double targetX, double targetY, double expectedX, double expectedY)
    {
        Point landingSpot = PointingFlightArc.LandingSpotBesideTarget(new Point(targetX, targetY), new Size(1920, 1080));
        Assert.Equal(new Point(expectedX, expectedY), landingSpot);
    }

    [Theory]
    // 4K primary at 150%, 1280x720 screenshot: model pixels scale by 3 to physical, then / 1.5 to DIPs.
    [InlineData(0, 0, 3840, 2160, 144u, 144u, 640, 360, 1920, 1080, 1280, 720)]
    // Negative-origin 1080p monitor left of the primary at 100%, 1280x720 screenshot.
    [InlineData(-1920, -300, 1920, 1080, 96u, 96u, 100, 50, -1770, -225, 150, 75)]
    // Mixed per-axis DPI above the primary.
    [InlineData(0, -1440, 2560, 1440, 120u, 144u, 640, 360, 1280, -720, 1024, 480)]
    public void ScreenshotPixels_MapToGlobalPhysicalPixelsAndThenOverlayDips(
        int monitorLeft, int monitorTop, int monitorWidth, int monitorHeight, uint dpiX, uint dpiY,
        double screenshotX, double screenshotY, double expectedPhysicalX, double expectedPhysicalY,
        double expectedDipX, double expectedDipY)
    {
        PixelRect bounds = new(monitorLeft, monitorTop, monitorLeft + monitorWidth, monitorTop + monitorHeight);
        DisplayMonitor displayMonitor = new("TESTDISPLAY", bounds, bounds, dpiX, dpiY, false);

        Point physicalPoint = OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(
            displayMonitor, new ScreenshotPixelCoordinate(screenshotX, screenshotY), 1280, 720);
        Point overlayPoint = OverlayCoordinateMapper.PhysicalPixelsToOverlayDips(displayMonitor, physicalPoint.X, physicalPoint.Y);

        Assert.Equal(expectedPhysicalX, physicalPoint.X, 6);
        Assert.Equal(expectedPhysicalY, physicalPoint.Y, 6);
        Assert.Equal(expectedDipX, overlayPoint.X, 6);
        Assert.Equal(expectedDipY, overlayPoint.Y, 6);
        Assert.True(displayMonitor.Contains(physicalPoint.X, physicalPoint.Y));
    }

    [Theory]
    [InlineData(5000, 5000, -1, 1079)]
    [InlineData(-50, -50, -1920, 0)]
    [InlineData(1280, 720, -1, 1079)]
    public void ScreenshotPixels_OutsideTheImageClampOntoTheCapturedMonitor(
        double screenshotX, double screenshotY, double expectedPhysicalX, double expectedPhysicalY)
    {
        PixelRect bounds = new(-1920, 0, 0, 1080);
        DisplayMonitor displayMonitor = new("TESTDISPLAY", bounds, bounds, 96, 96, false);

        Point physicalPoint = OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(
            displayMonitor, new ScreenshotPixelCoordinate(screenshotX, screenshotY), 1280, 720);

        Assert.Equal(new Point(expectedPhysicalX, expectedPhysicalY), physicalPoint);
        Assert.True(displayMonitor.Contains(physicalPoint.X, physicalPoint.Y));
    }

    [Fact]
    public void ScreenshotPixels_RejectAnEmptyScreenshotSize()
    {
        PixelRect bounds = new(0, 0, 1920, 1080);
        DisplayMonitor displayMonitor = new("TESTDISPLAY", bounds, bounds, 96, 96, true);
        Assert.Throws<ArgumentOutOfRangeException>(() => OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(
            displayMonitor, new ScreenshotPixelCoordinate(1, 1), 0, 720));
    }

    [Theory]
    // Room to spare: 22 right, 6 below the mouse.
    [InlineData(100, 100, 122, 106)]
    // Right edge: flips to the left of the mouse.
    [InlineData(1800, 100, 1478, 106)]
    // Bottom edge (taskbar work area ends at 1040): goes above the mouse.
    [InlineData(100, 1000, 122, 914)]
    // Top-left corner beside a flipped bubble still clamps inside the work area.
    [InlineData(250, 50, 0, 56)]
    public void ResponseBubble_FollowsTheMacPanelOffsetsFlipsAtEdgesAndStaysInTheWorkArea(
        double mouseX, double mouseY, double expectedLeft, double expectedTop)
    {
        Rect workArea = new(0, 0, 1920, 1040);
        Point bubbleTopLeft = BlueCursorView.ResponseBubbleTopLeft(new Point(mouseX, mouseY), new Size(300, 80),
            mouseX == 250 ? new Rect(0, 0, 400, 1040) : workArea);
        Assert.Equal(new Point(expectedLeft, expectedTop), bubbleTopLeft);
    }

    [Fact]
    public void ListeningBars_PulseGentlyInSilenceAndGrowWithTheMacProfileWhenSpeaking()
    {
        for (int barIndex = 0; barIndex < 5; barIndex++)
            Assert.InRange(BlueCursorView.ListeningBarHeight(barIndex, 0, 1.23), 3, 4.5);
        // Middle bar: phase 2 * 0.35 gives an idle pulse of (sin(0.7) + 1) / 2 * 1.5 on top of the full 10.
        Assert.Equal(3 + 10 + (Math.Sin(0.7) + 1) / 2 * 1.5, BlueCursorView.ListeningBarHeight(2, 1, 0), 6);
        double edgeBarHeight = BlueCursorView.ListeningBarHeight(0, 1, 0);
        Assert.InRange(edgeBarHeight, 3 + 4, 3 + 4 + 1.5);
    }
}

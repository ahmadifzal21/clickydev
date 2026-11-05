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
}

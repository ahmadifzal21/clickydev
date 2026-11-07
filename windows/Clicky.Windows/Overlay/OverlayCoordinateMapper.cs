using System.Windows;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;

namespace Clicky.Windows.Overlay;

/// <summary>
/// The PORTING-PLAN "Coordinates" chain: screenshot pixels -> global physical virtual-screen pixels -> one overlay
/// window's DIPs. Each overlay covers exactly one monitor, so its DIP origin is the monitor's top-left and its scale
/// is that monitor's own DPI. No Y flip: every space here has a top-left origin.
/// </summary>
internal static class OverlayCoordinateMapper
{
    public static Point PhysicalPixelsToOverlayDips(DisplayMonitor overlayMonitor, double physicalX, double physicalY) =>
        new((physicalX - overlayMonitor.Bounds.Left) / overlayMonitor.DpiScaleX,
            (physicalY - overlayMonitor.Bounds.Top) / overlayMonitor.DpiScaleY);

    /// <summary>
    /// Maps a model coordinate in a downscaled screenshot of <paramref name="capturedMonitor"/> to a global physical
    /// pixel. Like the Mac, out-of-range model output is clamped onto the screenshot rather than rejected, and the
    /// result stays on the monitor's last pixel so the overlay lookup always finds this monitor.
    /// </summary>
    public static Point ScreenshotPixelsToPhysicalPixels(DisplayMonitor capturedMonitor,
        ScreenshotPixelCoordinate screenshotCoordinate, int screenshotWidthPixels, int screenshotHeightPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenshotWidthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenshotHeightPixels);
        PixelRect monitorBounds = capturedMonitor.Bounds;
        double physicalX = monitorBounds.Left + screenshotCoordinate.X * monitorBounds.Width / screenshotWidthPixels;
        double physicalY = monitorBounds.Top + screenshotCoordinate.Y * monitorBounds.Height / screenshotHeightPixels;
        return new Point(Math.Clamp(physicalX, monitorBounds.Left, monitorBounds.Right - 1),
            Math.Clamp(physicalY, monitorBounds.Top, monitorBounds.Bottom - 1));
    }
}

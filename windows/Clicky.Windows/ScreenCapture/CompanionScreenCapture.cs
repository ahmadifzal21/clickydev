using Clicky.Windows.Display;

namespace Clicky.Windows.ScreenCapture;

/// <summary>How one monitor's pixels were read, kept so the debug screenshots can say which path ran.</summary>
internal enum ScreenCaptureMethod
{
    /// <summary>Windows Graphics Capture: reads what the compositor shows (GPU-drawn apps, HDR).</summary>
    WindowsGraphicsCapture,

    /// <summary>GDI BitBlt from the screen DC, used when WGC is unavailable or fails for that monitor.</summary>
    GdiBitBlt,
}

/// <summary>
/// One monitor's screenshot (port of the Mac CompanionScreenCapture struct). The monitor's physical bounds and the
/// screenshot size are what OverlayCoordinateMapper needs to turn a [POINT] in this image into a screen position.
/// </summary>
internal sealed record CompanionScreenCapture(
    byte[] JpegImageBytes,
    string Label,
    bool IsCursorScreen,
    DisplayMonitor CapturedMonitor,
    int ScreenshotWidthPixels,
    int ScreenshotHeightPixels,
    ScreenCaptureMethod CaptureMethod,
    TimeSpan CaptureDuration);

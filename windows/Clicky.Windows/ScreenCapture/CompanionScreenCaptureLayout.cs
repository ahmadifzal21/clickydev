using Clicky.Windows.Display;

namespace Clicky.Windows.ScreenCapture;

/// <summary>
/// The window-free parts of CompanionScreenCaptureUtility.swift: screenshot size, cursor-screen-first order and the
/// labels the model sees. Kept identical to the Mac so prompts and [POINT] coordinates mean the same thing.
/// </summary>
internal static class CompanionScreenCaptureLayout
{
    /// <summary>Longest side of every screenshot sent to the model (Mac: maxDimension = 1280).</summary>
    public const int ScreenshotMaximumDimensionPixels = 1280;

    /// <summary>JPEG quality, the Mac's compressionFactor 0.8.</summary>
    public const float JpegQuality = 0.8f;

    /// <summary>
    /// Mac sizing: the longest side becomes 1280 and the other side is truncated from the aspect ratio, so a
    /// 1920x1080 monitor gives 1280x720 and a 1080x1920 portrait monitor gives 720x1280.
    /// </summary>
    public static (int WidthPixels, int HeightPixels) ComputeScreenshotSize(int monitorWidthPixels, int monitorHeightPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monitorWidthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monitorHeightPixels);
        double aspectRatio = (double)monitorWidthPixels / monitorHeightPixels;
        if (monitorWidthPixels >= monitorHeightPixels)
        {
            return (ScreenshotMaximumDimensionPixels, Math.Max(1, (int)(ScreenshotMaximumDimensionPixels / aspectRatio)));
        }

        return (Math.Max(1, (int)(ScreenshotMaximumDimensionPixels * aspectRatio)), ScreenshotMaximumDimensionPixels);
    }

    /// <summary>The monitor under the cursor first, the rest in catalog order (the Mac's stable sort).</summary>
    public static IReadOnlyList<DisplayMonitor> OrderCursorScreenFirst(IReadOnlyList<DisplayMonitor> monitors,
        double cursorPhysicalX, double cursorPhysicalY) =>
        monitors.OrderBy(monitor => monitor.Contains(cursorPhysicalX, cursorPhysicalY) ? 0 : 1).ToList();

    /// <summary>Label text copied verbatim from the Mac, including its em dashes.</summary>
    public static string BuildScreenLabel(int screenIndex, int screenCount, bool isCursorScreen)
    {
        if (screenCount == 1) return "user's screen (cursor is here)";
        if (isCursorScreen) return $"screen {screenIndex + 1} of {screenCount} — cursor is on this screen (primary focus)";
        return $"screen {screenIndex + 1} of {screenCount} — secondary screen";
    }
}

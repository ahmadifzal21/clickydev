using System.Diagnostics;
using System.Windows;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;

namespace Clicky.Windows.Overlay;

/// <summary>One stop of the tray's "Test pointing" tour, already in global physical pixels.</summary>
internal readonly record struct PointingTestTarget(Point PhysicalPoint, string Label);

/// <summary>
/// Tray "Test pointing": flies to known spots on every monitor so pointing can be checked at the user's real
/// scaling and layout without an AI round trip. Spots go through the same screenshot-pixel mapping the model's
/// [POINT] tags will use, so that path is exercised too.
/// </summary>
internal sealed class PointingTestTour(CursorOverlayManager cursorOverlayManager, DisplayMonitorCatalog displayMonitorCatalog)
{
    /// <summary>Screenshots are downscaled to this longest side before the model sees them (PORTING-PLAN step 1).</summary>
    private const int ScreenshotMaximumDimensionPixels = 1280;
    private const int CornerInsetScreenshotPixels = 40;
    private bool isRunning;

    /// <summary>Top-left, centre and bottom-right of each monitor, labelled "screenN spot" in catalog order.</summary>
    public static IReadOnlyList<PointingTestTarget> BuildTargets(IReadOnlyList<DisplayMonitor> monitors)
    {
        List<PointingTestTarget> targets = [];
        for (int monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
        {
            DisplayMonitor monitor = monitors[monitorIndex];
            double downscale = (double)ScreenshotMaximumDimensionPixels / Math.Max(monitor.Bounds.Width, monitor.Bounds.Height);
            int screenshotWidth = (int)Math.Round(monitor.Bounds.Width * downscale);
            int screenshotHeight = (int)Math.Round(monitor.Bounds.Height * downscale);
            (double X, double Y, string Spot)[] spots =
            [
                (CornerInsetScreenshotPixels, CornerInsetScreenshotPixels, "top-left"),
                (screenshotWidth / 2.0, screenshotHeight / 2.0, "centre"),
                (screenshotWidth - CornerInsetScreenshotPixels, screenshotHeight - CornerInsetScreenshotPixels, "bottom-right"),
            ];
            foreach ((double x, double y, string spot) in spots)
            {
                Point physicalPoint = OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(
                    monitor, new ScreenshotPixelCoordinate(x, y), screenshotWidth, screenshotHeight);
                targets.Add(new PointingTestTarget(physicalPoint, $"screen{monitorIndex + 1} {spot}"));
            }
        }
        return targets;
    }

    /// <summary>Runs the tour once; clicks while it is running are ignored. Each stop finishes before the next.</summary>
    public async void Run()
    {
        if (isRunning) return;
        isRunning = true;
        try
        {
            cursorOverlayManager.ShowTransiently();
            foreach (PointingTestTarget target in BuildTargets(displayMonitorCatalog.Monitors))
            {
                cursorOverlayManager.PointAt(target.PhysicalPoint.X, target.PhysicalPoint.Y, target.Label);
                // Continuations resume on the UI thread, where the manager lives.
                while (cursorOverlayManager.IsPointing) await Task.Delay(100);
                // "Show Clicky" turned off mid-tour hides the overlays; stop instead of pointing at nothing.
                if (!cursorOverlayManager.IsOverlayVisible) return;
            }
            cursorOverlayManager.ScheduleTransientHide();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Clicky pointing test stopped: {exception.Message}");
        }
        finally
        {
            isRunning = false;
        }
    }
}

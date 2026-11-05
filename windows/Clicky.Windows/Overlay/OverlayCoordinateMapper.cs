using System.Windows;
using Clicky.Windows.Display;

namespace Clicky.Windows.Overlay;

/// <summary>
/// Converts Win32 physical virtual-screen pixels into one overlay window's DIPs. Each overlay covers exactly
/// one monitor, so its DIP origin is the monitor's top-left and its scale is that monitor's own DPI.
/// </summary>
internal static class OverlayCoordinateMapper
{
    public static Point PhysicalPixelsToOverlayDips(DisplayMonitor overlayMonitor, double physicalX, double physicalY) =>
        new((physicalX - overlayMonitor.Bounds.Left) / overlayMonitor.DpiScaleX,
            (physicalY - overlayMonitor.Bounds.Top) / overlayMonitor.DpiScaleY);
}

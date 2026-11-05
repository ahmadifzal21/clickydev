using Clicky.Windows.Display;

namespace Clicky.Windows.Tray;

/// <summary>Which edge of a monitor the taskbar (and so the tray icon) sits on.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right
}

/// <summary>
/// Pure placement math for the companion panel, kept free of Win32 so it can be unit tested.
/// Everything is in physical pixels. The Mac centers the panel under the menu bar item; on
/// Windows the taskbar can be on any edge of any monitor, so the panel hugs whichever edge the
/// taskbar is on and centers on the tray click along that edge, clamped inside the work area.
/// </summary>
internal static class CompanionPanelPlacement
{
    /// <summary>
    /// Works out the taskbar edge from how the work area is inset from the monitor bounds.
    /// When there's no inset (auto-hide taskbar, or a monitor without one), the tray click must
    /// have happened at an edge, so the edge nearest the click is used.
    /// </summary>
    public static TaskbarEdge DetectTaskbarEdge(PixelRect monitorBounds, PixelRect workArea, int anchorX, int anchorY)
    {
        if (workArea.Bottom < monitorBounds.Bottom) return TaskbarEdge.Bottom;
        if (workArea.Top > monitorBounds.Top) return TaskbarEdge.Top;
        if (workArea.Left > monitorBounds.Left) return TaskbarEdge.Left;
        if (workArea.Right < monitorBounds.Right) return TaskbarEdge.Right;

        int distanceToBottom = monitorBounds.Bottom - anchorY;
        int distanceToTop = anchorY - monitorBounds.Top;
        int distanceToLeft = anchorX - monitorBounds.Left;
        int distanceToRight = monitorBounds.Right - anchorX;
        int nearestDistance = Math.Min(Math.Min(distanceToBottom, distanceToTop), Math.Min(distanceToLeft, distanceToRight));

        if (nearestDistance == distanceToBottom) return TaskbarEdge.Bottom;
        if (nearestDistance == distanceToTop) return TaskbarEdge.Top;
        return nearestDistance == distanceToLeft ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>
    /// Returns the panel's top-left corner. The panel sits <paramref name="gapFromTaskbar"/>
    /// away from the taskbar and is centered on the anchor along the taskbar, but never leaves
    /// the work area (keeping <paramref name="gapFromTaskbar"/> from its other edges too).
    /// </summary>
    public static (int Left, int Top) CalculatePanelTopLeft(
        PixelRect workArea,
        TaskbarEdge taskbarEdge,
        int anchorX,
        int anchorY,
        int panelWidth,
        int panelHeight,
        int gapFromTaskbar)
    {
        int centeredLeft = ClampInsideWorkArea(anchorX - panelWidth / 2, workArea.Left, workArea.Right, panelWidth, gapFromTaskbar);
        int centeredTop = ClampInsideWorkArea(anchorY - panelHeight / 2, workArea.Top, workArea.Bottom, panelHeight, gapFromTaskbar);

        return taskbarEdge switch
        {
            TaskbarEdge.Bottom => (centeredLeft, workArea.Bottom - panelHeight - gapFromTaskbar),
            TaskbarEdge.Top => (centeredLeft, workArea.Top + gapFromTaskbar),
            TaskbarEdge.Left => (workArea.Left + gapFromTaskbar, centeredTop),
            TaskbarEdge.Right => (workArea.Right - panelWidth - gapFromTaskbar, centeredTop),
            _ => throw new ArgumentOutOfRangeException(nameof(taskbarEdge), taskbarEdge, "Unknown taskbar edge")
        };
    }

    private static int ClampInsideWorkArea(int desiredStart, int workAreaStart, int workAreaEnd, int panelLength, int gap)
    {
        int smallestStart = workAreaStart + gap;
        int largestStart = workAreaEnd - panelLength - gap;

        // A panel bigger than the work area: pin it to the start rather than throwing.
        return largestStart < smallestStart ? smallestStart : Math.Clamp(desiredStart, smallestStart, largestStart);
    }
}

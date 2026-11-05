using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clicky.Windows.Display;

namespace Clicky.Windows.Overlay;

/// <summary>
/// A monitor-sized WPF host whose native styles let input reach the user's app even over drawn pixels.
/// Physical bounds stay with the HWND; WPF handles the content's per-monitor DIP scale.
/// </summary>
internal sealed class CursorOverlayWindow : Window
{
    private readonly DisplayMonitor displayMonitor;
    private readonly Action requestMonitorRefresh;
    private readonly BlueCursorView blueCursorView = new();
    private HwndSource? overlayHwndSource;
    private IntPtr overlayWindowHandle;

    public CursorOverlayWindow(DisplayMonitor displayMonitor, Action requestMonitorRefresh)
    {
        this.displayMonitor = displayMonitor;
        this.requestMonitorRefresh = requestMonitorRefresh;
        Title = $"Clicky cursor overlay - {displayMonitor.DeviceName}";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Width = displayMonitor.Bounds.Width / displayMonitor.DpiScaleX;
        Height = displayMonitor.Bounds.Height / displayMonitor.DpiScaleY;
        Content = blueCursorView;
    }

    public DisplayMonitor DisplayMonitor => displayMonitor;

    public bool IsPointing => blueCursorView.IsPointing;

    /// <summary>
    /// Moves the blue cursor for this frame; it is drawn while the mouse is on this monitor (unless another overlay
    /// is pointing) or while this overlay is pointing.
    /// </summary>
    public void FollowMouse(double physicalMouseX, double physicalMouseY, bool isAnotherOverlayPointing, double elapsedSeconds)
    {
        Point mouseInOverlayDips = OverlayCoordinateMapper.PhysicalPixelsToOverlayDips(displayMonitor, physicalMouseX, physicalMouseY);
        blueCursorView.FollowMouse(mouseInOverlayDips, displayMonitor.Contains(physicalMouseX, physicalMouseY),
            isAnotherOverlayPointing, elapsedSeconds);
    }

    /// <summary>Flies the buddy to a physical-pixel spot on this monitor and shows <paramref name="bubbleText"/> there.</summary>
    public void PointAt(double physicalTargetX, double physicalTargetY, string? bubbleText)
    {
        Point targetInOverlayDips = OverlayCoordinateMapper.PhysicalPixelsToOverlayDips(displayMonitor, physicalTargetX, physicalTargetY);
        Size overlaySizeDips = new(displayMonitor.Bounds.Width / displayMonitor.DpiScaleX,
            displayMonitor.Bounds.Height / displayMonitor.DpiScaleY);
        blueCursorView.PointAt(targetInOverlayDips, overlaySizeDips, bubbleText);
    }

    public void CancelPointing() => blueCursorView.CancelPointing();

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        overlayWindowHandle = new WindowInteropHelper(this).Handle;
        overlayHwndSource = HwndSource.FromHwnd(overlayWindowHandle);
        overlayHwndSource.AddHook(HandleWindowMessage);

        int extendedWindowStyle = GetWindowLong(overlayWindowHandle, GWL_EXSTYLE);
        int previousWindowStyle = SetWindowLong(overlayWindowHandle, GWL_EXSTYLE,
            extendedWindowStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        if (previousWindowStyle == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the Clicky overlay click-through.");

        // Establish exclusion before showing the HWND so screenshots never capture an unprotected overlay.
        if (!SetWindowDisplayAffinity(overlayWindowHandle, WDA_EXCLUDEFROMCAPTURE))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not exclude the Clicky overlay from screen capture.");

        PixelRect monitorBounds = displayMonitor.Bounds;
        if (!SetWindowPos(overlayWindowHandle, HWND_TOPMOST, monitorBounds.Left, monitorBounds.Top,
                monitorBounds.Width, monitorBounds.Height, SWP_NOACTIVATE | SWP_FRAMECHANGED))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the Clicky overlay on its monitor.");
    }

    /// <summary>Restores compositor ordering after another app foregrounds its topmost/fullscreen window.</summary>
    public void RaiseWithoutActivating()
    {
        if (!SetWindowPos(overlayWindowHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the Clicky overlay's topmost position.");
    }

    private IntPtr HandleWindowMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_DPICHANGED)
        {
            requestMonitorRefresh();
            // WPF still needs this message to update its content scale before the catalog replaces this window.
        }
        else if (message == WM_WINDOWPOSCHANGING)
        {
            NativeWindowPosition windowPosition = Marshal.PtrToStructure<NativeWindowPosition>(lParam);
            PixelRect monitorBounds = displayMonitor.Bounds;
            // WPF's startup/DPI resizing must not replace physical monitor bounds with scaled DIP bounds.
            if ((windowPosition.Flags & SWP_NOMOVE) == 0)
            {
                windowPosition.Left = monitorBounds.Left;
                windowPosition.Top = monitorBounds.Top;
            }
            if ((windowPosition.Flags & SWP_NOSIZE) == 0)
            {
                windowPosition.Width = monitorBounds.Width;
                windowPosition.Height = monitorBounds.Height;
            }
            Marshal.StructureToPtr(windowPosition, lParam, false);
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        overlayHwndSource?.RemoveHook(HandleWindowMessage);
        overlayHwndSource = null;
        overlayWindowHandle = IntPtr.Zero;
        base.OnClosed(eventArgs);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_DPICHANGED = 0x02E0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowPosition
    {
        public IntPtr WindowHandle;
        public IntPtr InsertAfter;
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint displayAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int left, int top, int width, int height, uint flags);
}

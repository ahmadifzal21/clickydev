using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;

namespace Clicky.Windows.Tests;

public sealed class CursorOverlayWindowTests
{
    [Fact]
    public void ShownOverlay_CoversPhysicalMonitorWithoutActivatingAndIsExcludedFromCapture()
    {
        RunOnStaThread(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            IntPtr originalActiveWindow = GetActiveWindow();
            foreach (DisplayMonitor displayMonitor in displayMonitorCatalog.Monitors)
            {
                CursorOverlayWindow cursorOverlayWindow = new(displayMonitor, displayMonitorCatalog.RequestRefresh);
                try
                {
                    cursorOverlayWindow.Show();
                    DrainDispatcher();
                    IntPtr windowHandle = new WindowInteropHelper(cursorOverlayWindow).Handle;
                    Assert.True(IsWindowVisible(windowHandle));
                    Assert.Equal(displayMonitor.Bounds, ReadWindowBounds(windowHandle));
                    int expectedStyles = 0x08000000 | 0x00080000 | 0x00000080 | 0x00000020 | 0x00000008;
                    Assert.Equal(expectedStyles, GetWindowLong(windowHandle, -20) & expectedStyles);
                    Assert.Equal(originalActiveWindow, GetActiveWindow());
                    Assert.NotEqual(windowHandle, GetForegroundWindow());
                    Assert.True(GetWindowDisplayAffinity(windowHandle, out uint displayAffinity));
                    Assert.Equal(0x11u, displayAffinity);
                    Assert.False(cursorOverlayWindow.IsHitTestVisible);
                    Assert.False(cursorOverlayWindow.Focusable);
                }
                finally
                {
                    cursorOverlayWindow.Close();
                }
            }
        });
    }

    [Fact]
    public void DpiChange_RequestsMonitorRefreshAndKeepsPhysicalBounds()
    {
        RunOnStaThread(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            DisplayMonitor displayMonitor = displayMonitorCatalog.Monitors[0];
            int refreshRequestCount = 0;
            CursorOverlayWindow cursorOverlayWindow = new(displayMonitor, () => refreshRequestCount++);
            IntPtr suggestedBoundsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            try
            {
                cursorOverlayWindow.Show();
                IntPtr windowHandle = new WindowInteropHelper(cursorOverlayWindow).Handle;
                refreshRequestCount = 0;
                Marshal.StructureToPtr(new NativeRect { Left = 10, Top = 20, Right = 310, Bottom = 420 }, suggestedBoundsPointer, false);
                SendMessage(windowHandle, 0x02E0, new IntPtr(144 | (144 << 16)), suggestedBoundsPointer);
                DrainDispatcher();
                Assert.True(refreshRequestCount > 0);
                Assert.Equal(displayMonitor.Bounds, ReadWindowBounds(windowHandle));
            }
            finally
            {
                Marshal.FreeHGlobal(suggestedBoundsPointer);
                cursorOverlayWindow.Close();
            }
        });
    }

    [Fact]
    public void RaiseWithoutActivating_RestoresTopmostAfterNativeDemotion()
    {
        RunOnStaThread(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            CursorOverlayWindow cursorOverlayWindow = new(displayMonitorCatalog.Monitors[0], displayMonitorCatalog.RequestRefresh);
            try
            {
                cursorOverlayWindow.Show();
                IntPtr windowHandle = new WindowInteropHelper(cursorOverlayWindow).Handle;
                IntPtr originalActiveWindow = GetActiveWindow();
                Assert.True(SetWindowPos(windowHandle, new IntPtr(-2), 0, 0, 0, 0, 0x0013));
                Assert.Equal(0, GetWindowLong(windowHandle, -20) & 0x00000008);
                cursorOverlayWindow.RaiseWithoutActivating();
                Assert.Equal(0x00000008, GetWindowLong(windowHandle, -20) & 0x00000008);
                Assert.Equal(originalActiveWindow, GetActiveWindow());
                Assert.NotEqual(windowHandle, GetForegroundWindow());
            }
            finally
            {
                cursorOverlayWindow.Close();
            }
        });
    }

    [Theory]
    [InlineData(-2560, -200, 2560, 1440, 144, 144)]
    [InlineData(1920, -1080, 1920, 1080, 120, 144)]
    public void NativeResize_CannotReplaceMonitorPhysicalBounds(int left, int top, int width, int height, uint dpiX, uint dpiY)
    {
        RunOnStaThread(() =>
        {
            PixelRect monitorBounds = new(left, top, left + width, top + height);
            DisplayMonitor displayMonitor = new("TESTDISPLAY", monitorBounds, monitorBounds, dpiX, dpiY, false);
            CursorOverlayWindow cursorOverlayWindow = new(displayMonitor, () => { });
            try
            {
                cursorOverlayWindow.Show();
                IntPtr windowHandle = new WindowInteropHelper(cursorOverlayWindow).Handle;
                Assert.True(SetWindowPos(windowHandle, IntPtr.Zero, 10, 20, 30, 40, 0x0014));
                DrainDispatcher();
                Assert.Equal(monitorBounds, ReadWindowBounds(windowHandle));
            }
            finally
            {
                cursorOverlayWindow.Close();
            }
        });
    }

    [Fact]
    public void FollowMouse_DrawsTriangleWithMacOffsetOnlyOnTheMouseMonitor()
    {
        RunOnStaThread(() =>
        {
            PixelRect monitorBounds = new(-2560, -200, 0, 1240);
            DisplayMonitor displayMonitor = new("TESTDISPLAY", monitorBounds, monitorBounds, 144, 144, false);
            CursorOverlayWindow cursorOverlayWindow = new(displayMonitor, () => { });
            try
            {
                cursorOverlayWindow.Show();
                BlueCursorView blueCursorView = Assert.IsType<BlueCursorView>(cursorOverlayWindow.Content);

                cursorOverlayWindow.FollowMouse(-1060, 400, false, 0);
                Assert.Equal(Visibility.Visible, blueCursorView.BlueCursorTriangle.Visibility);
                Assert.Equal(1000 + 35, Canvas.GetLeft(blueCursorView.BlueCursorTriangle), 6);
                Assert.Equal(400 + 25, Canvas.GetTop(blueCursorView.BlueCursorTriangle), 6);

                cursorOverlayWindow.FollowMouse(100, 100, false, 1.0 / 60);
                Assert.Equal(Visibility.Hidden, blueCursorView.BlueCursorTriangle.Visibility);
            }
            finally
            {
                cursorOverlayWindow.Close();
            }
        });
    }

    [Fact]
    public void PointAt_FliesToTargetSpeaksHoldsAndFliesBackToTheMouse()
    {
        RunOnStaThread(() =>
        {
            BlueCursorView blueCursorView = new();
            const double frameSeconds = 1.0 / 60;
            Point mouse = new(100, 100);
            blueCursorView.FollowMouse(mouse, true, false, 0);

            blueCursorView.PointAt(new Point(1500, 700), new Size(1920, 1080), "save");
            Assert.True(blueCursorView.IsPointing);
            for (double seconds = 0; seconds < 1.5; seconds += frameSeconds)
                blueCursorView.FollowMouse(mouse, false, false, frameSeconds);
            // Landed beside the target and visible even though the mouse is not on this monitor.
            Assert.Equal(1508, Canvas.GetLeft(blueCursorView.BlueCursorTriangle), 6);
            Assert.Equal(712, Canvas.GetTop(blueCursorView.BlueCursorTriangle), 6);
            Assert.Equal(Visibility.Visible, blueCursorView.BlueCursorTriangle.Visibility);
            Assert.Equal(Visibility.Visible, blueCursorView.PointingBubble.Visibility);

            for (double seconds = 0; seconds < 1; seconds += frameSeconds)
                blueCursorView.FollowMouse(mouse, false, false, frameSeconds);
            Assert.Equal("save", blueCursorView.PointingBubbleText.Text);
            Assert.True(blueCursorView.IsPointing);

            // Hold 3 s + fade 0.5 s + return flight of at most 1.4 s.
            for (double seconds = 0; seconds < 5 && blueCursorView.IsPointing; seconds += frameSeconds)
                blueCursorView.FollowMouse(mouse, false, false, frameSeconds);
            Assert.False(blueCursorView.IsPointing);
            Assert.Equal(Visibility.Hidden, blueCursorView.PointingBubble.Visibility);
            Assert.Equal(135, Canvas.GetLeft(blueCursorView.BlueCursorTriangle), 6);
            Assert.Equal(125, Canvas.GetTop(blueCursorView.BlueCursorTriangle), 6);
            Assert.Equal(Visibility.Hidden, blueCursorView.BlueCursorTriangle.Visibility);
        });
    }

    [Fact]
    public void ReturnFlight_YieldsWhenTheMouseMovesAndOtherOverlaysHideWhileOnePoints()
    {
        RunOnStaThread(() =>
        {
            BlueCursorView blueCursorView = new();
            const double frameSeconds = 1.0 / 60;
            blueCursorView.FollowMouse(new Point(100, 100), true, false, 0);
            blueCursorView.FollowMouse(new Point(100, 100), true, true, frameSeconds);
            Assert.Equal(Visibility.Hidden, blueCursorView.BlueCursorTriangle.Visibility);

            blueCursorView.PointAt(new Point(800, 400), new Size(1920, 1080), "x");
            for (double seconds = 0; seconds < 6 && blueCursorView.PointingBubble.Visibility == Visibility.Hidden; seconds += frameSeconds)
                blueCursorView.FollowMouse(new Point(100, 100), true, false, frameSeconds);
            for (double seconds = 0; seconds < 6 && blueCursorView.PointingBubble.Visibility == Visibility.Visible; seconds += frameSeconds)
                blueCursorView.FollowMouse(new Point(100, 100), true, false, frameSeconds);
            Assert.True(blueCursorView.IsPointing);

            blueCursorView.FollowMouse(new Point(300, 100), true, false, frameSeconds);
            Assert.False(blueCursorView.IsPointing);

            blueCursorView.PointAt(new Point(800, 400), new Size(1920, 1080), null);
            blueCursorView.CancelPointing();
            Assert.False(blueCursorView.IsPointing);
        });
    }

    [Fact]
    public void Manager_CreatesOneWindowPerMonitorAndClosesEveryWindowOnDispose()
    {
        RunOnStaThread(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog);
            List<IntPtr> overlayWindowHandles = FindOverlayWindowHandles();
            Assert.Equal(displayMonitorCatalog.Monitors.Count, overlayWindowHandles.Count);
            Assert.All(overlayWindowHandles, windowHandle => Assert.True(IsWindowVisible(windowHandle)));
            cursorOverlayManager.Dispose();
            cursorOverlayManager.Dispose();
            Assert.All(overlayWindowHandles, windowHandle => Assert.False(IsWindow(windowHandle)));
            displayMonitorCatalog.RequestRefresh();
            DrainDispatcher();
            Assert.Empty(FindOverlayWindowHandles());
        });
    }

    private static List<IntPtr> FindOverlayWindowHandles()
    {
        List<IntPtr> overlayWindowHandles = [];
        EnumThreadWindows(GetCurrentThreadId(), (windowHandle, _) =>
        {
            if (HwndSource.FromHwnd(windowHandle)?.RootVisual is CursorOverlayWindow)
                overlayWindowHandles.Add(windowHandle);
            return true;
        }, IntPtr.Zero);
        return overlayWindowHandles;
    }

    private static PixelRect ReadWindowBounds(IntPtr windowHandle)
    {
        Assert.True(GetWindowRect(windowHandle, out NativeRect windowRect));
        return new PixelRect(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
    }

    private static void DrainDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void RunOnStaThread(Action testAction)
    {
        Exception? testException = null;
        Thread testThread = new(() =>
        {
            IntPtr previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                Assert.NotEqual(IntPtr.Zero, previousDpiContext);
                testAction();
            }
            catch (Exception exception)
            {
                testException = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                SetThreadDpiAwarenessContext(previousDpiContext);
            }
        });
        testThread.SetApartmentState(ApartmentState.STA);
        testThread.Start();
        testThread.Join();
        if (testException is not null) ExceptionDispatchInfo.Capture(testException).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool WindowEnumerationCallback(IntPtr windowHandle, IntPtr callbackData);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect windowRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr windowHandle, out uint displayAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int left, int top, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, WindowEnumerationCallback callback, IntPtr callbackData);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

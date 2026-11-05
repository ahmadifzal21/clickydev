using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clicky.Windows.Tests;

public sealed class CompanionScreenCaptureTests
{
    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(2560, 1440, 1280, 720)]
    [InlineData(3840, 2160, 1280, 720)]
    [InlineData(1366, 768, 1280, 719)]   // Mac truncates 1280 / (1366/768) = 719.6, it doesn't round
    [InlineData(2560, 1600, 1280, 800)]
    [InlineData(1080, 1920, 720, 1280)]  // portrait: height is the long side
    [InlineData(1024, 1024, 1280, 1280)]
    [InlineData(1024, 768, 1280, 960)]   // smaller monitors are scaled up, as on the Mac
    public void ComputeScreenshotSize_MatchesMacSizing(int monitorWidth, int monitorHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight),
            CompanionScreenCaptureLayout.ComputeScreenshotSize(monitorWidth, monitorHeight));
    }

    [Fact]
    public void OrderCursorScreenFirst_MovesCursorMonitorToFrontAndKeepsTheRestInOrder()
    {
        DisplayMonitor leftMonitor = CreateMonitor("\\\\.\\DISPLAY1", new PixelRect(-1920, 0, 0, 1080));
        DisplayMonitor primaryMonitor = CreateMonitor("\\\\.\\DISPLAY2", new PixelRect(0, 0, 2560, 1440));
        DisplayMonitor rightMonitor = CreateMonitor("\\\\.\\DISPLAY3", new PixelRect(2560, 0, 4480, 1080));
        DisplayMonitor[] monitors = [leftMonitor, primaryMonitor, rightMonitor];

        Assert.Equal([rightMonitor, leftMonitor, primaryMonitor],
            CompanionScreenCaptureLayout.OrderCursorScreenFirst(monitors, 3000, 500));
        Assert.Equal([leftMonitor, primaryMonitor, rightMonitor],
            CompanionScreenCaptureLayout.OrderCursorScreenFirst(monitors, -10, 10));
        // Cursor in a gap between monitors: nothing moves.
        Assert.Equal(monitors, CompanionScreenCaptureLayout.OrderCursorScreenFirst(monitors, 3000, 1300));
    }

    [Fact]
    public void BuildScreenLabel_IsVerbatimMacWording()
    {
        Assert.Equal("user's screen (cursor is here)", CompanionScreenCaptureLayout.BuildScreenLabel(0, 1, isCursorScreen: true));
        Assert.Equal("screen 1 of 2 — cursor is on this screen (primary focus)",
            CompanionScreenCaptureLayout.BuildScreenLabel(0, 2, isCursorScreen: true));
        Assert.Equal("screen 2 of 2 — secondary screen", CompanionScreenCaptureLayout.BuildScreenLabel(1, 2, isCursorScreen: false));
    }

    /// <summary>Real capture of this machine's monitors: one JPEG each, Mac-sized, labelled and cursor screen first.</summary>
    [Fact]
    public void CaptureAllScreensAsJpeg_CapturesEveryMonitorOfThisMachine()
    {
        IReadOnlyList<CompanionScreenCapture> capturedScreens = [];
        IReadOnlyList<DisplayMonitor> monitors = [];
        Exception? captureException = null;
        Thread captureThread = new(() =>
        {
            try
            {
                // The app is PerMonitorV2 through its manifest; the test host is not, so match it on this thread.
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                using DisplayMonitorCatalog displayMonitorCatalog = new();
                monitors = displayMonitorCatalog.Monitors;
                capturedScreens = CompanionScreenCaptureUtility
                    .CaptureAllScreensAsJpegAsync(monitors, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                captureException = exception;
            }
        });
        captureThread.SetApartmentState(ApartmentState.STA);
        captureThread.Start();
        captureThread.Join();
        if (captureException is not null) throw captureException;

        Assert.Equal(monitors.Count, capturedScreens.Count);
        Assert.Equal(capturedScreens.OrderByDescending(capturedScreen => capturedScreen.IsCursorScreen), capturedScreens);
        for (int screenIndex = 0; screenIndex < capturedScreens.Count; screenIndex++)
        {
            CompanionScreenCapture capturedScreen = capturedScreens[screenIndex];
            Assert.Equal(CompanionScreenCaptureLayout.BuildScreenLabel(screenIndex, capturedScreens.Count, capturedScreen.IsCursorScreen),
                capturedScreen.Label);
            Assert.Equal(CompanionScreenCaptureLayout.ComputeScreenshotSize(capturedScreen.CapturedMonitor.Bounds.Width,
                capturedScreen.CapturedMonitor.Bounds.Height), (capturedScreen.ScreenshotWidthPixels, capturedScreen.ScreenshotHeightPixels));
            Assert.Equal(capturedScreen.ScreenshotWidthPixels, ReadJpegSize(capturedScreen.JpegImageBytes).Width);
            Assert.Equal(capturedScreen.ScreenshotHeightPixels, ReadJpegSize(capturedScreen.JpegImageBytes).Height);
            Assert.Equal(ScreenCaptureMethod.WindowsGraphicsCapture, capturedScreen.CaptureMethod);
        }
    }

    /// <summary>The fallback path on its own: a full-size BGRA copy of the monitor that encodes like a WGC frame.</summary>
    [Fact]
    public void GdiFallback_CapturesFullMonitorAndEncodesToScreenshotSize()
    {
        Exception? captureException = null;
        Thread captureThread = new(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                using DisplayMonitorCatalog displayMonitorCatalog = new();
                DisplayMonitor monitor = displayMonitorCatalog.Monitors[0];
                using global::Windows.Graphics.Imaging.SoftwareBitmap gdiBitmap =
                    CompanionScreenCaptureUtility.CaptureMonitorBitmapWithGdi(monitor);
                Assert.Equal((monitor.Bounds.Width, monitor.Bounds.Height), (gdiBitmap.PixelWidth, gdiBitmap.PixelHeight));

                (int screenshotWidth, int screenshotHeight) =
                    CompanionScreenCaptureLayout.ComputeScreenshotSize(monitor.Bounds.Width, monitor.Bounds.Height);
                byte[] jpegImageBytes = CompanionScreenCaptureUtility
                    .EncodeDownscaledJpegAsync(gdiBitmap, screenshotWidth, screenshotHeight).GetAwaiter().GetResult();
                Assert.Equal((screenshotWidth, screenshotHeight), ReadJpegSize(jpegImageBytes));
            }
            catch (Exception exception)
            {
                captureException = exception;
            }
        });
        captureThread.SetApartmentState(ApartmentState.STA);
        captureThread.Start();
        captureThread.Join();
        if (captureException is not null) throw captureException;
    }

    /// <summary>
    /// An opaque magenta overlay is invisible to the capture; the control (same window with capture exclusion lifted)
    /// proves the overlay really was on screen, so the zero isn't just an overlay that never drew.
    /// </summary>
    [Fact]
    public void OverlayWindow_IsAbsentFromCaptures_AndPresentWhenExclusionIsLifted()
    {
        Exception? captureException = null;
        Thread captureThread = new(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                using DisplayMonitorCatalog displayMonitorCatalog = new();
                DisplayMonitor monitor = displayMonitorCatalog.Monitors[0];
                CursorOverlayWindow magentaOverlayWindow = new(monitor, () => { }) { Background = Brushes.Magenta };
                try
                {
                    magentaOverlayWindow.Show();
                    WaitForComposition();
                    Assert.True(MagentaFraction(CaptureMonitorJpeg(monitor)) < 0.01);

                    Assert.True(SetWindowDisplayAffinity(new WindowInteropHelper(magentaOverlayWindow).Handle, WDA_NONE));
                    WaitForComposition();
                    Assert.True(MagentaFraction(CaptureMonitorJpeg(monitor)) > 0.9);
                }
                finally
                {
                    magentaOverlayWindow.Close();
                }
            }
            catch (Exception exception)
            {
                captureException = exception;
            }
        });
        captureThread.SetApartmentState(ApartmentState.STA);
        captureThread.Start();
        captureThread.Join();
        if (captureException is not null) throw captureException;
    }

    private static void WaitForComposition()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(300);
    }

    /// <summary>Captures off the STA thread (no pumping needed) but with the same PerMonitorV2 context as the app.</summary>
    private static byte[] CaptureMonitorJpeg(DisplayMonitor monitor) => Task.Run(async () =>
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        IReadOnlyList<CompanionScreenCapture> capturedScreens =
            await CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync([monitor], CancellationToken.None);
        return capturedScreens[0].JpegImageBytes;
    }).GetAwaiter().GetResult();

    private static double MagentaFraction(byte[] jpegImageBytes) => Task.Run(async () =>
    {
        using InMemoryRandomAccessStream jpegStream = new();
        await jpegStream.WriteAsync(jpegImageBytes.AsBuffer());
        jpegStream.Seek(0);
        BitmapDecoder jpegDecoder = await BitmapDecoder.CreateAsync(jpegStream);
        PixelDataProvider pixelData = await jpegDecoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] bgraPixelBytes = pixelData.DetachPixelData();
        int magentaPixelCount = 0;
        for (int byteOffset = 0; byteOffset < bgraPixelBytes.Length; byteOffset += 4)
        {
            bool isMagenta = bgraPixelBytes[byteOffset] > 180 && bgraPixelBytes[byteOffset + 1] < 90 && bgraPixelBytes[byteOffset + 2] > 180;
            if (isMagenta) magentaPixelCount++;
        }
        return (double)magentaPixelCount / (bgraPixelBytes.Length / 4);
    }).GetAwaiter().GetResult();

    /// <summary>Reads width/height from the JPEG start-of-frame marker, so the test checks the encoded file itself.</summary>
    private static (int Width, int Height) ReadJpegSize(byte[] jpegImageBytes)
    {
        Assert.True(jpegImageBytes[0] == 0xFF && jpegImageBytes[1] == 0xD8, "Not a JPEG");
        int markerOffset = 2;
        while (markerOffset < jpegImageBytes.Length)
        {
            byte markerType = jpegImageBytes[markerOffset + 1];
            int segmentLength = jpegImageBytes[markerOffset + 2] << 8 | jpegImageBytes[markerOffset + 3];
            if (markerType is 0xC0 or 0xC1 or 0xC2)
            {
                int height = jpegImageBytes[markerOffset + 5] << 8 | jpegImageBytes[markerOffset + 6];
                int width = jpegImageBytes[markerOffset + 7] << 8 | jpegImageBytes[markerOffset + 8];
                return (width, height);
            }
            markerOffset += 2 + segmentLength;
        }
        throw new InvalidOperationException("JPEG has no start-of-frame marker");
    }

    private static DisplayMonitor CreateMonitor(string deviceName, PixelRect bounds) =>
        new(deviceName, bounds, bounds, 96, 96, isPrimary: bounds.Left == 0 && bounds.Top == 0);

    private const uint WDA_NONE = 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint displayAffinity);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}

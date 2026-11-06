using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Clicky.Windows.Display;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Clicky.Windows.ScreenCapture;

/// <summary>
/// Port of CompanionScreenCaptureUtility.swift: captures every monitor as a labeled JPEG, cursor screen first.
/// Monitors are captured in parallel with Windows Graphics Capture, each falling back to GDI BitBlt on its own.
/// Clicky's windows don't need filtering like the Mac's SCContentFilter: they all set WDA_EXCLUDEFROMCAPTURE,
/// which both WGC and BitBlt honour.
/// </summary>
internal static class CompanionScreenCaptureUtility
{
    public static async Task<IReadOnlyList<CompanionScreenCapture>> CaptureAllScreensAsJpegAsync(
        IReadOnlyList<DisplayMonitor> monitors, CancellationToken cancellationToken)
    {
        if (monitors.Count == 0) throw new InvalidOperationException("No display available for capture.");

        // PerMonitorV2 makes GetCursorPos report physical virtual-screen pixels, the space DisplayMonitor uses.
        GetCursorPos(out NativePoint cursorPhysicalPosition);
        IReadOnlyList<DisplayMonitor> orderedMonitors =
            CompanionScreenCaptureLayout.OrderCursorScreenFirst(monitors, cursorPhysicalPosition.X, cursorPhysicalPosition.Y);

        using WindowsGraphicsMonitorCapture? windowsGraphicsMonitorCapture = TryCreateWindowsGraphicsMonitorCapture();
        CompanionScreenCapture?[] capturedScreens = await Task.WhenAll(orderedMonitors.Select((monitor, screenIndex) =>
            Task.Run(() => TryCaptureOneScreenAsync(windowsGraphicsMonitorCapture, monitor, screenIndex,
                orderedMonitors.Count, monitor.Contains(cursorPhysicalPosition.X, cursorPhysicalPosition.Y), cancellationToken),
                cancellationToken)));

        // Like the Mac, a monitor that can't be captured is skipped; only "nothing at all" is an error.
        List<CompanionScreenCapture> successfulCaptures = capturedScreens.OfType<CompanionScreenCapture>().ToList();
        if (successfulCaptures.Count == 0) throw new InvalidOperationException("Failed to capture any screen.");
        return successfulCaptures;
    }

    private static WindowsGraphicsMonitorCapture? TryCreateWindowsGraphicsMonitorCapture()
    {
        try
        {
            return WindowsGraphicsMonitorCapture.IsSupportedOnThisMachine ? new WindowsGraphicsMonitorCapture() : null;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Windows Graphics Capture is unavailable, using GDI for every monitor: {exception.Message}");
            return null;
        }
    }

    private static async Task<CompanionScreenCapture?> TryCaptureOneScreenAsync(
        WindowsGraphicsMonitorCapture? windowsGraphicsMonitorCapture, DisplayMonitor monitor, int screenIndex,
        int screenCount, bool isCursorScreen, CancellationToken cancellationToken)
    {
        Stopwatch captureStopwatch = Stopwatch.StartNew();
        try
        {
            (SoftwareBitmap monitorBitmap, ScreenCaptureMethod captureMethod) =
                await CaptureMonitorBitmapAsync(windowsGraphicsMonitorCapture, monitor, cancellationToken);
            using (monitorBitmap)
            {
                (int screenshotWidth, int screenshotHeight) =
                    CompanionScreenCaptureLayout.ComputeScreenshotSize(monitor.Bounds.Width, monitor.Bounds.Height);
                byte[] jpegImageBytes = await EncodeDownscaledJpegAsync(monitorBitmap, screenshotWidth, screenshotHeight);
                return new CompanionScreenCapture(jpegImageBytes,
                    CompanionScreenCaptureLayout.BuildScreenLabel(screenIndex, screenCount, isCursorScreen), isCursorScreen,
                    monitor, screenshotWidth, screenshotHeight, captureMethod, captureStopwatch.Elapsed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"Could not capture {monitor.DeviceName}: {exception.Message}");
            return null;
        }
    }

    private static async Task<(SoftwareBitmap MonitorBitmap, ScreenCaptureMethod CaptureMethod)> CaptureMonitorBitmapAsync(
        WindowsGraphicsMonitorCapture? windowsGraphicsMonitorCapture, DisplayMonitor monitor, CancellationToken cancellationToken)
    {
        if (windowsGraphicsMonitorCapture is not null)
        {
            try
            {
                IntPtr monitorHandle = FindMonitorHandle(monitor);
                return (await windowsGraphicsMonitorCapture.CaptureMonitorAsync(monitorHandle, cancellationToken),
                    ScreenCaptureMethod.WindowsGraphicsCapture);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning($"Windows Graphics Capture failed for {monitor.DeviceName}, falling back to GDI: {exception.Message}");
            }
        }

        return (CaptureMonitorBitmapWithGdi(monitor), ScreenCaptureMethod.GdiBitBlt);
    }

    /// <summary>The catalog keeps geometry, not HMONITORs (they can go stale), so look the handle up at capture time.</summary>
    private static IntPtr FindMonitorHandle(DisplayMonitor monitor)
    {
        NativePoint monitorCentre = new()
        {
            X = monitor.Bounds.Left + monitor.Bounds.Width / 2,
            Y = monitor.Bounds.Top + monitor.Bounds.Height / 2,
        };
        IntPtr monitorHandle = MonitorFromPoint(monitorCentre, MONITOR_DEFAULTTONULL);
        return monitorHandle != IntPtr.Zero
            ? monitorHandle
            : throw new InvalidOperationException($"{monitor.DeviceName} is no longer connected.");
    }

    internal static SoftwareBitmap CaptureMonitorBitmapWithGdi(DisplayMonitor monitor)
    {
        PixelRect monitorBounds = monitor.Bounds;
        using System.Drawing.Bitmap gdiBitmap = new(monitorBounds.Width, monitorBounds.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (System.Drawing.Graphics bitmapGraphics = System.Drawing.Graphics.FromImage(gdiBitmap))
        {
            bitmapGraphics.CopyFromScreen(monitorBounds.Left, monitorBounds.Top, 0, 0,
                new System.Drawing.Size(monitorBounds.Width, monitorBounds.Height),
                // CopyFromScreen rejects SourceCopy | CaptureBlt; with DWM the screen DC already includes layered windows.
                System.Drawing.CopyPixelOperation.SourceCopy);
        }

        System.Drawing.Imaging.BitmapData lockedPixels = gdiBitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, monitorBounds.Width, monitorBounds.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            // 32bpp rows are never padded, so the locked buffer is exactly width * 4 bytes per row, in BGRA order.
            byte[] bgraPixelBytes = new byte[monitorBounds.Width * monitorBounds.Height * 4];
            Marshal.Copy(lockedPixels.Scan0, bgraPixelBytes, 0, bgraPixelBytes.Length);
            return SoftwareBitmap.CreateCopyFromBuffer(bgraPixelBytes.AsBuffer(), BitmapPixelFormat.Bgra8,
                monitorBounds.Width, monitorBounds.Height, BitmapAlphaMode.Ignore);
        }
        finally
        {
            gdiBitmap.UnlockBits(lockedPixels);
        }
    }

    /// <summary>One encoder for both capture paths: scale to the Mac's screenshot size, JPEG quality 0.8.</summary>
    internal static async Task<byte[]> EncodeDownscaledJpegAsync(SoftwareBitmap monitorBitmap, int screenshotWidth, int screenshotHeight)
    {
        using InMemoryRandomAccessStream jpegStream = new();
        BitmapPropertySet jpegEncodingOptions = new()
        {
            { "ImageQuality", new BitmapTypedValue(CompanionScreenCaptureLayout.JpegQuality, global::Windows.Foundation.PropertyType.Single) },
        };
        BitmapEncoder jpegEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpegStream, jpegEncodingOptions);
        jpegEncoder.SetSoftwareBitmap(monitorBitmap);
        jpegEncoder.BitmapTransform.ScaledWidth = (uint)screenshotWidth;
        jpegEncoder.BitmapTransform.ScaledHeight = (uint)screenshotHeight;
        jpegEncoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await jpegEncoder.FlushAsync();

        byte[] jpegImageBytes = new byte[jpegStream.Size];
        using Stream jpegReadStream = jpegStream.GetInputStreamAt(0).AsStreamForRead();
        await jpegReadStream.ReadExactlyAsync(jpegImageBytes);
        return jpegImageBytes;
    }

    private const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
}

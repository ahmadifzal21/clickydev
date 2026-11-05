using System.Diagnostics;
using System.IO;
using System.Text;
using Clicky.Windows.Display;

namespace Clicky.Windows.ScreenCapture;

/// <summary>
/// Tray "Save debug screenshots": runs the real capture and writes what the model would see (one JPEG per monitor,
/// cursor screen first) plus capture-log.txt with the label, monitor, method and timing of each, then opens the
/// folder so captures can be checked by eye.
/// </summary>
internal static class DebugScreenshotWriter
{
    public static async Task SaveAndOpenFolderAsync(IReadOnlyList<DisplayMonitor> monitors)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        IReadOnlyList<CompanionScreenCapture> capturedScreens =
            await CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync(monitors, CancellationToken.None);
        totalStopwatch.Stop();

        string debugFolderPath = Path.Combine(Path.GetTempPath(), "Clicky", "debug-screenshots",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(debugFolderPath);

        StringBuilder captureLog = new();
        captureLog.AppendLine($"Captured {capturedScreens.Count} of {monitors.Count} monitors in {totalStopwatch.ElapsedMilliseconds} ms (in parallel).");
        for (int screenIndex = 0; screenIndex < capturedScreens.Count; screenIndex++)
        {
            CompanionScreenCapture capturedScreen = capturedScreens[screenIndex];
            string jpegFileName = $"screen{screenIndex + 1}{(capturedScreen.IsCursorScreen ? "-cursor" : "")}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(debugFolderPath, jpegFileName), capturedScreen.JpegImageBytes);

            DisplayMonitor capturedMonitor = capturedScreen.CapturedMonitor;
            PixelRect monitorBounds = capturedMonitor.Bounds;
            captureLog.AppendLine();
            captureLog.AppendLine($"{jpegFileName}: {capturedScreen.Label}");
            captureLog.AppendLine($"  monitor {capturedMonitor.DeviceName}, {monitorBounds.Width}x{monitorBounds.Height} px at ({monitorBounds.Left}, {monitorBounds.Top}), {capturedMonitor.DpiX} dpi");
            captureLog.AppendLine($"  screenshot {capturedScreen.ScreenshotWidthPixels}x{capturedScreen.ScreenshotHeightPixels} px, {capturedScreen.JpegImageBytes.Length / 1024} KB");
            captureLog.AppendLine($"  method {capturedScreen.CaptureMethod}, {capturedScreen.CaptureDuration.TotalMilliseconds:F0} ms");
        }

        await File.WriteAllTextAsync(Path.Combine(debugFolderPath, "capture-log.txt"), captureLog.ToString());
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{debugFolderPath}\"") { UseShellExecute = true });
    }
}

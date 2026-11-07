using System.Diagnostics;
using System.Runtime.InteropServices;
using Clicky.Windows.Display;

namespace Clicky.Windows.ScreenCapture;

/// <summary>A tiny greyscale picture of part of the screen, for telling "the screen changed" from "it didn't".</summary>
internal interface IScreenSampler
{
    /// <summary>The area scaled to <see cref="ScreenSettleWaiter.SampleWidth"/> wide, one byte per pixel; null if it can't be read.</summary>
    byte[]? Sample(PixelRect area);
}

/// <summary>
/// Waits until the screen stops changing after the user acts (E11 T11.15), so the follow-up screenshot shows the menu fully
/// open or the page fully loaded, without a fixed long delay. Samples are 192 px wide greyscale; a pixel counts as changed
/// when it moves more than 12 levels. After a 250 ms head start it samples every 150 ms and stops when two samples in a row
/// differ by at most 0.5% and either the screen has changed since the action or 1.5 s have passed; never longer than 4 s.
/// If the screen can't be sampled it just waits <c>fallback</c>.
/// </summary>
internal sealed class ScreenSettleWaiter(IScreenSampler screenSampler)
{
    public const int SampleWidth = 192;
    private const int ChangedPixelThreshold = 12;
    private const double StableFraction = 0.005;
    private static readonly TimeSpan HeadStart = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan UnchangedGiveUp = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(4);

    public ScreenSettleWaiter() : this(new GdiScreenSampler())
    {
    }

    public IScreenSampler Sampler => screenSampler;

    /// <summary>Waits for <paramref name="area"/> to settle; <paramref name="baseline"/> is the picture from the moment of the action, if taken.</summary>
    public async Task<(bool Changed, TimeSpan Waited)> WaitAsync(PixelRect area, byte[]? baseline, TimeSpan fallback, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        baseline ??= screenSampler.Sample(area);
        if (baseline is null)
        {
            await Task.Delay(fallback, cancellationToken);
            return (false, stopwatch.Elapsed);
        }
        await Task.Delay(HeadStart, cancellationToken);
        byte[] previous = baseline;
        bool changed = false;
        int sampleCount = 0;
        while (stopwatch.Elapsed < HardCap)
        {
            byte[]? sample = screenSampler.Sample(area);
            if (sample is null)
            {
                TimeSpan remaining = fallback - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
                return (changed, stopwatch.Elapsed);
            }
            if (ChangedFraction(sample, baseline) > StableFraction) changed = true;
            sampleCount++;
            // Stable means two samples in a row agree; the baseline from the moment of the action isn't one of them.
            bool isStable = sampleCount >= 2 && ChangedFraction(sample, previous) <= StableFraction;
            previous = sample;
            if (isStable && (changed || stopwatch.Elapsed >= UnchangedGiveUp)) return (changed, stopwatch.Elapsed);
            await Task.Delay(SampleInterval, cancellationToken);
        }
        return (changed, stopwatch.Elapsed);
    }

    /// <summary>The share of pixels that moved more than 12 grey levels; 1 when the pictures can't be compared.</summary>
    public static double ChangedFraction(byte[] first, byte[] second)
    {
        if (first.Length != second.Length || first.Length == 0) return 1;
        int changedPixels = 0;
        for (int index = 0; index < first.Length; index++)
            if (Math.Abs(first[index] - second[index]) > ChangedPixelThreshold) changedPixels++;
        return (double)changedPixels / first.Length;
    }
}

/// <summary>
/// Samples with GDI StretchBlt from the screen DC. Clicky's own overlays are excluded from capture (WDA_EXCLUDEFROMCAPTURE),
/// so the rings and marks it draws never count as a change.
/// </summary>
internal sealed class GdiScreenSampler : IScreenSampler
{
    public byte[]? Sample(PixelRect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;
        int width = ScreenSettleWaiter.SampleWidth;
        int height = Math.Max(1, (int)Math.Round((double)area.Height * width / area.Width));
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr previousObject = SelectObject(memoryDc, bitmap);
        try
        {
            SetStretchBltMode(memoryDc, HALFTONE);
            if (!StretchBlt(memoryDc, 0, 0, width, height, screenDc, area.Left, area.Top, area.Width, area.Height, SRCCOPY)) return null;
            BitmapInfoHeader header = new()
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0
            };
            byte[] pixels = new byte[width * height * 4];
            SelectObject(memoryDc, previousObject); // GetDIBits needs the bitmap deselected.
            previousObject = IntPtr.Zero;
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS) == 0) return null;
            byte[] grey = new byte[width * height];
            for (int index = 0; index < grey.Length; index++)
                grey[index] = (byte)((pixels[index * 4] * 29 + pixels[index * 4 + 1] * 150 + pixels[index * 4 + 2] * 77) >> 8);
            return grey;
        }
        finally
        {
            if (previousObject != IntPtr.Zero) SelectObject(memoryDc, previousObject);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr windowHandle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr deviceContext);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr destination, int destinationX, int destinationY, int destinationWidth, int destinationHeight,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr deviceContext, IntPtr bitmap, uint startScan, uint scanLines, byte[] bits,
        ref BitmapInfoHeader bitmapInfo, uint usage);
}

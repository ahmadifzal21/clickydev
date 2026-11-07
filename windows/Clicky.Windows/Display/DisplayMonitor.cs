namespace Clicky.Windows.Display;

/// <summary>Immutable physical geometry so an overlay never mixes two display configurations.</summary>
internal sealed record DisplayMonitor
{
    public string DeviceName { get; }
    public PixelRect Bounds { get; }
    public PixelRect WorkArea { get; }
    public uint DpiX { get; }
    public uint DpiY { get; }
    public bool IsPrimary { get; }
    public double DpiScaleX => DpiX / 96.0;
    public double DpiScaleY => DpiY / 96.0;

    public DisplayMonitor(string deviceName, PixelRect bounds, PixelRect workArea, uint dpiX, uint dpiY, bool isPrimary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentOutOfRangeException(nameof(bounds));
        if (dpiX == 0) throw new ArgumentOutOfRangeException(nameof(dpiX));
        if (dpiY == 0) throw new ArgumentOutOfRangeException(nameof(dpiY));
        DeviceName = deviceName;
        Bounds = bounds;
        WorkArea = workArea;
        DpiX = dpiX;
        DpiY = dpiY;
        IsPrimary = isPrimary;
    }

    public bool Contains(double physicalX, double physicalY) =>
        physicalX >= Bounds.Left && physicalX < Bounds.Right &&
        physicalY >= Bounds.Top && physicalY < Bounds.Bottom;
}

namespace Clicky.Windows.Display;

/// <summary>An integer rectangle in physical virtual-screen pixels (same space as Win32 RECT).</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

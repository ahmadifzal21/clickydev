using System.Runtime.InteropServices;

namespace Clicky.Windows.Tray;

/// <summary>
/// Owns the system tray icon (the Windows counterpart of the NSStatusItem half of
/// MenuBarPanelManager.swift). Left-click raises <see cref="TrayIconLeftClicked"/> so the app can
/// toggle the companion panel; right-click shows the context menu the caller supplies.
/// WPF has no tray icon, so this uses WinForms' NotifyIcon; the WPF dispatcher pumps its messages.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon notifyIcon;
    private readonly System.Drawing.Icon clickyTrayIcon;
    private readonly IntPtr clickyTrayIconHandle;

    /// <summary>
    /// Raised on a left-click, with the mouse position in physical virtual-screen pixels. The
    /// click lands on the icon itself, so this is the anchor the panel is positioned against.
    /// </summary>
    public event Action<System.Drawing.Point>? TrayIconLeftClicked;

    public TrayIconManager(System.Windows.Forms.ContextMenuStrip trayContextMenu)
    {
        (clickyTrayIcon, clickyTrayIconHandle) = CreateClickyTrayIcon(System.Windows.Forms.SystemInformation.SmallIconSize.Width);

        notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = clickyTrayIcon,
            Text = "Clicky",
            // NotifyIcon shows this on right-click by itself; left-click is ours.
            ContextMenuStrip = trayContextMenu,
            Visible = true
        };
        notifyIcon.MouseClick += HandleNotifyIconMouseClick;
    }

    private void HandleNotifyIconMouseClick(object? sender, System.Windows.Forms.MouseEventArgs mouseEventArgs)
    {
        if (mouseEventArgs.Button != System.Windows.Forms.MouseButtons.Left)
        {
            return;
        }

        // MouseEventArgs.Location is meaningless for a tray icon; Cursor.Position is the real
        // click point (physical pixels, because the process is PerMonitorV2 DPI aware).
        TrayIconLeftClicked?.Invoke(System.Windows.Forms.Cursor.Position);
    }

    public void Dispose()
    {
        notifyIcon.MouseClick -= HandleNotifyIconMouseClick;

        // Hide before disposing, otherwise a ghost icon stays in the tray until it's hovered.
        notifyIcon.Visible = false;
        notifyIcon.Dispose();

        clickyTrayIcon.Dispose();
        DestroyIcon(clickyTrayIconHandle);
    }

    /// <summary>
    /// Draws the blue Clicky cursor (DS.Colors.overlayCursorBlue, #3380FF) with a white outline,
    /// so it reads on both light and dark taskbars. Drawn in code at the system small-icon size
    /// (which already reflects the display scale) rather than shipping an .ico file.
    /// </summary>
    private static (System.Drawing.Icon Icon, IntPtr IconHandle) CreateClickyTrayIcon(int iconSizePixels)
    {
        using System.Drawing.Bitmap iconBitmap = new(iconSizePixels, iconSizePixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics iconGraphics = System.Drawing.Graphics.FromImage(iconBitmap))
        {
            iconGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            iconGraphics.Clear(System.Drawing.Color.Transparent);

            // Arrow-pointer silhouette in unit coordinates, tip at the top-left like a mouse cursor.
            System.Drawing.PointF[] cursorOutlineUnitPoints =
            [
                new(0.18f, 0.08f),
                new(0.86f, 0.56f),
                new(0.52f, 0.60f),
                new(0.34f, 0.92f)
            ];
            System.Drawing.PointF[] cursorOutlinePoints = cursorOutlineUnitPoints
                .Select(unitPoint => new System.Drawing.PointF(unitPoint.X * iconSizePixels, unitPoint.Y * iconSizePixels))
                .ToArray();

            using System.Drawing.Drawing2D.GraphicsPath cursorPath = new();
            cursorPath.AddPolygon(cursorOutlinePoints);

            using System.Drawing.SolidBrush cursorFillBrush = new(System.Drawing.Color.FromArgb(0x33, 0x80, 0xFF));
            using System.Drawing.Pen cursorOutlinePen = new(System.Drawing.Color.White, Math.Max(1f, iconSizePixels / 12f))
            {
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            iconGraphics.FillPath(cursorFillBrush, cursorPath);
            iconGraphics.DrawPath(cursorOutlinePen, cursorPath);
        }

        // Icon.FromHandle doesn't take ownership of the HICON, so we keep the handle and
        // DestroyIcon it on dispose to avoid leaking a GDI handle.
        IntPtr iconHandle = iconBitmap.GetHicon();
        return (System.Drawing.Icon.FromHandle(iconHandle), iconHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}

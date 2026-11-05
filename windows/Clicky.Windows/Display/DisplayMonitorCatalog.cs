using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Clicky.Windows.Display;

/// <summary>
/// Owns a hidden top-level HWND because message-only windows do not receive display broadcasts.
/// Change notifications run on the WPF dispatcher, allowing overlays to rebuild safely.
/// </summary>
internal sealed class DisplayMonitorCatalog : IDisposable
{
    private readonly HwndSource notificationWindow;
    private DispatcherOperation? pendingRefresh;
    private bool isDisposed;

    public IReadOnlyList<DisplayMonitor> Monitors { get; private set; }
    public event Action? MonitorsChanged;

    public DisplayMonitorCatalog()
    {
        Monitors = EnumerateMonitors();
        notificationWindow = new HwndSource(new HwndSourceParameters("Clicky display notifications")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
            ExtendedWindowStyle = 0x08000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        });
        notificationWindow.AddHook(HandleWindowMessage);
    }

    /// <summary>Coalesces display broadcasts and per-overlay WM_DPICHANGED messages.</summary>
    public void RequestRefresh()
    {
        notificationWindow.Dispatcher.VerifyAccess();
        if (isDisposed || pendingRefresh is { Status: DispatcherOperationStatus.Pending }) return;
        pendingRefresh = notificationWindow.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (isDisposed) return;
            IReadOnlyList<DisplayMonitor> currentMonitors;
            try
            {
                currentMonitors = EnumerateMonitors();
            }
            catch (Win32Exception exception)
            {
                // A topology change can invalidate a handle during enumeration; keep the last good snapshot.
                Trace.TraceWarning($"Could not refresh Clicky monitors: {exception.Message}");
                return;
            }

            if (Monitors.SequenceEqual(currentMonitors)) return;
            Monitors = currentMonitors;
            MonitorsChanged?.Invoke();
        });
    }

    private IntPtr HandleWindowMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x007E or 0x001A or 0x02E0) RequestRefresh(); // display, settings, DPI
        return IntPtr.Zero;
    }

    private static IReadOnlyList<DisplayMonitor> EnumerateMonitors()
    {
        List<DisplayMonitor> monitors = [];
        Win32Exception? enumerationError = null;
        bool succeeded = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitorHandle, IntPtr deviceContext, ref NativeRect monitorRect, IntPtr callbackData) =>
            {
                NativeMonitorInfo monitorInfo = new() { Size = Marshal.SizeOf<NativeMonitorInfo>(), DeviceName = string.Empty };
                if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
                {
                    enumerationError = new Win32Exception(Marshal.GetLastWin32Error());
                    return false;
                }

                int dpiResult = GetDpiForMonitor(monitorHandle, 0, out uint dpiX, out uint dpiY);
                if (dpiResult < 0 || dpiX == 0 || dpiY == 0)
                {
                    enumerationError = new Win32Exception($"Could not read DPI for {monitorInfo.DeviceName} (0x{dpiResult:X8}).");
                    return false;
                }

                monitors.Add(new DisplayMonitor(monitorInfo.DeviceName, monitorInfo.MonitorRect.ToPixelRect(),
                    monitorInfo.WorkRect.ToPixelRect(), dpiX, dpiY, (monitorInfo.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);

        if (enumerationError is not null) throw enumerationError;
        if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (monitors.Count == 0) throw new Win32Exception("Windows reported no connected monitors.");
        return monitors.OrderByDescending(displayMonitor => displayMonitor.IsPrimary)
            .ThenBy(displayMonitor => displayMonitor.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        pendingRefresh?.Abort();
        notificationWindow.RemoveHook(HandleWindowMessage);
        notificationWindow.Dispose();
    }

    private delegate bool MonitorEnumerationCallback(IntPtr monitorHandle, IntPtr deviceContext, ref NativeRect monitorRect, IntPtr callbackData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect MonitorRect;
        public NativeRect WorkRect;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRect, MonitorEnumerationCallback callback, IntPtr callbackData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref NativeMonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitorHandle, int dpiType, out uint dpiX, out uint dpiY);
}

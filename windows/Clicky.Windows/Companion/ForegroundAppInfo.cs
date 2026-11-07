using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Clicky.Windows.Companion;

/// <summary>
/// The app in front: process id, process name and window title (E11 T11.19). Clicky's own windows never count as the front
/// app, since the user is always really working in something else.
/// </summary>
internal sealed record ForegroundAppInfo(int ProcessId, string ProcessName, string WindowTitle)
{
    /// <summary>The foreground window's app, or null when there is none or it is Clicky's own.</summary>
    public static ForegroundAppInfo? Read()
    {
        IntPtr windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero) return null;
        GetWindowThreadProcessId(windowHandle, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId) return null;
        StringBuilder title = new(512);
        GetWindowText(windowHandle, title, title.Capacity);
        string processName;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            processName = "unknown";
        }
        return new ForegroundAppInfo((int)processId, processName, title.ToString());
    }

    /// <summary>The foreground window's process id (cheap; for polling), or 0.</summary>
    public static int ReadProcessId()
    {
        IntPtr windowHandle = GetForegroundWindow();
        if (windowHandle == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(windowHandle, out uint processId);
        return (int)processId;
    }

    /// <summary>True when the window under a physical-pixel point is one of Clicky's (the panel, a result card).</summary>
    public static bool IsClickyWindowAt(int physicalX, int physicalY)
    {
        IntPtr windowHandle = WindowFromPoint(new NativePoint { X = physicalX, Y = physicalY });
        if (windowHandle == IntPtr.Zero) return false;
        GetWindowThreadProcessId(windowHandle, out uint processId);
        return processId == Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}

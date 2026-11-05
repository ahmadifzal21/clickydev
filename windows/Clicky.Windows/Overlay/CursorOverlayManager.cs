using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Clicky.Windows.Display;

namespace Clicky.Windows.Overlay;

/// <summary>Owns the per-monitor HWNDs and one foreground hook so topology and ordering stay on the UI thread.</summary>
internal sealed class CursorOverlayManager : IDisposable
{
    private readonly DisplayMonitorCatalog displayMonitorCatalog;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WinEventCallback foregroundChangedCallback;
    private List<CursorOverlayWindow> cursorOverlayWindows = [];
    private IntPtr foregroundEventHook;
    private bool isDisposed;

    public CursorOverlayManager(DisplayMonitorCatalog displayMonitorCatalog)
    {
        this.displayMonitorCatalog = displayMonitorCatalog;
        foregroundChangedCallback = HandleForegroundChanged;
        foregroundEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            foregroundChangedCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (foregroundEventHook == IntPtr.Zero)
            Trace.TraceWarning("Could not watch foreground changes; Clicky overlays may be covered by another topmost window.");
        displayMonitorCatalog.MonitorsChanged += RebuildOverlayWindows;
        RebuildOverlayWindows();
    }

    private void RebuildOverlayWindows()
    {
        dispatcher.VerifyAccess();
        if (isDisposed) return;
        List<CursorOverlayWindow> replacementWindows = [];
        try
        {
            foreach (DisplayMonitor displayMonitor in displayMonitorCatalog.Monitors)
            {
                CursorOverlayWindow cursorOverlayWindow = new(displayMonitor, displayMonitorCatalog.RequestRefresh);
                replacementWindows.Add(cursorOverlayWindow);
                cursorOverlayWindow.Show();
            }
        }
        catch (Win32Exception exception)
        {
            foreach (CursorOverlayWindow replacementWindow in replacementWindows) replacementWindow.Close();
            Trace.TraceWarning($"Could not rebuild Clicky overlays; keeping the previous windows: {exception.Message}");
            return;
        }

        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows) cursorOverlayWindow.Close();
        cursorOverlayWindows = replacementWindows;
    }

    private void HandleForegroundChanged(IntPtr eventHook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTime)
    {
        // Out-of-context callbacks arrive on the registering thread; defer until the new foreground is settled.
        if (isDisposed) return;
        dispatcher.BeginInvoke(() =>
        {
            if (isDisposed) return;
            foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows)
            {
                try
                {
                    cursorOverlayWindow.RaiseWithoutActivating();
                }
                catch (Win32Exception exception)
                {
                    Trace.TraceWarning(exception.Message);
                }
            }
        });
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (isDisposed) return;
        isDisposed = true;
        displayMonitorCatalog.MonitorsChanged -= RebuildOverlayWindows;
        if (foregroundEventHook != IntPtr.Zero)
        {
            if (!UnhookWinEvent(foregroundEventHook))
                Trace.TraceWarning("Could not remove the Clicky foreground event hook.");
            foregroundEventHook = IntPtr.Zero;
        }
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows) cursorOverlayWindow.Close();
        cursorOverlayWindows.Clear();
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventCallback(IntPtr eventHook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHookModule,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr eventHook);
}

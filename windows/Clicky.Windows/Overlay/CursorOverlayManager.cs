using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Windows.Companion;
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
    private TimeSpan lastRenderingTime;
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
        // Per-frame like the Mac's 16 ms tracking timer, but synced to WPF's own render so motion never tears.
        CompositionTarget.Rendering += FollowMouseForRenderedFrame;
    }

    private void FollowMouseForRenderedFrame(object? sender, EventArgs eventArgs)
    {
        TimeSpan renderingTime = ((RenderingEventArgs)eventArgs).RenderingTime;
        // Rendering can fire more than once per frame; an unchanged time means the same frame.
        if (renderingTime == lastRenderingTime) return;
        double elapsedSeconds = lastRenderingTime == default ? 0 : (renderingTime - lastRenderingTime).TotalSeconds;
        lastRenderingTime = renderingTime;

        // PerMonitorV2 makes GetCursorPos report physical virtual-screen pixels, the space DisplayMonitor uses.
        if (!GetCursorPos(out NativePoint physicalMousePosition)) return;
        bool isAnyOverlayPointing = cursorOverlayWindows.Any(cursorOverlayWindow => cursorOverlayWindow.IsPointing);
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows)
            cursorOverlayWindow.FollowMouse(physicalMousePosition.X, physicalMousePosition.Y,
                isAnyOverlayPointing && !cursorOverlayWindow.IsPointing, elapsedSeconds);
    }

    /// <summary>
    /// Flies the buddy to a physical virtual-screen pixel, on the overlay of the monitor containing it; any other
    /// overlay's pointing is dropped so only one buddy is ever visible.
    /// </summary>
    public void PointAt(double physicalTargetX, double physicalTargetY, string? bubbleText)
    {
        dispatcher.VerifyAccess();
        CursorOverlayWindow? targetOverlayWindow = cursorOverlayWindows.FirstOrDefault(
            cursorOverlayWindow => cursorOverlayWindow.DisplayMonitor.Contains(physicalTargetX, physicalTargetY));
        if (targetOverlayWindow is null)
        {
            Trace.TraceWarning($"Clicky can't point at ({physicalTargetX}, {physicalTargetY}): it is on no monitor.");
            return;
        }
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows)
            if (cursorOverlayWindow != targetOverlayWindow) cursorOverlayWindow.CancelPointing();
        targetOverlayWindow.PointAt(physicalTargetX, physicalTargetY, bubbleText);
    }

    // Remembered so overlays rebuilt after a display change show the same state.
    private CompanionVoiceState currentVoiceState = CompanionVoiceState.Idle;
    private string? currentResponseText;

    public void SetVoiceState(CompanionVoiceState voiceState)
    {
        currentVoiceState = voiceState;
        ForEachCompanionView(blueCursorView => blueCursorView.SetVoiceState(voiceState));
    }

    public void SetAudioPowerLevel(double audioPowerLevel) =>
        ForEachCompanionView(blueCursorView => blueCursorView.SetAudioPowerLevel(audioPowerLevel));

    /// <summary>Shows or updates the streamed reply; every overlay gets it and the mouse's monitor draws it.</summary>
    public void ShowResponseText(string accumulatedResponseText)
    {
        currentResponseText = accumulatedResponseText;
        ForEachCompanionView(blueCursorView => blueCursorView.ShowResponseText(accumulatedResponseText));
    }

    public void FinishResponse()
    {
        currentResponseText = null;
        ForEachCompanionView(blueCursorView => blueCursorView.FinishResponse());
    }

    public void HideResponse()
    {
        currentResponseText = null;
        ForEachCompanionView(blueCursorView => blueCursorView.HideResponse());
    }

    private void ForEachCompanionView(Action<BlueCursorView> applyToView)
    {
        dispatcher.VerifyAccess();
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows) applyToView(cursorOverlayWindow.BlueCursorView);
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
                cursorOverlayWindow.BlueCursorView.SetVoiceState(currentVoiceState);
                if (currentResponseText is not null) cursorOverlayWindow.BlueCursorView.ShowResponseText(currentResponseText);
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
        CompositionTarget.Rendering -= FollowMouseForRenderedFrame;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate void WinEventCallback(IntPtr eventHook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHookModule,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr eventHook);
}

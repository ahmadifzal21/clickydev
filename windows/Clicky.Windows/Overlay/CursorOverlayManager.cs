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

    // Mac timings: first appearance fades in over 2 s; transient mode waits 1 s after the interaction (and any
    // pointing flight) ends, then fades out over 0.4 s. Both ease in.
    private const double FirstAppearanceFadeInSeconds = 2.0;
    private const double TransientHideDelaySeconds = 1.0;
    private const double FadeOutSeconds = 0.4;
    private bool isCursorAlwaysVisible;
    private double? fadeInElapsedSeconds;
    private double? transientHideCountdownSeconds;
    private double? fadeOutElapsedSeconds;

    /// <summary>
    /// Creates the overlays. With <paramref name="isCursorAlwaysVisible"/> ("Show Clicky") they appear with the Mac's
    /// first-appearance fade-in; otherwise they stay hidden until <see cref="ShowTransiently"/>.
    /// </summary>
    public CursorOverlayManager(DisplayMonitorCatalog displayMonitorCatalog, bool isCursorAlwaysVisible)
    {
        this.displayMonitorCatalog = displayMonitorCatalog;
        this.isCursorAlwaysVisible = isCursorAlwaysVisible;
        foregroundChangedCallback = HandleForegroundChanged;
        foregroundEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            foregroundChangedCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (foregroundEventHook == IntPtr.Zero)
            Trace.TraceWarning("Could not watch foreground changes; Clicky overlays may be covered by another topmost window.");
        displayMonitorCatalog.MonitorsChanged += RebuildOverlayWindows;
        RebuildOverlayWindows();
        if (isCursorAlwaysVisible)
        {
            fadeInElapsedSeconds = 0;
            ShowOverlays();
        }
    }

    /// <summary>Raised when the overlay is shown or hidden, or the voice state changes; the tray panel mirrors it.</summary>
    public event Action<bool, CompanionVoiceState>? CompanionStatusChanged;

    public bool IsOverlayVisible { get; private set; }

    /// <summary>True while any overlay's buddy is flying to, pointing at, or flying back from a target.</summary>
    public bool IsPointing => cursorOverlayWindows.Any(cursorOverlayWindow => cursorOverlayWindow.IsPointing);

    /// <summary>True while any overlay still shows (or fades) a reply bubble.</summary>
    public bool IsShowingResponse => cursorOverlayWindows.Any(cursorOverlayWindow => cursorOverlayWindow.IsShowingResponse);

    /// <summary>
    /// "Show Clicky" toggled. Like the Mac's <c>setClickyCursorEnabled</c>, on shows immediately and off hides
    /// immediately, dropping any pending transient hide. Unchanged values are ignored so unrelated config saves
    /// don't hide a transiently shown buddy.
    /// </summary>
    public void SetCursorAlwaysVisible(bool isAlwaysVisible)
    {
        dispatcher.VerifyAccess();
        if (isAlwaysVisible == isCursorAlwaysVisible) return;
        isCursorAlwaysVisible = isAlwaysVisible;
        transientHideCountdownSeconds = null;
        if (isAlwaysVisible) ShowOverlays();
        else HideOverlays();
    }

    /// <summary>An interaction is starting: keep the buddy up, bringing it back transiently if "Show Clicky" is off.</summary>
    public void ShowTransiently()
    {
        dispatcher.VerifyAccess();
        transientHideCountdownSeconds = null;
        if (!IsOverlayVisible || fadeOutElapsedSeconds is not null) ShowOverlays();
    }

    /// <summary>
    /// The interaction (including speech) is over. In transient mode, fade the buddy out once any pointing flight
    /// has landed back at the mouse and a further second has passed; a new <see cref="ShowTransiently"/> cancels it.
    /// </summary>
    public void ScheduleTransientHide()
    {
        dispatcher.VerifyAccess();
        if (!isCursorAlwaysVisible && IsOverlayVisible) transientHideCountdownSeconds = TransientHideDelaySeconds;
    }

    private void ShowOverlays()
    {
        fadeOutElapsedSeconds = null;
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows)
        {
            cursorOverlayWindow.Opacity = fadeInElapsedSeconds is null ? 1 : 0;
            try
            {
                cursorOverlayWindow.Show();
            }
            catch (Win32Exception exception)
            {
                Trace.TraceWarning($"Could not show a Clicky overlay: {exception.Message}");
            }
        }
        if (IsOverlayVisible) return;
        IsOverlayVisible = true;
        lastRenderingTime = default;
        // Per-frame like the Mac's 16 ms tracking timer, but synced to WPF's own render so motion never tears.
        // Subscribed only while visible: a hidden Clicky should cost nothing.
        CompositionTarget.Rendering += FollowMouseForRenderedFrame;
        CompanionStatusChanged?.Invoke(true, currentVoiceState);
    }

    private void HideOverlays()
    {
        fadeInElapsedSeconds = null;
        fadeOutElapsedSeconds = null;
        transientHideCountdownSeconds = null;
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows)
        {
            cursorOverlayWindow.CancelPointing();
            cursorOverlayWindow.Hide();
        }
        if (!IsOverlayVisible) return;
        IsOverlayVisible = false;
        CompositionTarget.Rendering -= FollowMouseForRenderedFrame;
        CompanionStatusChanged?.Invoke(false, currentVoiceState);
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
        AdvanceFrame(physicalMousePosition.X, physicalMousePosition.Y, elapsedSeconds);
    }

    /// <summary>One rendered frame: moves every buddy and steps the overlay fades. Tests drive it directly.</summary>
    internal void AdvanceFrame(double physicalMouseX, double physicalMouseY, double elapsedSeconds)
    {
        bool isAnyOverlayPointing = cursorOverlayWindows.Any(cursorOverlayWindow => cursorOverlayWindow.IsPointing);
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows)
            cursorOverlayWindow.FollowMouse(physicalMouseX, physicalMouseY,
                isAnyOverlayPointing && !cursorOverlayWindow.IsPointing, elapsedSeconds);

        if (fadeInElapsedSeconds is double fadeInElapsed)
        {
            fadeInElapsed += elapsedSeconds;
            fadeInElapsedSeconds = fadeInElapsed < FirstAppearanceFadeInSeconds ? fadeInElapsed : null;
            SetOverlayOpacity(EaseIn(fadeInElapsed / FirstAppearanceFadeInSeconds));
        }

        if (transientHideCountdownSeconds is double hideCountdown)
        {
            // The Mac waits for the flight back to the mouse before starting its one-second pause; the reply bubble
            // lives in the overlay too, so it must finish its hold and fade first.
            hideCountdown = isAnyOverlayPointing || IsShowingResponse ? TransientHideDelaySeconds : hideCountdown - elapsedSeconds;
            transientHideCountdownSeconds = hideCountdown > 0 ? hideCountdown : null;
            if (hideCountdown <= 0)
            {
                fadeInElapsedSeconds = null;
                fadeOutElapsedSeconds = 0;
            }
        }

        if (fadeOutElapsedSeconds is double fadeOutElapsed)
        {
            fadeOutElapsed += elapsedSeconds;
            if (fadeOutElapsed >= FadeOutSeconds)
            {
                HideOverlays();
                return;
            }
            fadeOutElapsedSeconds = fadeOutElapsed;
            SetOverlayOpacity(1 - EaseIn(fadeOutElapsed / FadeOutSeconds));
        }
    }

    private void SetOverlayOpacity(double opacity)
    {
        foreach (CursorOverlayWindow cursorOverlayWindow in cursorOverlayWindows) cursorOverlayWindow.Opacity = opacity;
    }

    private static double EaseIn(double progress)
    {
        double clampedProgress = Math.Clamp(progress, 0, 1);
        return clampedProgress * clampedProgress;
    }

    /// <summary>
    /// Flies the buddy to a physical virtual-screen pixel, on the overlay of the monitor containing it; any other
    /// overlay's pointing is dropped so only one buddy is ever visible.
    /// </summary>
    public void PointAt(double physicalTargetX, double physicalTargetY, string? bubbleText)
    {
        dispatcher.VerifyAccess();
        // Hidden overlays don't run frames; callers show the buddy (ShowTransiently) before pointing.
        if (!IsOverlayVisible) return;
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
        CompanionStatusChanged?.Invoke(IsOverlayVisible, voiceState);
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
                if (!IsOverlayVisible) continue;
                // Mid-fade, start where the old windows were; the next frame continues the fade.
                cursorOverlayWindow.Opacity = cursorOverlayWindows.Count > 0 ? cursorOverlayWindows[0].Opacity : 1;
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
            // Hidden overlays may not have an HWND yet, and there is nothing to keep on top.
            if (isDisposed || !IsOverlayVisible) return;
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

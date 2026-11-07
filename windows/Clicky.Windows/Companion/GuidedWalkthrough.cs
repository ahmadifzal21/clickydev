using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Companion;

internal enum TurnTriggerKind
{
    /// <summary>The user asked something (voice or typed).</summary>
    Question,

    /// <summary>The user clicked the guided step's target.</summary>
    ClickedTarget,

    /// <summary>The user rested the mouse on a hover target.</summary>
    HoverRested,

    /// <summary>The user clicked or scrolled after a step without a target, and the screen changed.</summary>
    DidStep
}

/// <summary>Why a turn runs: a question, or something the user did on screen during a walkthrough (E11 T11.17).</summary>
internal sealed record TurnTrigger(TurnTriggerKind Kind, int? WalkthroughStep = null, string? AppFact = null)
{
    public static readonly TurnTrigger Question = new(TurnTriggerKind.Question);

    public bool IsWalkthrough => Kind != TurnTriggerKind.Question;
}

/// <summary>A guided step's target, in physical pixels: the box to click (snapped to the control when possible).</summary>
internal sealed record GuidedTarget(Rect Box, string Label, bool IsHover, int Step, PixelRect MonitorBounds, double DpiScale)
{
    /// <summary>Ring radius: half the box's diagonal, at least 28 and at most 180 (scaled for the monitor's DPI).</summary>
    public double RingRadius => Math.Clamp(Math.Sqrt(Box.Width * Box.Width + Box.Height * Box.Height) / 2, 28 * DpiScale, 180 * DpiScale);

    public Point Center => new(Box.X + Box.Width / 2, Box.Y + Box.Height / 2);

    /// <summary>A click counts if it lands in the box (with 8 px of slack) or inside the ring.</summary>
    public bool Contains(double physicalX, double physicalY)
    {
        Rect slack = Box;
        slack.Inflate(8 * DpiScale, 8 * DpiScale);
        return slack.Contains(physicalX, physicalY) || (new Point(physicalX, physicalY) - Center).Length <= RingRadius;
    }
}

/// <summary>What the walkthrough needs from the rest of the app; tests pass fakes.</summary>
internal sealed class GuidedWalkthroughHost
{
    public required Action<ScreenMark> DrawTarget { get; init; }
    public required Action RemoveTarget { get; init; }
    public required Action ClearDrawing { get; init; }
    public required Func<bool> HasDrawing { get; init; }
    public required Action InterruptTurn { get; init; }
    public required Func<TurnTrigger, Task> StartFollowUp { get; init; }
    public required Func<bool> IsTalkKeyHeld { get; init; }
    public Func<int> ForegroundProcessId { get; init; } = ForegroundAppInfo.ReadProcessId;
    public Func<int, int, bool> IsClickyWindowAt { get; init; } = ForegroundAppInfo.IsClickyWindowAt;
    public Func<Point> CursorPosition { get; init; } = ReadCursorPosition;

    private static Point ReadCursorPosition() =>
        System.Windows.Forms.Cursor.Position is var position ? new Point(position.X, position.Y) : default;
}

/// <summary>Where mouse events come from: the global hook in the app, a fake in tests.</summary>
internal interface IMouseEventSource
{
    event Action<LowLevelMouseEvent>? MouseEventObserved;
    IDisposable Acquire();
}

/// <summary>
/// The guided walkthrough loop (E11 T11.11, T11.12, T11.16, T11.17), on the UI thread:
/// <list type="bullet">
/// <item>A step's <c>click</c>/<c>hover</c> target gets a ring (after 450 ms). A click inside it, or a 2 s rest on a hover
/// target, interrupts the reply, waits for the screen to settle and starts the next step's turn. A click elsewhere ends the
/// guide and leaves a note for the next turn. The target expires after 10 minutes, when another app comes to the front, or
/// at the 15th step.</item>
/// <item>A step without a target whose last line is an instruction arms a step watcher for 120 s: the next click or scroll
/// (not while the talk key is held; typing never counts) that changes the screen by 3% or more starts the next turn.</item>
/// <item>Marks left on screen are cleared once the screen changes under them (a click that changes it, or an app switch).</item>
/// </list>
/// Clicks on Clicky's own windows are ignored throughout.
/// </summary>
internal sealed partial class GuidedWalkthrough : IDisposable
{
    public const int MaximumSteps = 15;
    internal static readonly TimeSpan TargetLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RingDelay = TimeSpan.FromMilliseconds(450);
    internal static readonly TimeSpan HoverRest = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan StepWatchLifetime = TimeSpan.FromSeconds(120);
    internal const double StepChangeFraction = 0.03;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan AfterClickFallback = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AfterStepFallback = TimeSpan.FromMilliseconds(800);

    private readonly IMouseEventSource mouseEventSource;
    private readonly ScreenSettleWaiter screenSettleWaiter;
    private readonly GuidedWalkthroughHost host;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer tickTimer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<string> pendingNotes = [];
    private IDisposable? mouseLease;
    private int tickCount;

    private GuidedTarget? target;
    private TimeSpan targetArmedAt;
    private int targetFrontProcessId;
    private bool isRingShown;
    private TimeSpan hoverRest;

    private (PixelRect Monitor, byte[]? Baseline, int Step, TimeSpan ArmedAt)? stepWatch;
    private (PixelRect Monitor, int FrontProcessId)? staleWatch;
    private bool isCheckingTheScreen;

    public GuidedWalkthrough(IMouseEventSource mouseEventSource, ScreenSettleWaiter screenSettleWaiter, GuidedWalkthroughHost host)
    {
        this.mouseEventSource = mouseEventSource;
        this.screenSettleWaiter = screenSettleWaiter;
        this.host = host;
        dispatcher = Dispatcher.CurrentDispatcher;
        tickTimer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        tickTimer.Stop();
        mouseEventSource.MouseEventObserved += OnMouseEventFromHookThread;
    }

    public bool HasTarget => target is not null;
    public bool IsWatchingForAStep => stepWatch is not null;
    public bool IsWatchingTheDrawing => staleWatch is not null;
    public GuidedTarget? Target => target;

    /// <summary>Notes for the next turn (e.g. "they clicked somewhere else"); taking them empties the list.</summary>
    public IReadOnlyList<string> TakeNotes()
    {
        string[] notes = [.. pendingNotes];
        pendingNotes.Clear();
        return notes;
    }

    public void ArmTarget(GuidedTarget guidedTarget)
    {
        DisarmTarget(null);
        stepWatch = null;
        if (guidedTarget.Step >= MaximumSteps)
        {
            Trace.WriteLine($"Guided target suppressed: step {guidedTarget.Step} is the walkthrough's last (\"{guidedTarget.Label}\").");
            return;
        }
        target = guidedTarget;
        targetArmedAt = clock.Elapsed;
        targetFrontProcessId = host.ForegroundProcessId();
        isRingShown = false;
        hoverRest = TimeSpan.Zero;
        Trace.WriteLine($"Guided target armed: \"{guidedTarget.Label}\" box {guidedTarget.Box} radius {guidedTarget.RingRadius:0} step {guidedTarget.Step}{(guidedTarget.IsHover ? " (hover)" : "")}.");
        StartWatching();
    }

    /// <summary>Takes the target down (null reason: silently, e.g. a newer target replaces it).</summary>
    public void DisarmTarget(string? reason)
    {
        if (target is null) return;
        if (reason is not null) Trace.WriteLine($"Guided target \"{target.Label}\" disarmed: {reason}.");
        target = null;
        if (isRingShown) host.RemoveTarget();
        isRingShown = false;
        StopWatchingIfIdle();
    }

    /// <summary>After a step without a target: the next click or scroll that changes this monitor moves the walkthrough on.</summary>
    public void ArmStepWatch(PixelRect monitorBounds, int step)
    {
        if (target is not null || step >= MaximumSteps) return;
        stepWatch = (monitorBounds, screenSettleWaiter.Sampler.Sample(monitorBounds), step, clock.Elapsed);
        Trace.WriteLine($"A step with no click target (step {step}): the next click or scroll that changes the screen moves on.");
        StartWatching();
    }

    /// <summary>After a reply's marks are done: they're cleared once the screen changes under them.</summary>
    public void WatchDrawing(PixelRect monitorBounds)
    {
        staleWatch = (monitorBounds, host.ForegroundProcessId());
        StartWatching();
    }

    /// <summary>A new question (or quitting): everything armed goes; notes stay for that question's turn.</summary>
    public void StopAll()
    {
        DisarmTarget("a new question started");
        stepWatch = null;
        staleWatch = null;
        StopWatchingIfIdle();
    }

    /// <summary>True when <paramref name="spokenLine"/> tells the user to do something on screen (not a question).</summary>
    public static bool IsInstruction(string spokenLine)
    {
        string line = spokenLine.Trim();
        return line.Length > 0 && !line.EndsWith('?') && InstructionVerbRegex().IsMatch(line);
    }

    [GeneratedRegex(@"\b(click|tap|press|open|select|choose|pick|scroll|drag|type|enter|hit|toggle|turn (on|off)|switch|expand|check|uncheck|go to|head to|right-click|double-click)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstructionVerbRegex();

    private void StartWatching()
    {
        mouseLease ??= mouseEventSource.Acquire();
        if (!tickTimer.IsEnabled) tickTimer.Start();
    }

    private void StopWatchingIfIdle()
    {
        if (target is not null || stepWatch is not null || staleWatch is not null || isCheckingTheScreen) return;
        tickTimer.Stop();
        mouseLease?.Dispose();
        mouseLease = null;
    }

    private void OnMouseEventFromHookThread(LowLevelMouseEvent mouseEvent)
    {
        // Moves are followed by polling in Tick; only clicks and scrolls are worth a trip to the UI thread.
        if (!mouseEvent.IsButtonDown && mouseEvent.Kind != LowLevelMouseEventKind.Wheel) return;
        dispatcher.BeginInvoke(() => HandleMouseEvent(mouseEvent));
    }

    internal void HandleMouseEvent(LowLevelMouseEvent mouseEvent)
    {
        if (host.IsClickyWindowAt(mouseEvent.X, mouseEvent.Y)) return;
        if (target is { } armedTarget && mouseEvent.IsButtonDown)
        {
            if (armedTarget.Contains(mouseEvent.X, mouseEvent.Y))
            {
                Trace.WriteLine($"Guided target hit: \"{armedTarget.Label}\" ({(new Point(mouseEvent.X, mouseEvent.Y) - armedTarget.Center).Length:0} px from its center): step {armedTarget.Step + 1} follows.");
                _ = CompleteTargetAsync(armedTarget, TurnTriggerKind.ClickedTarget);
                return;
            }
            pendingNotes.Add($"[App] Last time they clicked somewhere other than \"{armedTarget.Label}\", the step you asked for, so it probably wasn't done.");
            DisarmTarget("they clicked somewhere else");
        }
        if (isCheckingTheScreen) return;
        if (stepWatch is { } watch && !host.IsTalkKeyHeld())
        {
            _ = CheckStepAsync(watch, mouseEvent.Kind == LowLevelMouseEventKind.Wheel ? "scrolled" : "clicked");
            return;
        }
        if (staleWatch is { } drawingWatch && mouseEvent.IsButtonDown) _ = CheckDrawingAsync(drawingWatch.Monitor);
    }

    internal void Tick()
    {
        tickCount++;
        TimeSpan now = clock.Elapsed;
        if (target is { } armedTarget)
        {
            if (now - targetArmedAt >= TargetLifetime) DisarmTarget("it expired");
            else if (tickCount % 4 == 0 && host.ForegroundProcessId() is var frontProcess && frontProcess != 0
                     && frontProcess != targetFrontProcessId && frontProcess != Environment.ProcessId)
                DisarmTarget("another app came to the front");
            else
            {
                if (!isRingShown && now - targetArmedAt >= RingDelay)
                {
                    host.DrawTarget(new ScreenMark(ScreenMarkKind.TargetRing, [armedTarget.Center]) { Radius = armedTarget.RingRadius });
                    isRingShown = true;
                }
                if (armedTarget.IsHover)
                {
                    Point cursor = host.CursorPosition();
                    hoverRest = armedTarget.Contains(cursor.X, cursor.Y) ? hoverRest + TickInterval : TimeSpan.Zero;
                    if (hoverRest >= HoverRest)
                    {
                        Trace.WriteLine($"Guided target \"{armedTarget.Label}\" rested on for 2 s: step {armedTarget.Step + 1} follows.");
                        _ = CompleteTargetAsync(armedTarget, TurnTriggerKind.HoverRested);
                    }
                }
            }
        }
        if (stepWatch is { } watch && now - watch.ArmedAt >= StepWatchLifetime)
        {
            Trace.WriteLine("Step watcher expired.");
            stepWatch = null;
        }
        if (staleWatch is { } drawingWatch && !isCheckingTheScreen)
        {
            if (!host.HasDrawing()) staleWatch = null;
            else if (tickCount % 4 == 0 && host.ForegroundProcessId() is var frontProcess && frontProcess != 0
                     && frontProcess != drawingWatch.FrontProcessId && frontProcess != Environment.ProcessId)
            {
                Trace.WriteLine("The drawing went stale (another app came to the front): clearing it.");
                host.ClearDrawing();
                staleWatch = null;
            }
        }
        StopWatchingIfIdle();
    }

    private async Task CompleteTargetAsync(GuidedTarget completedTarget, TurnTriggerKind triggerKind)
    {
        byte[]? atTheClick = screenSettleWaiter.Sampler.Sample(completedTarget.MonitorBounds);
        DisarmTarget(null);
        stepWatch = null;
        staleWatch = null;
        host.InterruptTurn();
        host.ClearDrawing();
        isCheckingTheScreen = true;
        try
        {
            (bool changed, TimeSpan waited) = await screenSettleWaiter.WaitAsync(completedTarget.MonitorBounds, atTheClick, AfterClickFallback, CancellationToken.None);
            Trace.WriteLine($"Screen settled {waited.TotalMilliseconds:0} ms after the step ({(changed ? "changed" : "unchanged")}).");
            string screenNow = changed ? "The screen changed; this is how it looks now." : "The screen hasn't visibly changed; this is how it looks now.";
            string fact = triggerKind == TurnTriggerKind.HoverRested
                ? $"[App] They rested the mouse on \"{completedTarget.Label}\", the step you asked for. {screenNow}"
                : $"[App] They clicked \"{completedTarget.Label}\", the step you asked for. {screenNow}";
            isCheckingTheScreen = false;
            StopWatchingIfIdle();
            await host.StartFollowUp(new TurnTrigger(triggerKind, completedTarget.Step + 1, fact));
        }
        finally
        {
            isCheckingTheScreen = false;
        }
    }

    private async Task CheckStepAsync((PixelRect Monitor, byte[]? Baseline, int Step, TimeSpan ArmedAt) watch, string action)
    {
        isCheckingTheScreen = true;
        try
        {
            (_, TimeSpan waited) = await screenSettleWaiter.WaitAsync(watch.Monitor, null, AfterStepFallback, CancellationToken.None);
            if (stepWatch != watch) return; // Replaced or stopped meanwhile.
            byte[]? now = screenSettleWaiter.Sampler.Sample(watch.Monitor);
            double changedFraction = watch.Baseline is null || now is null ? 1 : ScreenSettleWaiter.ChangedFraction(now, watch.Baseline);
            if (changedFraction < StepChangeFraction)
            {
                Trace.WriteLine($"They {action}, but the screen barely changed ({changedFraction:P1}): not a step.");
                return;
            }
            Trace.WriteLine($"They {action} and the screen changed ({changedFraction:P1}, settled in {waited.TotalMilliseconds:0} ms): moving on.");
            stepWatch = null;
            staleWatch = null;
            host.InterruptTurn();
            host.ClearDrawing();
            isCheckingTheScreen = false;
            StopWatchingIfIdle();
            await host.StartFollowUp(new TurnTrigger(TurnTriggerKind.DidStep, watch.Step + 1,
                $"[App] They {action} on the screen after your last step, and it changed. If that step is done, give the next one briefly; if it isn't, help them do it."));
        }
        finally
        {
            isCheckingTheScreen = false;
            StopWatchingIfIdle();
        }
    }

    private async Task CheckDrawingAsync(PixelRect monitorBounds)
    {
        isCheckingTheScreen = true;
        try
        {
            (bool changed, _) = await screenSettleWaiter.WaitAsync(monitorBounds, null, AfterClickFallback, CancellationToken.None);
            if (!changed || staleWatch is null) return;
            Trace.WriteLine("The screen changed under the drawing (click): clearing it.");
            host.ClearDrawing();
            staleWatch = null;
        }
        finally
        {
            isCheckingTheScreen = false;
            StopWatchingIfIdle();
        }
    }

    public void Dispose()
    {
        mouseEventSource.MouseEventObserved -= OnMouseEventFromHookThread;
        tickTimer.Stop();
        mouseLease?.Dispose();
        mouseLease = null;
    }
}

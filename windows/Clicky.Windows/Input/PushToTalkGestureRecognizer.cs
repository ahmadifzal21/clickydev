namespace Clicky.Windows.Input;

/// <summary>What the push-to-talk shortcut asks the microphone to do.</summary>
public enum PushToTalkGestureAction
{
    /// <summary>Start recording (and capture the screen): a press, unless it ends hands-free listening.</summary>
    StartListening,

    /// <summary>Stop recording and answer what was said.</summary>
    StopAndAnswer,

    /// <summary>Stop recording and drop it quietly: a quick tap is never a question.</summary>
    StopAndDiscard
}

/// <summary>
/// Turns the shortcut's presses and releases into listening, with the timings HeyClicky uses:
/// <list type="bullet">
/// <item>Hold (longer than <see cref="TapMaximumDuration"/>), then release: push-to-talk, as before.</item>
/// <item>Two quick taps (each shorter than <see cref="TapMaximumDuration"/>, the second pressed within
/// <see cref="DoubleTapMaximumGap"/> of the first's release): hands-free. Recording started on the second press keeps going
/// after its release, until the next press of the shortcut (answer) or <see cref="HandsFreeMaximumDuration"/> (answer).</item>
/// <item>A single quick tap: recording is dropped.</item>
/// </list>
/// Pure: the caller passes the time of each event and calls <see cref="HandleTimeElapsed"/> for the hands-free cap.
/// </summary>
public sealed class PushToTalkGestureRecognizer
{
    public static readonly TimeSpan TapMaximumDuration = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan DoubleTapMaximumGap = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan HandsFreeMaximumDuration = TimeSpan.FromSeconds(60);

    private enum GestureState { Idle, Holding, HandsFree, WaitingForReleaseAfterHandsFree }

    private GestureState state = GestureState.Idle;
    private TimeSpan pressedAt;
    private bool isPressAfterQuickTap;
    private TimeSpan? lastTapReleasedAt;

    public bool IsHandsFree => state == GestureState.HandsFree;

    /// <summary>When hands-free listening stops on its own, or null when it isn't on.</summary>
    public TimeSpan? HandsFreeDeadline { get; private set; }

    /// <param name="now">Time of the event on any steady clock (the same one for every call).</param>
    public PushToTalkGestureAction? Handle(PushToTalkShortcutTransition transition, TimeSpan now)
    {
        switch (state, transition)
        {
            case (GestureState.Idle, PushToTalkShortcutTransition.Pressed):
                state = GestureState.Holding;
                pressedAt = now;
                isPressAfterQuickTap = lastTapReleasedAt is { } tapReleasedAt && now - tapReleasedAt <= DoubleTapMaximumGap;
                lastTapReleasedAt = null;
                return PushToTalkGestureAction.StartListening;

            case (GestureState.Holding, PushToTalkShortcutTransition.Released):
                if (now - pressedAt >= TapMaximumDuration)
                {
                    state = GestureState.Idle;
                    return PushToTalkGestureAction.StopAndAnswer;
                }
                if (isPressAfterQuickTap)
                {
                    state = GestureState.HandsFree; // The recording started on this press keeps going.
                    HandsFreeDeadline = pressedAt + HandsFreeMaximumDuration;
                    return null;
                }
                state = GestureState.Idle;
                lastTapReleasedAt = now;
                return PushToTalkGestureAction.StopAndDiscard;

            case (GestureState.HandsFree, PushToTalkShortcutTransition.Pressed):
                state = GestureState.WaitingForReleaseAfterHandsFree; // This press only ends listening; its release is ignored.
                HandsFreeDeadline = null;
                return PushToTalkGestureAction.StopAndAnswer;

            case (GestureState.WaitingForReleaseAfterHandsFree, PushToTalkShortcutTransition.Released):
                state = GestureState.Idle;
                return null;

            default:
                return null;
        }
    }

    /// <summary>Ends hands-free listening once its cap has passed.</summary>
    public PushToTalkGestureAction? HandleTimeElapsed(TimeSpan now)
    {
        if (state != GestureState.HandsFree || HandsFreeDeadline is not { } deadline || now < deadline) return null;
        state = GestureState.Idle;
        HandsFreeDeadline = null;
        return PushToTalkGestureAction.StopAndAnswer;
    }
}

namespace Clicky.Windows.Companion;

/// <summary>What just happened in an interaction; <see cref="CompanionInteractionStateMachine"/> maps it to a voice state.</summary>
public enum CompanionInteractionTrigger
{
    /// The push-to-talk shortcut went down. Always wins: it interrupts whatever Clicky was doing.
    ShortcutPressed,

    /// The shortcut came up with a usable clip; transcription starts.
    RecordingFinished,

    /// The tray's typed question started. Ignored while a voice turn is listening or processing.
    TypedQuestionAsked,

    /// The reply is in and is being spoken.
    ReplyReady,

    /// Done: spoken, nothing heard, an error, or cancelled.
    InteractionEnded
}

/// <summary>
/// The single owner of the companion's <see cref="CompanionVoiceState"/> (Mac: CompanionManager.voiceState). Every part of an
/// interaction reports a trigger here instead of setting the overlay's state itself, so two paths can't fight over it.
/// UI-thread only.
/// </summary>
internal sealed class CompanionInteractionStateMachine(Action<CompanionVoiceState> applyVoiceState)
{
    public CompanionVoiceState CurrentState { get; private set; } = CompanionVoiceState.Idle;

    /// <summary>Applies <paramref name="trigger"/>; false when it doesn't fit the current state (nothing changes).</summary>
    public bool Apply(CompanionInteractionTrigger trigger)
    {
        if (GetNextState(CurrentState, trigger) is not { } nextState) return false;
        if (nextState != CurrentState)
        {
            CurrentState = nextState;
            applyVoiceState(nextState);
        }
        return true;
    }

    /// <summary>The pure transition table; null means the trigger is ignored in <paramref name="currentState"/>.</summary>
    internal static CompanionVoiceState? GetNextState(CompanionVoiceState currentState, CompanionInteractionTrigger trigger) =>
        (trigger, currentState) switch
        {
            (CompanionInteractionTrigger.ShortcutPressed, _) => CompanionVoiceState.Listening,
            (CompanionInteractionTrigger.RecordingFinished, CompanionVoiceState.Listening) => CompanionVoiceState.Processing,
            (CompanionInteractionTrigger.TypedQuestionAsked, CompanionVoiceState.Idle or CompanionVoiceState.Responding)
                => CompanionVoiceState.Processing,
            (CompanionInteractionTrigger.ReplyReady, CompanionVoiceState.Processing) => CompanionVoiceState.Responding,
            (CompanionInteractionTrigger.InteractionEnded, _) => CompanionVoiceState.Idle,
            _ => null
        };
}

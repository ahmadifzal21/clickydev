namespace Clicky.Windows.Companion;

/// <summary>
/// Where the companion is in a push-to-talk interaction. Mirrors
/// CompanionVoiceState in CompanionManager.swift.
/// </summary>
public enum CompanionVoiceState
{
    /// Waiting for the push-to-talk shortcut. The blue cursor follows the mouse.
    Idle,

    /// The shortcut is held and the microphone is recording. The overlay shows the waveform.
    Listening,

    /// Transcribing, capturing screens, and waiting on the AI backend. The overlay shows a spinner.
    Processing,

    /// The response is being spoken aloud.
    Responding
}

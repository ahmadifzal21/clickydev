using Clicky.Windows.Companion;
using Clicky.Windows.Input;

namespace Clicky.Windows.Tray;

/// <summary>Which design-system color the panel's status dot uses.</summary>
public enum CompanionStatusDotColor
{
    /// DS.TextTertiary: the cursor overlay is hidden.
    Inactive,

    /// DS.Success: the cursor is out and waiting for push-to-talk.
    Ready,

    /// DS.Blue400: an interaction is in progress.
    Busy
}

/// <summary>
/// Pure text/state mapping for the companion panel (ported from statusText / statusDotColor in
/// CompanionPanelView.swift), kept out of the window so it can be unit tested.
/// </summary>
internal static class CompanionPanelText
{
    /// <summary>
    /// "Ready" while the cursor overlay is hidden, otherwise the voice state. The Mac says
    /// "Processing"; the Windows tickets call that state "Thinking", which reads better to a user.
    /// </summary>
    /// <summary>
    /// The panel's note for a turn event it shows (it follows the turn event log), or null to leave the note as it is. Answers
    /// replace "Heard: …" so reopening the panel shows how the last turn ended.
    /// </summary>
    public static string? GetTurnNote(CompanionTurnEvent turnEvent) => turnEvent.Kind switch
    {
        CompanionTurnEventKind.ReplyReady => "Answered: " + ReplyProgramReader.ReadAll(turnEvent.Text).VisibleText,
        CompanionTurnEventKind.Failed => turnEvent.Text,
        _ => null
    };

    public static string GetStatusText(bool isCursorOverlayVisible, CompanionVoiceState voiceState)
    {
        if (!isCursorOverlayVisible)
        {
            return "Ready";
        }

        return voiceState switch
        {
            CompanionVoiceState.Idle => "Active",
            CompanionVoiceState.Listening => "Listening",
            CompanionVoiceState.Processing => "Thinking",
            CompanionVoiceState.Responding => "Responding",
            _ => throw new ArgumentOutOfRangeException(nameof(voiceState), voiceState, "Unknown voice state")
        };
    }

    public static CompanionStatusDotColor GetStatusDotColor(bool isCursorOverlayVisible, CompanionVoiceState voiceState)
    {
        if (!isCursorOverlayVisible)
        {
            return CompanionStatusDotColor.Inactive;
        }

        return voiceState == CompanionVoiceState.Idle ? CompanionStatusDotColor.Ready : CompanionStatusDotColor.Busy;
    }

    /// <summary>
    /// Turns a config shortcut like "ctrl+alt" or " Ctrl + shift+F9" into "Ctrl + Alt" /
    /// "Ctrl + Shift + F9" for display. Unknown key names are shown capitalized as typed.
    /// </summary>
    public static string FormatShortcutForDisplay(string configuredShortcut)
    {
        IEnumerable<string> displayKeyNames = configuredShortcut
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FormatKeyNameForDisplay);

        string formattedShortcut = string.Join(" + ", displayKeyNames);
        return formattedShortcut.Length == 0 ? "(no shortcut set)" : formattedShortcut;
    }

    /// <summary>The shortcut Clicky actually listens for: the configured one, or Ctrl + Alt if it doesn't parse.</summary>
    public static string FormatEffectiveShortcutForDisplay(string configuredShortcut)
    {
        return FormatShortcutForDisplay(PushToTalkShortcut.TryParse(configuredShortcut, out _)
            ? configuredShortcut
            : PushToTalkShortcut.DefaultShortcutText);
    }

    /// <summary>
    /// A note under the push-to-talk hint, or null when there's nothing to say: either the configured
    /// shortcut is invalid, or it is Ctrl + Alt, which is AltGr on many layouts (typing é, @, € ...
    /// would start Clicky listening).
    /// </summary>
    public static string? GetShortcutNote(string configuredShortcut)
    {
        if (!PushToTalkShortcut.TryParse(configuredShortcut, out PushToTalkShortcut shortcut))
        {
            return $"\"{configuredShortcut}\" isn't a valid shortcut, so Clicky uses Ctrl + Alt. "
                + "Fix pushToTalkShortcut in clicky.config.json (e.g. \"ctrl+shift+space\") and restart Clicky.";
        }

        const PushToTalkModifierKeys ControlAndAlt = PushToTalkModifierKeys.Control | PushToTalkModifierKeys.Alt;
        if ((shortcut.RequiredModifiers & ControlAndAlt) == ControlAndAlt)
        {
            return "If your keyboard has AltGr, typing AltGr characters also triggers this. "
                + "Set pushToTalkShortcut in clicky.config.json (e.g. \"ctrl+shift+space\") and restart Clicky.";
        }

        return null;
    }

    private static string FormatKeyNameForDisplay(string keyName)
    {
        return keyName.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "Ctrl",
            "alt" or "option" => "Alt",
            "shift" => "Shift",
            "win" or "windows" or "meta" or "cmd" => "Win",
            "space" => "Space",
            _ => keyName.Length <= 1 ? keyName.ToUpperInvariant() : char.ToUpperInvariant(keyName[0]) + keyName[1..]
        };
    }
}

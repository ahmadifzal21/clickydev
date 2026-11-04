using Clicky.Windows.Companion;

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

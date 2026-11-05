namespace Clicky.Windows.Input;

/// <summary>Modifier keys a push-to-talk shortcut can require. Either the left or right key satisfies each.</summary>
[Flags]
public enum PushToTalkModifierKeys
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// A parsed push-to-talk shortcut: one or more modifiers, plus optionally one ordinary key. The Mac
/// hard-codes its options (ctrl + option, ctrl + option + space, ...); on Windows the shortcut is a
/// config string like <c>"ctrl+alt"</c> or <c>"ctrl+shift+space"</c>, because ctrl+alt is AltGr on
/// many keyboard layouts and those users need to pick something else.
/// </summary>
/// <param name="TriggerVirtualKeyCode">Virtual key of the non-modifier key, or null for a modifier-only shortcut.</param>
public sealed record PushToTalkShortcut(PushToTalkModifierKeys RequiredModifiers, uint? TriggerVirtualKeyCode)
{
    public const string DefaultShortcutText = "ctrl+alt";

    public static PushToTalkShortcut Default { get; } = new(PushToTalkModifierKeys.Control | PushToTalkModifierKeys.Alt, null);

    /// <summary>
    /// Parses names joined by '+' (case and spaces ignored): ctrl/control, alt/option, shift, win,
    /// and at most one of space, a-z, 0-9, f1-f24. At least one modifier is required, because keys are
    /// never swallowed and a bare key would start listening on every ordinary keystroke.
    /// </summary>
    public static bool TryParse(string? shortcutText, out PushToTalkShortcut parsedShortcut)
    {
        parsedShortcut = Default;
        if (string.IsNullOrWhiteSpace(shortcutText))
        {
            return false;
        }

        PushToTalkModifierKeys requiredModifiers = PushToTalkModifierKeys.None;
        uint? triggerVirtualKeyCode = null;
        foreach (string keyName in shortcutText.Split('+', StringSplitOptions.TrimEntries))
        {
            PushToTalkModifierKeys modifier = ParseModifierName(keyName);
            if (modifier != PushToTalkModifierKeys.None)
            {
                if (requiredModifiers.HasFlag(modifier))
                {
                    return false;
                }

                requiredModifiers |= modifier;
            }
            else if (triggerVirtualKeyCode == null && ParseTriggerKeyName(keyName) is uint virtualKeyCode)
            {
                triggerVirtualKeyCode = virtualKeyCode;
            }
            else
            {
                return false;
            }
        }

        if (requiredModifiers == PushToTalkModifierKeys.None)
        {
            return false;
        }

        parsedShortcut = new PushToTalkShortcut(requiredModifiers, triggerVirtualKeyCode);
        return true;
    }

    private static PushToTalkModifierKeys ParseModifierName(string keyName) => keyName.ToLowerInvariant() switch
    {
        "ctrl" or "control" => PushToTalkModifierKeys.Control,
        "alt" or "option" => PushToTalkModifierKeys.Alt,
        "shift" => PushToTalkModifierKeys.Shift,
        "win" or "windows" => PushToTalkModifierKeys.Windows,
        _ => PushToTalkModifierKeys.None,
    };

    private static uint? ParseTriggerKeyName(string keyName)
    {
        string lowerKeyName = keyName.ToLowerInvariant();
        if (lowerKeyName == "space")
        {
            return 0x20; // VK_SPACE
        }

        if (lowerKeyName.Length == 1 && char.IsAsciiLetterOrDigit(lowerKeyName[0]))
        {
            return char.ToUpperInvariant(lowerKeyName[0]); // VK_0..VK_9 and VK_A..VK_Z equal their ASCII codes
        }

        if (lowerKeyName.Length is 2 or 3 && lowerKeyName[0] == 'f'
            && int.TryParse(lowerKeyName.AsSpan(1), System.Globalization.NumberStyles.None, null, out int functionKeyNumber)
            && functionKeyNumber is >= 1 and <= 24)
        {
            return (uint)(0x70 + functionKeyNumber - 1); // VK_F1 = 0x70
        }

        return null;
    }
}

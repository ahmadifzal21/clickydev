namespace Clicky.Windows.Input;

public enum PushToTalkShortcutTransition
{
    None,
    Pressed,
    Released,
}

/// <summary>
/// Turns raw key downs/ups into push-to-talk pressed/released transitions, porting the Mac's
/// BuddyPushToTalkShortcut.shortcutTransition. Pure (no Win32) so it's unit-tested directly.
/// The low-level hook reports no modifier flags, so this tracks which modifier keys are held.
/// <list type="bullet">
/// <item>Modifier-only shortcut (ctrl+alt): pressed while every required modifier is held by either
/// its left or right key, extra modifiers allowed (Mac: flags.contains); released when one lets go.</item>
/// <item>Shortcut with a key (ctrl+shift+space): pressed when that key goes down while the modifiers
/// are held; released when that key goes up, even if the modifiers were released first (as on Mac).</item>
/// </list>
/// Auto-repeat key-downs (a key-down for a key already held) never cause a transition.
/// </summary>
public sealed class PushToTalkShortcutStateMachine
{
    private readonly HashSet<uint> heldModifierVirtualKeyCodes = [];
    private bool isTriggerKeyHeld;

    public PushToTalkShortcutStateMachine(PushToTalkShortcut shortcut)
    {
        Shortcut = shortcut;
    }

    public PushToTalkShortcut Shortcut { get; }

    public bool IsShortcutPressed { get; private set; }

    public PushToTalkShortcutTransition HandleKeyboardEvent(LowLevelKeyboardEvent keyboardEvent)
    {
        uint virtualKeyCode = keyboardEvent.VirtualKeyCode;
        if (ModifierForVirtualKey(virtualKeyCode) != PushToTalkModifierKeys.None)
        {
            bool didHeldModifiersChange = keyboardEvent.IsKeyDown
                ? heldModifierVirtualKeyCodes.Add(virtualKeyCode)
                : heldModifierVirtualKeyCodes.Remove(virtualKeyCode);
            if (!didHeldModifiersChange || Shortcut.TriggerVirtualKeyCode != null)
            {
                return PushToTalkShortcutTransition.None;
            }

            return SetShortcutPressed(AreRequiredModifiersHeld());
        }

        if (virtualKeyCode != Shortcut.TriggerVirtualKeyCode)
        {
            return PushToTalkShortcutTransition.None;
        }

        if (keyboardEvent.IsKeyDown)
        {
            bool isAutoRepeat = isTriggerKeyHeld;
            isTriggerKeyHeld = true;
            return !isAutoRepeat && AreRequiredModifiersHeld()
                ? SetShortcutPressed(true)
                : PushToTalkShortcutTransition.None;
        }

        isTriggerKeyHeld = false;
        return SetShortcutPressed(false);
    }

    /// <summary>
    /// Forgets every held key and releases a press in progress. Called when the foreground window
    /// changes mid-press: a key-up can be lost then (e.g. a UAC / secure-desktop prompt sees it, not
    /// the hook), and a stuck "pressed" would leave Clicky listening forever. The user re-presses.
    /// </summary>
    public PushToTalkShortcutTransition ResetHeldKeys()
    {
        heldModifierVirtualKeyCodes.Clear();
        isTriggerKeyHeld = false;
        return SetShortcutPressed(false);
    }

    private PushToTalkShortcutTransition SetShortcutPressed(bool isPressedNow)
    {
        if (isPressedNow == IsShortcutPressed)
        {
            return PushToTalkShortcutTransition.None;
        }

        IsShortcutPressed = isPressedNow;
        return isPressedNow ? PushToTalkShortcutTransition.Pressed : PushToTalkShortcutTransition.Released;
    }

    private bool AreRequiredModifiersHeld()
    {
        PushToTalkModifierKeys heldModifiers = PushToTalkModifierKeys.None;
        foreach (uint heldVirtualKeyCode in heldModifierVirtualKeyCodes)
        {
            heldModifiers |= ModifierForVirtualKey(heldVirtualKeyCode);
        }

        return (heldModifiers & Shortcut.RequiredModifiers) == Shortcut.RequiredModifiers;
    }

    /// <summary>Maps left/right (and generic, for injected input) modifier virtual keys to their modifier.</summary>
    private static PushToTalkModifierKeys ModifierForVirtualKey(uint virtualKeyCode) => virtualKeyCode switch
    {
        0x10 or 0xA0 or 0xA1 => PushToTalkModifierKeys.Shift,   // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        0x11 or 0xA2 or 0xA3 => PushToTalkModifierKeys.Control, // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 => PushToTalkModifierKeys.Alt,     // VK_MENU, VK_LMENU, VK_RMENU
        0x5B or 0x5C => PushToTalkModifierKeys.Windows,         // VK_LWIN, VK_RWIN
        _ => PushToTalkModifierKeys.None,
    };
}

using Clicky.Windows.Input;
using static Clicky.Windows.Input.PushToTalkShortcutTransition;

namespace Clicky.Windows.Tests;

public sealed class PushToTalkShortcutStateMachineTests
{
    private const uint LeftControl = 0xA2, RightControl = 0xA3, LeftAlt = 0xA4, RightAlt = 0xA5, LeftShift = 0xA0;
    private const uint Space = 0x20, LetterT = 'T';

    private static PushToTalkShortcutStateMachine CreateStateMachine(string shortcutText)
    {
        Assert.True(PushToTalkShortcut.TryParse(shortcutText, out PushToTalkShortcut shortcut));
        return new PushToTalkShortcutStateMachine(shortcut);
    }

    private static PushToTalkShortcutTransition Down(PushToTalkShortcutStateMachine stateMachine, uint virtualKeyCode)
        => stateMachine.HandleKeyboardEvent(new LowLevelKeyboardEvent(virtualKeyCode, true));

    private static PushToTalkShortcutTransition Up(PushToTalkShortcutStateMachine stateMachine, uint virtualKeyCode)
        => stateMachine.HandleKeyboardEvent(new LowLevelKeyboardEvent(virtualKeyCode, false));

    [Theory]
    [InlineData(LeftControl, LeftAlt)]
    [InlineData(RightControl, RightAlt)]
    [InlineData(LeftAlt, RightControl)]
    [InlineData(0x11u, 0x12u)] // generic VK_CONTROL / VK_MENU, as injected input may send
    public void ModifierOnly_PressesWhenBothHeldWithEitherSide_AndReleasesWhenOneLetsGo(uint firstKey, uint secondKey)
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+alt");
        Assert.Equal(None, Down(stateMachine, firstKey));
        Assert.Equal(Pressed, Down(stateMachine, secondKey));
        Assert.True(stateMachine.IsShortcutPressed);
        Assert.Equal(Released, Up(stateMachine, firstKey));
        Assert.Equal(None, Up(stateMachine, secondKey));
        Assert.False(stateMachine.IsShortcutPressed);
    }

    [Fact]
    public void ModifierOnly_StaysPressedWhileTheOtherSideOfAModifierIsStillHeld()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+alt");
        Down(stateMachine, LeftControl);
        Down(stateMachine, RightControl);
        Assert.Equal(Pressed, Down(stateMachine, LeftAlt));
        Assert.Equal(None, Up(stateMachine, LeftControl));
        Assert.Equal(Released, Up(stateMachine, RightControl));
    }

    [Fact]
    public void ModifierOnly_AllowsExtraModifiersAndIgnoresOrdinaryKeys()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+alt");
        Down(stateMachine, LeftShift);
        Down(stateMachine, LeftControl);
        Assert.Equal(Pressed, Down(stateMachine, LeftAlt));
        Assert.Equal(None, Down(stateMachine, LetterT));
        Assert.Equal(None, Up(stateMachine, LetterT));
        Assert.Equal(None, Up(stateMachine, LeftShift));
        Assert.True(stateMachine.IsShortcutPressed);
    }

    [Fact]
    public void ModifierOnly_IgnoresAutoRepeat()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+alt");
        Down(stateMachine, LeftControl);
        Assert.Equal(Pressed, Down(stateMachine, LeftAlt));
        for (int repeat = 0; repeat < 5; repeat++)
        {
            Assert.Equal(None, Down(stateMachine, LeftAlt));
            Assert.Equal(None, Down(stateMachine, LeftControl));
        }

        Assert.Equal(Released, Up(stateMachine, LeftAlt));
    }

    [Fact]
    public void KeyShortcut_PressesOnKeyDownWithModifiers_AndReleasesOnKeyUpEvenAfterModifiersLetGo()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+shift+space");
        Assert.Equal(None, Down(stateMachine, LeftControl));
        Assert.Equal(None, Down(stateMachine, LeftShift));
        Assert.Equal(Pressed, Down(stateMachine, Space));
        Assert.Equal(None, Up(stateMachine, LeftShift));
        Assert.Equal(None, Up(stateMachine, LeftControl));
        Assert.True(stateMachine.IsShortcutPressed);
        Assert.Equal(Released, Up(stateMachine, Space));
    }

    [Fact]
    public void KeyShortcut_DoesNothingWithoutModifiers_OrForOtherKeys()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+shift+space");
        Assert.Equal(None, Down(stateMachine, Space));
        Assert.Equal(None, Up(stateMachine, Space));
        Down(stateMachine, LeftControl);
        Assert.Equal(None, Down(stateMachine, Space)); // shift missing
        Up(stateMachine, Space);
        Down(stateMachine, LeftShift);
        Assert.Equal(None, Down(stateMachine, LetterT));
        Assert.False(stateMachine.IsShortcutPressed);
    }

    [Fact]
    public void KeyShortcut_IgnoresAutoRepeat_IncludingRepeatsThatStartedBeforeTheModifiers()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+space");
        Down(stateMachine, Space);
        Down(stateMachine, LeftControl);
        Assert.Equal(None, Down(stateMachine, Space)); // repeat of a key held before ctrl: not a new press
        Assert.Equal(None, Up(stateMachine, Space));

        Assert.Equal(Pressed, Down(stateMachine, Space));
        Assert.Equal(None, Down(stateMachine, Space));
        Assert.Equal(None, Down(stateMachine, Space));
        Assert.Equal(Released, Up(stateMachine, Space));
    }

    [Fact]
    public void ResetHeldKeys_ReleasesAPressInProgress_AndLaterKeyUpsAreHarmless()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+alt");
        Down(stateMachine, LeftControl);
        Down(stateMachine, LeftAlt);
        Assert.Equal(Released, stateMachine.ResetHeldKeys());
        Assert.False(stateMachine.IsShortcutPressed);
        Assert.Equal(None, Up(stateMachine, LeftAlt));
        Assert.Equal(None, Up(stateMachine, LeftControl));
        Assert.Equal(None, stateMachine.ResetHeldKeys());

        // A fresh press works normally afterwards.
        Down(stateMachine, LeftControl);
        Assert.Equal(Pressed, Down(stateMachine, LeftAlt));
    }

    [Fact]
    public void ResetHeldKeys_ForgetsModifiersSoAMissedKeyUpCannotCauseAFalsePress()
    {
        PushToTalkShortcutStateMachine stateMachine = CreateStateMachine("ctrl+alt");
        Down(stateMachine, LeftControl); // its key-up is lost (e.g. to a secure-desktop prompt)
        stateMachine.ResetHeldKeys();
        Assert.Equal(None, Down(stateMachine, LeftAlt));
    }
}

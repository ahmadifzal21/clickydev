using Clicky.Windows.Input;

namespace Clicky.Windows.Tests;

public sealed class PushToTalkShortcutTests
{
    [Theory]
    [InlineData("ctrl+alt", PushToTalkModifierKeys.Control | PushToTalkModifierKeys.Alt, null)]
    [InlineData(" Control + Option ", PushToTalkModifierKeys.Control | PushToTalkModifierKeys.Alt, null)]
    [InlineData("ctrl+shift+space", PushToTalkModifierKeys.Control | PushToTalkModifierKeys.Shift, 0x20u)]
    [InlineData("SPACE+shift", PushToTalkModifierKeys.Shift, 0x20u)]
    [InlineData("win+alt+k", PushToTalkModifierKeys.Windows | PushToTalkModifierKeys.Alt, (uint)'K')]
    [InlineData("ctrl+7", PushToTalkModifierKeys.Control, (uint)'7')]
    [InlineData("alt+f1", PushToTalkModifierKeys.Alt, 0x70u)]
    [InlineData("ctrl+F24", PushToTalkModifierKeys.Control, 0x87u)]
    public void TryParse_AcceptsValidShortcuts(string shortcutText, PushToTalkModifierKeys expectedModifiers, uint? expectedTriggerKey)
    {
        Assert.True(PushToTalkShortcut.TryParse(shortcutText, out PushToTalkShortcut parsedShortcut));
        Assert.Equal(new PushToTalkShortcut(expectedModifiers, expectedTriggerKey), parsedShortcut);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("space")]           // no modifier: would fire on ordinary typing
    [InlineData("ctrl+ctrl")]       // duplicate modifier
    [InlineData("ctrl+a+b")]        // two trigger keys
    [InlineData("ctrl+")]           // empty part
    [InlineData("ctrl+hyper")]      // unknown name
    [InlineData("ctrl+f0")]
    [InlineData("ctrl+f25")]
    [InlineData("ctrl+f+1")]
    [InlineData("ctrl+é")]
    public void TryParse_RejectsInvalidShortcutsAndFallsBackToDefault(string? shortcutText)
    {
        Assert.False(PushToTalkShortcut.TryParse(shortcutText, out PushToTalkShortcut parsedShortcut));
        Assert.Equal(PushToTalkShortcut.Default, parsedShortcut);
    }

    [Fact]
    public void Default_MatchesTheDefaultShortcutText()
    {
        Assert.True(PushToTalkShortcut.TryParse(PushToTalkShortcut.DefaultShortcutText, out PushToTalkShortcut parsedShortcut));
        Assert.Equal(PushToTalkShortcut.Default, parsedShortcut);
    }
}

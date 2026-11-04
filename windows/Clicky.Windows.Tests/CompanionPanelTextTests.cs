using Clicky.Windows.Companion;
using Clicky.Windows.Tray;

namespace Clicky.Windows.Tests;

public sealed class CompanionPanelTextTests
{
    [Theory]
    [InlineData(false, CompanionVoiceState.Listening, "Ready")]
    [InlineData(true, CompanionVoiceState.Idle, "Active")]
    [InlineData(true, CompanionVoiceState.Listening, "Listening")]
    [InlineData(true, CompanionVoiceState.Processing, "Thinking")]
    [InlineData(true, CompanionVoiceState.Responding, "Responding")]
    public void StatusText_MatchesOverlayAndVoiceState(bool isCursorOverlayVisible, CompanionVoiceState voiceState, string expectedStatusText)
    {
        Assert.Equal(expectedStatusText, CompanionPanelText.GetStatusText(isCursorOverlayVisible, voiceState));
    }

    [Fact]
    public void StatusDot_InactiveReadyBusy()
    {
        Assert.Equal(CompanionStatusDotColor.Inactive, CompanionPanelText.GetStatusDotColor(false, CompanionVoiceState.Idle));
        Assert.Equal(CompanionStatusDotColor.Ready, CompanionPanelText.GetStatusDotColor(true, CompanionVoiceState.Idle));
        Assert.Equal(CompanionStatusDotColor.Busy, CompanionPanelText.GetStatusDotColor(true, CompanionVoiceState.Processing));
    }

    [Theory]
    [InlineData("ctrl+alt", "Ctrl + Alt")]
    [InlineData(" Ctrl + shift+F9", "Ctrl + Shift + F9")]
    [InlineData("win+space", "Win + Space")]
    [InlineData("alt+k", "Alt + K")]
    [InlineData("", "(no shortcut set)")]
    public void Shortcut_FormattedForDisplay(string configuredShortcut, string expectedDisplayText)
    {
        Assert.Equal(expectedDisplayText, CompanionPanelText.FormatShortcutForDisplay(configuredShortcut));
    }
}

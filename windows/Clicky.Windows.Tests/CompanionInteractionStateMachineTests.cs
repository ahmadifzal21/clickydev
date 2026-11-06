using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

public class CompanionInteractionStateMachineTests
{
    [Theory]
    [InlineData(CompanionVoiceState.Idle, CompanionInteractionTrigger.ShortcutPressed, CompanionVoiceState.Listening)]
    [InlineData(CompanionVoiceState.Processing, CompanionInteractionTrigger.ShortcutPressed, CompanionVoiceState.Listening)]
    [InlineData(CompanionVoiceState.Responding, CompanionInteractionTrigger.ShortcutPressed, CompanionVoiceState.Listening)]
    [InlineData(CompanionVoiceState.Listening, CompanionInteractionTrigger.RecordingFinished, CompanionVoiceState.Processing)]
    [InlineData(CompanionVoiceState.Idle, CompanionInteractionTrigger.TypedQuestionAsked, CompanionVoiceState.Processing)]
    [InlineData(CompanionVoiceState.Responding, CompanionInteractionTrigger.TypedQuestionAsked, CompanionVoiceState.Processing)]
    [InlineData(CompanionVoiceState.Processing, CompanionInteractionTrigger.ReplyReady, CompanionVoiceState.Responding)]
    [InlineData(CompanionVoiceState.Listening, CompanionInteractionTrigger.InteractionEnded, CompanionVoiceState.Idle)]
    [InlineData(CompanionVoiceState.Processing, CompanionInteractionTrigger.InteractionEnded, CompanionVoiceState.Idle)]
    [InlineData(CompanionVoiceState.Responding, CompanionInteractionTrigger.InteractionEnded, CompanionVoiceState.Idle)]
    public void GetNextState_AllowedTransitions(CompanionVoiceState current, CompanionInteractionTrigger trigger, CompanionVoiceState expected) =>
        Assert.Equal(expected, CompanionInteractionStateMachine.GetNextState(current, trigger));

    [Theory]
    [InlineData(CompanionVoiceState.Idle, CompanionInteractionTrigger.RecordingFinished)]
    [InlineData(CompanionVoiceState.Processing, CompanionInteractionTrigger.RecordingFinished)]
    [InlineData(CompanionVoiceState.Listening, CompanionInteractionTrigger.TypedQuestionAsked)]
    [InlineData(CompanionVoiceState.Processing, CompanionInteractionTrigger.TypedQuestionAsked)]
    [InlineData(CompanionVoiceState.Idle, CompanionInteractionTrigger.ReplyReady)]
    [InlineData(CompanionVoiceState.Listening, CompanionInteractionTrigger.ReplyReady)]
    public void GetNextState_IgnoresTriggersThatDontFit(CompanionVoiceState current, CompanionInteractionTrigger trigger) =>
        Assert.Null(CompanionInteractionStateMachine.GetNextState(current, trigger));

    [Fact]
    public void Apply_ReportsOnlyRealChanges()
    {
        List<CompanionVoiceState> appliedStates = [];
        CompanionInteractionStateMachine stateMachine = new(appliedStates.Add);

        Assert.True(stateMachine.Apply(CompanionInteractionTrigger.ShortcutPressed));
        Assert.True(stateMachine.Apply(CompanionInteractionTrigger.ShortcutPressed));
        Assert.False(stateMachine.Apply(CompanionInteractionTrigger.ReplyReady));
        Assert.True(stateMachine.Apply(CompanionInteractionTrigger.RecordingFinished));
        Assert.True(stateMachine.Apply(CompanionInteractionTrigger.ReplyReady));
        Assert.True(stateMachine.Apply(CompanionInteractionTrigger.InteractionEnded));

        Assert.Equal([CompanionVoiceState.Listening, CompanionVoiceState.Processing, CompanionVoiceState.Responding, CompanionVoiceState.Idle],
            appliedStates);
        Assert.Equal(CompanionVoiceState.Idle, stateMachine.CurrentState);
    }
}

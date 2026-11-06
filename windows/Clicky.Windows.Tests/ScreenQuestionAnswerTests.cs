using System.Windows.Threading;
using Clicky.Windows.AI;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

/// <summary>
/// T8.2: the answer step the voice pipeline shares with the typed question — real capture and overlay, fake backend and
/// speaker (no CLIs, no audio). Checks the spoken text, the voice states and the bubble.
/// </summary>
public class ScreenQuestionAnswerTests
{
    internal sealed class FakeSpeaker : ITextToSpeechSpeaker
    {
        public List<string> SpokenTexts { get; } = [];
        public List<CompanionVoiceState> StatesWhileSpeaking { get; } = [];
        public Func<CompanionVoiceState>? ReadCurrentState { get; set; }
        public event Action<string>? StatusChanged { add { } remove { } }
        public void Prepare(VoiceSettings voiceSettings) { }

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            SpokenTexts.Add(text);
            if (ReadCurrentState is not null) StatesWhileSpeaking.Add(ReadCurrentState());
            return Task.CompletedTask;
        }

        public bool IsSpeaking => false;
        public void StopSpeaking() { }
        public void Dispose() { }
    }

    private sealed class FakeBackend(Func<string> reply) : ICompanionChatBackend
    {
        public List<string> UserPrompts { get; } = [];
        public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
        public string DisplayName => "fake";

        public Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken)
        {
            UserPrompts.Add(companionChatRequest.UserPrompt);
            return Task.FromResult(reply());
        }
    }

    private static void RunAnswer(Func<string> reply, Action<string, FakeSpeaker, FakeBackend, List<CompanionVoiceState>, CursorOverlayManager> check) =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            List<CompanionVoiceState> voiceStates = [];
            CompanionInteractionStateMachine stateMachine = new(voiceState => { voiceStates.Add(voiceState); cursorOverlayManager.SetVoiceState(voiceState); });
            FakeSpeaker speaker = new() { ReadCurrentState = () => stateMachine.CurrentState };
            FakeBackend backend = new(reply);
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null }, [backend]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory(),
                stateMachine, speaker);

            // As the voice pipeline does: the transcript arrives while Processing.
            stateMachine.Apply(CompanionInteractionTrigger.ShortcutPressed);
            stateMachine.Apply(CompanionInteractionTrigger.RecordingFinished);
            Task<string> answerTask = runner.AnswerAsync("where is the start button");
            StaTestThread.WaitFor(answerTask);
            check(answerTask.Result, speaker, backend, voiceStates, cursorOverlayManager);
        });

    [Fact]
    public void Reply_IsSpokenWithoutThePointTag_PointsAndEndsIdle() => RunAnswer(
        () => "it's down in the bottom left. [POINT:5,5:start button]",
        (bubbleText, speaker, backend, voiceStates, cursorOverlayManager) =>
        {
            Assert.Equal(["where is the start button"], backend.UserPrompts);
            Assert.Equal("it's down in the bottom left.", bubbleText);
            Assert.Equal(["it's down in the bottom left."], speaker.SpokenTexts);
            Assert.Equal([CompanionVoiceState.Responding], speaker.StatesWhileSpeaking);
            Assert.Equal([CompanionVoiceState.Listening, CompanionVoiceState.Processing, CompanionVoiceState.Responding, CompanionVoiceState.Idle],
                voiceStates);
            Assert.True(cursorOverlayManager.IsPointing);
            Assert.True(cursorOverlayManager.IsShowingResponse);
        });

    [Fact]
    public void BackendError_IsShownInTheBubble_AndSpokenBriefly() => RunAnswer(
        () => throw new InvalidOperationException("Claude Code CLI isn't logged in."),
        (bubbleText, speaker, _, voiceStates, cursorOverlayManager) =>
        {
            Assert.Contains("Claude Code CLI isn't logged in.", bubbleText);
            Assert.StartsWith("Clicky couldn't answer:", bubbleText);
            Assert.Equal([TypedScreenQuestionRunner.SpokenErrorLine], speaker.SpokenTexts);
            Assert.Equal(CompanionVoiceState.Idle, voiceStates[^1]);
            Assert.False(cursorOverlayManager.IsPointing);
            Assert.True(cursorOverlayManager.IsShowingResponse);
        });
}

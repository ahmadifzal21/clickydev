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
        public Action? OnSpeak { get; set; }
        public event Action<string>? StatusChanged { add { } remove { } }
        public void Prepare(VoiceSettings voiceSettings) { }

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            SpokenTexts.Add(text);
            OnSpeak?.Invoke();
            if (ReadCurrentState is not null) StatesWhileSpeaking.Add(ReadCurrentState());
            return Task.CompletedTask;
        }

        public bool IsSpeaking => false;
        public int StopCount { get; private set; }
        public void StopSpeaking() => StopCount++;
        public void Dispose() { }
    }

    private sealed class FakeBackend(Func<CancellationToken, Task<string>> reply) : ICompanionChatBackend
    {
        public List<string> UserPrompts { get; } = [];
        public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
        public string DisplayName => "fake";

        public Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken)
        {
            UserPrompts.Add(companionChatRequest.UserPrompt);
            return reply(cancellationToken);
        }
    }

    private static void RunAnswer(Func<string> reply, Action<string, FakeSpeaker, FakeBackend, List<CompanionVoiceState>, CursorOverlayManager> check) =>
        RunAnswer(_ => Task.FromResult(reply()), (_, _) => { }, check);

    private static void RunAnswer(Func<CancellationToken, Task<string>> reply, Action<TypedScreenQuestionRunner, CompanionInteractionStateMachine> whileAnswering,
        Action<string, FakeSpeaker, FakeBackend, List<CompanionVoiceState>, CursorOverlayManager> check) =>
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
            whileAnswering(runner, stateMachine);
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

    [Fact]
    public void NewPress_CancelsTheInFlightAnswer_StopsSpeech_AndKeepsListening()
    {
        bool sawCancellation = false;
        TaskCompletionSource backendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunAnswer(
            async cancellationToken =>
            {
                backendStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { sawCancellation = true; throw; }
                return "never";
            },
            (runner, stateMachine) =>
            {
                StaTestThread.WaitFor(backendStarted.Task); // Interrupt mid-request, after the capture.
                // What the app does on a press: interrupt, then dictation starts listening.
                runner.InterruptCurrentAnswer();
                stateMachine.Apply(CompanionInteractionTrigger.ShortcutPressed);
            },
            (bubbleText, speaker, _, voiceStates, cursorOverlayManager) =>
            {
                Assert.True(sawCancellation);
                Assert.Equal("", bubbleText);
                Assert.Empty(speaker.SpokenTexts);
                Assert.Equal(1, speaker.StopCount);
                Assert.Equal(CompanionVoiceState.Listening, voiceStates[^1]);
                Assert.False(cursorOverlayManager.IsShowingResponse);
            });
    }

    [Fact]
    public void TransientMode_StaysUpThroughLongSpeech_ThenFadesOutAfterTheLastActivity() => StaTestThread.Run(() =>
    {
        using DisplayMonitorCatalog displayMonitorCatalog = new();
        using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
        CompanionInteractionStateMachine stateMachine = new(cursorOverlayManager.SetVoiceState);
        List<bool> visibleWhileSpeaking = [];
        FakeSpeaker speaker = new()
        {
            OnSpeak = () =>
            {
                // 20 s of speech, well past the bubble's 6 s hold and the 1 s pause.
                for (int frame = 0; frame < 40; frame++)
                {
                    cursorOverlayManager.AdvanceFrame(0, 0, 0.5);
                    visibleWhileSpeaking.Add(cursorOverlayManager.IsOverlayVisible);
                }
            }
        };
        ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null },
            [new FakeBackend(_ => Task.FromResult("a long answer. [POINT:none]"))]);
        TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory(),
            stateMachine, speaker);

        Assert.False(cursorOverlayManager.IsOverlayVisible);
        StaTestThread.WaitFor(runner.AnswerAsync("explain this"));

        Assert.Equal(40, visibleWhileSpeaking.Count);
        Assert.All(visibleWhileSpeaking, Assert.True);
        cursorOverlayManager.AdvanceFrame(0, 0, 0.9);
        Assert.True(cursorOverlayManager.IsOverlayVisible); // Inside the 1 s pause.
        for (int frame = 0; frame < 10 && cursorOverlayManager.IsOverlayVisible; frame++) cursorOverlayManager.AdvanceFrame(0, 0, 0.2);
        Assert.False(cursorOverlayManager.IsOverlayVisible);
    });
}

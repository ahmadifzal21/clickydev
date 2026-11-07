using Clicky.Windows.ScreenCapture;
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
        public event Action? SoundStarted;
        public void Prepare(VoiceSettings voiceSettings) { }

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            SpokenTexts.Add(text);
            SoundStarted?.Invoke();
            OnSpeak?.Invoke();
            if (ReadCurrentState is not null) StatesWhileSpeaking.Add(ReadCurrentState());
            return Task.CompletedTask;
        }

        public List<string> LinesStarted { get; } = [];

        public async Task SpeakLinesAsync(System.Threading.Channels.ChannelReader<SpokenLine> spokenLines, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (SpokenLine spokenLine in spokenLines.ReadAllAsync(cancellationToken))
                {
                    SpokenTexts.Add(spokenLine.Text);
                    if (SpokenTexts.Count == 1) SoundStarted?.Invoke();
                    LinesStarted.Add(spokenLine.Text);
                    spokenLine.OnStarted?.Invoke();
                    OnSpeak?.Invoke();
                    if (ReadCurrentState is not null) StatesWhileSpeaking.Add(ReadCurrentState());
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public bool IsSpeaking => false;
        public int StopCount { get; private set; }
        public void StopSpeaking() => StopCount++;
        public void Dispose() { }
    }

    private sealed class FakeBackend(Func<CancellationToken, Task<string>> reply) : ICompanionChatBackend
    {
        public List<string> UserPrompts { get; } = [];
        public List<string> ImageLabels { get; } = [];
        public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
        public string DisplayName => "fake";

        public Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
            Action<string>? onTextDelta = null)
        {
            UserPrompts.Add(companionChatRequest.UserPrompt);
            ImageLabels.AddRange(companionChatRequest.LabeledScreenshotImages.Select(image => image.ImageLabel));
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
    public void Turn_AppendsItsStagesToTheEventLog_InOrder() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            FakeSpeaker speaker = new();
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null },
                [new FakeBackend(_ => Task.FromResult("over there. [POINT:none]"))]);
            CompanionTurnEventLog turnEventLog = new();
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory(),
                textToSpeechSpeaker: speaker, turnEventLog: turnEventLog);
            int turnId = turnEventLog.StartTurn("release");
            turnEventLog.Append(turnId, CompanionTurnEventKind.TranscriptReady, "where is it");

            StaTestThread.WaitFor(runner.AnswerAsync("where is it", turnId: turnId));

            Assert.Equal([CompanionTurnEventKind.TurnStarted, CompanionTurnEventKind.TranscriptReady, CompanionTurnEventKind.CaptureDone,
                    CompanionTurnEventKind.ReplyReady, CompanionTurnEventKind.SpeechStarted, CompanionTurnEventKind.TurnEnded],
                turnEventLog.ReadTurn(turnId).Select(turnEvent => turnEvent.Kind));
            Assert.Equal("over there. [POINT:none]", turnEventLog.ReadTurn(turnId)[3].Text);
            Assert.Matches(@"^Turn 1 timing from release: transcript [\d.]+ s, capture [\d.]+ s, answer [\d.]+ s, first sound [\d.]+ s, done [\d.]+ s$",
                CompanionTurnTiming.Describe(turnEventLog.ReadTurn(turnId)));
        });

    /// <summary>Streams its reply in the given pieces, pausing between them so the UI thread can show and speak each one.</summary>
    private sealed class StreamingBackend(string[] pieces, Exception? failAfterPieces = null) : ICompanionChatBackend
    {
        public Func<int, Task>? AfterPiece { get; init; }
        public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
        public string DisplayName => "streaming fake";

        public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
            Action<string>? onTextDelta = null)
        {
            for (int index = 0; index < pieces.Length; index++)
            {
                onTextDelta?.Invoke(pieces[index]);
                await Task.Delay(30, cancellationToken);
                if (AfterPiece is not null) await AfterPiece(index);
            }
            if (failAfterPieces is not null) throw failAfterPieces;
            return string.Concat(pieces);
        }
    }

    [Fact]
    public void StreamedReply_IsSpokenLineByLine_StartingBeforeTheReplyIsComplete() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            FakeSpeaker speaker = new();
            List<string> spokenWhileStreaming = [];
            StreamingBackend backend = new(["That's the Save button. ", "Click it to keep", " your changes. [POINT:none]"])
            {
                AfterPiece = index => { if (index == 1) spokenWhileStreaming.AddRange(speaker.SpokenTexts); return Task.CompletedTask; }
            };
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null }, [backend]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory(),
                textToSpeechSpeaker: speaker);

            Task<string> answerTask = runner.AnswerAsync("where is save");
            StaTestThread.WaitFor(answerTask);

            Assert.Equal(["That's the Save button."], spokenWhileStreaming);
            Assert.Equal(["That's the Save button.", "Click it to keep your changes."], speaker.SpokenTexts);
            Assert.Equal("That's the Save button. Click it to keep your changes.", answerTask.Result);
            Assert.Contains(runner.TurnEventLog.ReadAfter(0), turnEvent => turnEvent.Kind == CompanionTurnEventKind.TextDelta);
        });

    [Fact]
    public void StreamThatBreaksAfterSpeechStarted_EndsWithTheErrorInsteadOfAFallback() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            FakeSpeaker speaker = new();
            FakeBackend fallback = new(_ => Task.FromResult("fallback answer"));
            StreamingBackend streamingBackend = new(["It's the Save button. ", "Then"], new InvalidOperationException("stream broke"));
            ChatBackendRouter router = new(() => new JobBackendRouting
            {
                Answer = ChatBackendKind.ClaudeCodeCli, Fallback = ChatBackendKind.CodexCli
            }, [streamingBackend, new CodexKindBackend(fallback)]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory(),
                textToSpeechSpeaker: speaker);

            Task<string> answerTask = runner.AnswerAsync("where is save");
            StaTestThread.WaitFor(answerTask);

            Assert.Equal("Clicky couldn't answer: stream broke", answerTask.Result);
            Assert.Empty(fallback.UserPrompts);
            Assert.Equal(["It's the Save button.", TypedScreenQuestionRunner.SpokenErrorLine], speaker.SpokenTexts);
        });

    /// <summary>Presents another fake as the Codex backend, so a router can hold it next to a Claude-kind fake.</summary>
    private sealed class CodexKindBackend(ICompanionChatBackend innerBackend) : ICompanionChatBackend
    {
        public ChatBackendKind BackendKind => ChatBackendKind.CodexCli;
        public string DisplayName => "codex fake";

        public Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
            Action<string>? onTextDelta = null) => innerBackend.GetResponseTextAsync(companionChatRequest, cancellationToken, onTextDelta);
    }

    private static (string BubbleText, FakeSpeaker Speaker, List<string> PointedLabels) RunReply(string reply, bool withSpeaker = true)
    {
        string bubbleText = "";
        FakeSpeaker speaker = new();
        List<string> pointedLabels = [];
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null },
                [new FakeBackend(_ => Task.FromResult(reply))]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory(),
                textToSpeechSpeaker: withSpeaker ? speaker : null);
            Task<string> answerTask = runner.AnswerAsync("how do i export a pdf");
            StaTestThread.WaitFor(answerTask);
            bubbleText = answerTask.Result;
            pointedLabels.AddRange(runner.TurnEventLog.ReadAfter(0)
                .Where(turnEvent => turnEvent.Kind == CompanionTurnEventKind.Pointed).Select(turnEvent => turnEvent.Text));
        });
        return (bubbleText, speaker, pointedLabels);
    }

    [Fact]
    public void Walkthrough_PointsAtEachStep_AsThatStepIsSpoken()
    {
        (string bubbleText, FakeSpeaker speaker, List<string> pointedLabels) = RunReply(
            "first, open the file menu. [POINT:24,12:file menu]\nthen pick export. [POINT:60,410:export]\nand hit create pdf. [POINT:520,300:create pdf]");

        Assert.Equal(["first, open the file menu.", "then pick export.", "and hit create pdf."], speaker.SpokenTexts);
        Assert.Equal(["file menu", "export", "create pdf"], pointedLabels);
        Assert.Equal("first, open the file menu.\nthen pick export.\nand hit create pdf.", bubbleText);
    }

    [Fact]
    public void ClassicReply_WithOneTagAtTheEnd_PointsOnce()
    {
        (string bubbleText, FakeSpeaker speaker, List<string> pointedLabels) = RunReply(
            "you'll want the color inspector up top. click it to get the color wheels. [POINT:1100,42:color inspector]");

        Assert.Equal(["you'll want the color inspector up top.", "click it to get the color wheels."], speaker.SpokenTexts);
        Assert.Equal(["color inspector"], pointedLabels);
        Assert.Equal("you'll want the color inspector up top. click it to get the color wheels.", bubbleText);
    }

    [Fact]
    public void WithoutSpeech_EachStepPointsInTurn_AfterTheLineBeforeHadItsReadingTime()
    {
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        (_, _, List<string> pointedLabels) = RunReply(
            "open file. [POINT:24,12:file menu]\nthen export. [POINT:60,410:export]", withSpeaker: false);

        Assert.Equal(["file menu", "export"], pointedLabels);
        Assert.True(stopwatch.Elapsed >= ReplyPlayback.ReadingTime(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ProgramReply_SpeaksEachSay_AndPointsAtItsMarksWhileThatLineIsSpoken()
    {
        (string bubbleText, FakeSpeaker speaker, List<string> pointedLabels) = RunReply("""
            ```clicky
            step(1)
            say("open the file menu up top.")
            point(24, 12, "file menu")
            say("then pick export.");  click(40, 400, 80, 420, "export")
            seen("exporting a document in Word")
            ```
            """);

        Assert.Equal(["open the file menu up top.", "then pick export."], speaker.SpokenTexts);
        Assert.Equal(["file menu", "export"], pointedLabels);
        Assert.Equal("open the file menu up top.\nthen pick export.", bubbleText);
    }

    [Fact]
    public void ProgramReply_StreamedInPieces_StartsSpeakingTheFirstSayBeforeTheRestArrives() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            FakeSpeaker speaker = new();
            List<string> spokenWhileStreaming = [];
            StreamingBackend backend = new(["```clicky\nsay(\"that's the save", " button.\")\npoint(5, 5, \"save\")\n", "say(\"click it.\")\n```"])
            {
                AfterPiece = index => { if (index == 1) spokenWhileStreaming.AddRange(speaker.SpokenTexts); return Task.CompletedTask; }
            };
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null }, [backend]);
            CompanionConversationHistory history = new();
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, history, textToSpeechSpeaker: speaker);

            StaTestThread.WaitFor(runner.AnswerAsync("where is save"));

            Assert.Equal(["that's the save button."], spokenWhileStreaming);
            Assert.Equal(["that's the save button.", "click it."], speaker.SpokenTexts);
            Assert.Equal("that's the save button.\nclick it.\n[showed: save]", history.GetExchanges()[0].AssistantResponse);
        });

    [Fact]
    public void PointNone_PointsNowhere_AndIsNotSpoken()
    {
        (string bubbleText, FakeSpeaker speaker, List<string> pointedLabels) = RunReply("html is the skeleton of a web page. [POINT:none]");

        Assert.Equal(["html is the skeleton of a web page."], speaker.SpokenTexts);
        Assert.Empty(pointedLabels);
        Assert.Equal("html is the skeleton of a web page.", bubbleText);
    }

    private static CompanionScreenCapture FakePressCapture()
    {
        PixelRect bounds = new(0, 0, 1920, 1080);
        return new CompanionScreenCapture([0xFF, 0xD8], "captured at the press", true,
            new DisplayMonitor("D", bounds, bounds, 96, 96, true), 1280, 720, ScreenCaptureMethod.WindowsGraphicsCapture, TimeSpan.Zero);
    }

    [Fact]
    public void CaptureFromThePress_IsSentInsteadOfANewOne() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            FakeBackend backend = new(_ => Task.FromResult("there. [POINT:none]"));
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null }, [backend]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory());

            StaTestThread.WaitFor(runner.AnswerAsync("where is it",
                screenCapturesTask: Task.FromResult<IReadOnlyList<CompanionScreenCapture>>([FakePressCapture()])));

            Assert.Equal(["captured at the press (image dimensions: 1280x720 pixels)"], backend.ImageLabels);
        });

    [Fact]
    public void CaptureFromThePress_IsHandedOutOnce() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            ChatBackendRouter router = new(() => new JobBackendRouting(), [new FakeBackend(_ => Task.FromResult(""))]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory());

            Assert.Null(runner.TakeCaptureFromPress());
            runner.CaptureOnPress();
            Task<IReadOnlyList<CompanionScreenCapture>>? pressCapture = runner.TakeCaptureFromPress();
            Assert.NotNull(pressCapture);
            Assert.Null(runner.TakeCaptureFromPress());
            StaTestThread.WaitFor(pressCapture!);
            Assert.NotEmpty(pressCapture!.Result);
        });

    [Fact]
    public void Interrupt_WhileWaitingForThePressCapture_CancelsTheTurn() =>
        StaTestThread.Run(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            FakeBackend backend = new(_ => Task.FromResult("never asked"));
            ChatBackendRouter router = new(() => new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli, Fallback = null }, [backend]);
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, new CompanionConversationHistory());
            TaskCompletionSource<IReadOnlyList<CompanionScreenCapture>> slowCapture = new();

            Task<string> answerTask = runner.AnswerAsync("where is it", screenCapturesTask: slowCapture.Task);
            runner.InterruptCurrentAnswer();
            StaTestThread.WaitFor(answerTask);

            Assert.Equal("", answerTask.Result);
            Assert.Empty(backend.UserPrompts);
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

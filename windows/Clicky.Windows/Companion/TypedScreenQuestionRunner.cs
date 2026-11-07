using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using Clicky.Windows.AI;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Companion;

/// <summary>
/// One answer turn (capture -> route -> bubble -> point -> speak), shared by push-to-talk (after transcription) and the tray's
/// "Ask about my screen (typed)" debug item. Runs on the UI thread, where the overlay manager and history live.
/// </summary>
internal sealed class TypedScreenQuestionRunner(
    CursorOverlayManager cursorOverlayManager, DisplayMonitorCatalog displayMonitorCatalog,
    ChatBackendRouter chatBackendRouter, CompanionConversationHistory conversationHistory,
    CompanionInteractionStateMachine? interactionStateMachine = null, ITextToSpeechSpeaker? textToSpeechSpeaker = null,
    CompanionTurnEventLog? turnEventLog = null)
{
    /// <summary>Every turn's stages go here; the timing line and the panel read them back.</summary>
    public CompanionTurnEventLog TurnEventLog { get; } = turnEventLog ?? new CompanionTurnEventLog();

    /// <summary>Spoken when a turn fails; the bubble carries the details.</summary>
    internal const string SpokenErrorLine = "Sorry, I couldn't answer that. The details are on screen.";

    private readonly CompanionInteractionStateMachine interactionStateMachine =
        interactionStateMachine ?? new CompanionInteractionStateMachine(cursorOverlayManager.SetVoiceState);
    private bool isRunning;

    /// <summary>The in-flight turn's cancellation; <see cref="InterruptCurrentAnswer"/> fires it.</summary>
    private CancellationTokenSource? currentAnswerCancellation;

    /// <summary>Started by <see cref="CaptureOnPress"/>, used once by the voice turn that the press leads to.</summary>
    private Task<IReadOnlyList<CompanionScreenCapture>>? captureFromPress;

    /// <summary>
    /// Push-to-talk press: capture now, while the user talks, so the turn doesn't wait for it after the release. The screen at
    /// the press is what the question is about (HeyClicky captures at release). A tap or an empty transcript leaves it unused;
    /// the next press replaces it.
    /// </summary>
    public void CaptureOnPress()
    {
        captureFromPress = CaptureThenPrestartAnswerAsync(
            CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync(displayMonitorCatalog.Monitors, CancellationToken.None));
        // A failure surfaces in the turn that uses it; one that is never used must not go unobserved.
        captureFromPress.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>
    /// Once the press's screenshots exist, starts the answer's CLI process too (E10 T10.6), so its start-up (about 1 s for Claude
    /// Code, 1.5 s for Codex) happens while the user is still talking. The turn awaits this task, so the process is ready first.
    /// </summary>
    private async Task<IReadOnlyList<CompanionScreenCapture>> CaptureThenPrestartAnswerAsync(Task<IReadOnlyList<CompanionScreenCapture>> capture)
    {
        IReadOnlyList<CompanionScreenCapture> screenCaptures = await capture.ConfigureAwait(false);
        chatBackendRouter.Prestart(CompanionJobKind.Answer, CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt,
            CompanionScreenQuestion.LabelScreenshots(screenCaptures));
        return screenCaptures;
    }

    /// <summary>The press led to no question (a tap, an empty transcript) or Clicky is quitting: kill the prestarted process.</summary>
    public void DiscardPrestartedAnswer() => chatBackendRouter.DiscardPrestarted();

    /// <summary>The press's capture for the voice turn that follows it (null if there was none); each capture is used once.</summary>
    public Task<IReadOnlyList<CompanionScreenCapture>>? TakeCaptureFromPress()
    {
        Task<IReadOnlyList<CompanionScreenCapture>>? pressCapture = captureFromPress;
        captureFromPress = null;
        return pressCapture;
    }

    /// <summary>
    /// A new push-to-talk press talks over Clicky (Mac: currentResponseTask?.cancel() + stopPlayback): cancels the in-flight
    /// turn (capture, the backend request — CLI process trees are killed — and speech), stops any speech and drops the bubble.
    /// </summary>
    public void InterruptCurrentAnswer()
    {
        currentAnswerCancellation?.Cancel();
        textToSpeechSpeaker?.StopSpeaking();
        cursorOverlayManager.HideResponse();
    }

    /// <summary>Asks for a question, answers it; clicks while a question is in flight are ignored.</summary>
    public async void Run()
    {
        if (isRunning) return;
        // shortcut: a modal VB InputBox is enough for a debug entry point; build a styled box if this ever ships to users.
        string userQuestion = Microsoft.VisualBasic.Interaction.InputBox("Ask Clicky about your screen:", "Clicky").Trim();
        if (userQuestion.Length == 0) return;
        // A voice turn that started while the box was open keeps the overlay; the typed question waits for another click.
        if (interactionStateMachine.CurrentState is CompanionVoiceState.Listening or CompanionVoiceState.Processing) return;
        // Let the closed input box leave the compositor before it can end up in the screenshot.
        await AnswerAsync(userQuestion, TimeSpan.FromMilliseconds(150));
    }

    /// <summary>
    /// One full turn for <paramref name="userQuestion"/>; returns the text put in the bubble (reply or error). Ends Idle with
    /// the transient hide scheduled, unless <paramref name="cancellationToken"/> fired (a new press owns the state then).
    /// The stages are appended to <see cref="TurnEventLog"/> as turn <paramref name="turnId"/> (a voice turn, started at the key
    /// release), or as a new turn starting now; the turn's timing line is logged at the end.
    /// <paramref name="screenCapturesTask"/> is a capture already started (at the key press); without one the turn captures now.
    /// </summary>
    internal async Task<string> AnswerAsync(string userQuestion, TimeSpan delayBeforeCapture = default,
        CancellationToken callerCancellationToken = default, int? turnId = null,
        Task<IReadOnlyList<CompanionScreenCapture>>? screenCapturesTask = null)
    {
        int thisTurnId = turnId ?? TurnEventLog.StartTurn("question");
        Action logFirstSound = () => TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.SpeechStarted);
        if (textToSpeechSpeaker is not null) textToSpeechSpeaker.SoundStarted += logFirstSound;
        isRunning = true;
        string bubbleText;
        currentAnswerCancellation?.Cancel(); // Only one turn at a time; the newest wins.
        using CancellationTokenSource answerCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        currentAnswerCancellation = answerCancellation;
        CancellationToken cancellationToken = answerCancellation.Token;
        cursorOverlayManager.ShowTransiently();
        cursorOverlayManager.ClearDrawing(); // A new turn draws on a clean screen.
        // A voice turn is already Processing (the trigger is ignored then); a typed one starts here.
        interactionStateMachine.Apply(CompanionInteractionTrigger.TypedQuestionAsked);

        // The reply streams in (backend thread) -> textDeltas -> ReplyPlayback on the UI thread: the bubble grows, and the speaker
        // starts on the first complete line while the backend is still writing the rest.
        Channel<string> textDeltas = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        IReadOnlyList<CompanionScreenCapture> screenCaptures = [];
        ReplyPlayback replyPlayback = new(textToSpeechSpeaker,
            onSpeechStarting: () => interactionStateMachine.Apply(CompanionInteractionTrigger.ReplyReady), // Responding while speaking.
            showText: cursorOverlayManager.ShowResponseText, perform: PerformReplyCall, cancellationToken);

        void PerformReplyCall(ProgramCall call)
        {
            switch (call.Name)
            {
                case "point" when call.LeadingNumbers() is [var x, var y, ..]:
                    PointAtReplyTarget(x, y, call.ScreenNumber, call.Label);
                    break;
                case "click" or "hover" when call.LeadingNumbers() is [var left, var top, var right, var bottom, ..]:
                    PointAtReplyTarget((left + right) / 2, (top + bottom) / 2, call.ScreenNumber, call.Label);
                    break;
                case "clear":
                    cursorOverlayManager.ClearDrawing();
                    break;
                case var name when ScreenMarkBuilder.DrawingCallNames.Contains(name):
                    if (ScreenMarkBuilder.FromCall(call, screenCaptures) is { } mark) cursorOverlayManager.Draw(mark);
                    else Trace.TraceWarning($"Reply mark skipped (missing numbers or screen): {call}");
                    break;
                default:
                    Trace.WriteLine($"Reply call not handled: {call}");
                    break;
            }
        }

        void PointAtReplyTarget(double x, double y, int? screenNumber, string? label)
        {
            if (CompanionScreenQuestion.ResolveScreenshotPoint(new ScreenshotPixelCoordinate(x, y), screenNumber, screenCaptures)
                is not { } pointTarget) return;
            Trace.WriteLine($"Pointing at \"{label}\" ({pointTarget.X}, {pointTarget.Y}) physical px.");
            cursorOverlayManager.PointAt(pointTarget.X, pointTarget.Y, label);
            TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.Pointed, label ?? "");
        }

        async Task PlayTheReplyAsItArrivesAsync()
        {
            await foreach (string textDelta in textDeltas.Reader.ReadAllAsync()) replyPlayback.AddText(textDelta); // Ends with the backend.
        }

        try
        {
            if (delayBeforeCapture > TimeSpan.Zero) await Task.Delay(delayBeforeCapture, cancellationToken);
            screenCaptures = await (screenCapturesTask
                ?? CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync(displayMonitorCatalog.Monitors, cancellationToken))
                .WaitAsync(cancellationToken);
            TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.CaptureDone);

            Task playingTheReply = PlayTheReplyAsItArrivesAsync();
            RoutedChatResponse routedResponse;
            try
            {
                routedResponse = await chatBackendRouter.GetResponseAsync(CompanionJobKind.Answer,
                    CompanionScreenQuestion.BuildChatRequest(userQuestion, screenCaptures, conversationHistory.GetExchanges()),
                    cancellationToken, textDelta =>
                    {
                        TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.TextDelta, textDelta);
                        textDeltas.Writer.TryWrite(textDelta);
                    });
            }
            finally
            {
                textDeltas.Writer.TryComplete();
                await playingTheReply; // Every streamed piece is in the bubble and queued for speech.
            }
            cancellationToken.ThrowIfCancellationRequested();
            TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.ReplyReady, routedResponse.ResponseText);
            Trace.WriteLine($"Question answered by {routedResponse.AnsweringBackendKind}.");
            // The raw reply, marks included: the only way to see what the model actually wrote when a turn misbehaves.
            Trace.WriteLine($"Reply text ({routedResponse.AnsweringBackendKind}): {routedResponse.ResponseText.ReplaceLineEndings(" | ")}");

            replyPlayback.Finish(routedResponse.ResponseText);
            conversationHistory.Record(userQuestion, replyPlayback.HistoryText);
            bubbleText = replyPlayback.VisibleText;
            // Responding (or Idle) before pointing: both draw the triangle, which the spinner would hide during the flight.
            if (!replyPlayback.HasStartedSpeaking) interactionStateMachine.Apply(CompanionInteractionTrigger.InteractionEnded);
            ShowFinishedResponse(bubbleText);
            await replyPlayback.WaitUntilPlayedAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            replyPlayback.Stop();
            bubbleText = ""; // Interrupted by a new press, which now owns the state and the overlay.
            TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.Cancelled);
            Trace.WriteLine("Answer cancelled by a new press.");
        }
        catch (Exception exception)
        {
            replyPlayback.Stop();
            Trace.TraceWarning($"Question failed: {exception}");
            bubbleText = $"Clicky couldn't answer: {exception.Message}";
            TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.Failed, bubbleText);
            interactionStateMachine.Apply(textToSpeechSpeaker is null
                ? CompanionInteractionTrigger.InteractionEnded : CompanionInteractionTrigger.ReplyReady);
            ShowFinishedResponse(bubbleText);
            if (textToSpeechSpeaker is not null) await SpeakErrorLineAsync(textToSpeechSpeaker, cancellationToken);
        }

        if (textToSpeechSpeaker is not null) textToSpeechSpeaker.SoundStarted -= logFirstSound;
        TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.TurnEnded);
        Trace.WriteLine(CompanionTurnTiming.Describe(TurnEventLog.ReadTurn(thisTurnId)));
        if (currentAnswerCancellation == answerCancellation)
        {
            currentAnswerCancellation = null;
            isRunning = false;
        }
        if (cancellationToken.IsCancellationRequested) return bubbleText;
        interactionStateMachine.Apply(CompanionInteractionTrigger.InteractionEnded);
        cursorOverlayManager.ScheduleTransientHide();
        return bubbleText;
    }

    private static async Task SpeakErrorLineAsync(ITextToSpeechSpeaker textToSpeechSpeaker, CancellationToken cancellationToken)
    {
        try
        {
            await textToSpeechSpeaker.SpeakAsync(SpokenErrorLine, cancellationToken);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException) Trace.TraceWarning($"Couldn't speak the error line: {exception.Message}");
        }
    }

    private void ShowFinishedResponse(string responseText)
    {
        cursorOverlayManager.ShowResponseText(responseText);
        cursorOverlayManager.FinishResponse();
    }

    /// <summary>The three backends, sharing the live settings objects so config edits made in the app apply on the next question.</summary>
    public static ChatBackendRouter CreateRouter(ClickyConfigurationStore clickyConfigurationStore)
    {
        ClickyConfiguration configuration = clickyConfigurationStore.CurrentConfiguration;
        return new ChatBackendRouter(() => clickyConfigurationStore.CurrentConfiguration.JobRouting,
        [
            new ClaudeCodeCliChatBackend(configuration.ClaudeCodeCli),
            new CodexCliChatBackend(configuration.CodexCli),
            new CloudflareWorkerChatBackend(configuration.CloudflareWorker)
        ]);
    }
}

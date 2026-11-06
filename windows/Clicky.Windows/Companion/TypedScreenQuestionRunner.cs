using System.Diagnostics;
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
    CompanionInteractionStateMachine? interactionStateMachine = null, ITextToSpeechSpeaker? textToSpeechSpeaker = null)
{
    /// <summary>Spoken when a turn fails; the bubble carries the details.</summary>
    internal const string SpokenErrorLine = "Sorry, I couldn't answer that. The details are on screen.";

    private readonly CompanionInteractionStateMachine interactionStateMachine =
        interactionStateMachine ?? new CompanionInteractionStateMachine(cursorOverlayManager.SetVoiceState);
    private bool isRunning;

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
    /// </summary>
    internal async Task<string> AnswerAsync(string userQuestion, TimeSpan delayBeforeCapture = default,
        CancellationToken cancellationToken = default)
    {
        isRunning = true;
        string bubbleText;
        cursorOverlayManager.ShowTransiently();
        // A voice turn is already Processing (the trigger is ignored then); a typed one starts here.
        interactionStateMachine.Apply(CompanionInteractionTrigger.TypedQuestionAsked);
        try
        {
            if (delayBeforeCapture > TimeSpan.Zero) await Task.Delay(delayBeforeCapture, cancellationToken);
            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<CompanionScreenCapture> screenCaptures =
                await CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync(displayMonitorCatalog.Monitors, cancellationToken);
            TimeSpan captureDuration = stopwatch.Elapsed;

            RoutedChatResponse routedResponse = await chatBackendRouter.GetResponseAsync(CompanionJobKind.Answer,
                CompanionScreenQuestion.BuildChatRequest(userQuestion, screenCaptures, conversationHistory.GetExchanges()),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Trace.WriteLine($"Question answered by {routedResponse.AnsweringBackendKind}: capture " +
                $"{captureDuration.TotalMilliseconds:F0} ms, answer {(stopwatch.Elapsed - captureDuration).TotalMilliseconds:F0} ms.");

            conversationHistory.Record(userQuestion, routedResponse.ResponseText);
            PointingParseResult pointingParseResult = PointTagParser.ParsePointingCoordinates(routedResponse.ResponseText);
            bubbleText = pointingParseResult.SpokenText;
            bool willSpeak = textToSpeechSpeaker is not null && bubbleText.Trim().Length > 0;
            // Responding (or Idle) before pointing: both draw the triangle, which the spinner would hide during the flight.
            interactionStateMachine.Apply(willSpeak ? CompanionInteractionTrigger.ReplyReady : CompanionInteractionTrigger.InteractionEnded);
            ShowFinishedResponse(bubbleText);
            if (CompanionScreenQuestion.ResolvePointTarget(pointingParseResult, screenCaptures) is { } pointTarget)
                cursorOverlayManager.PointAt(pointTarget.X, pointTarget.Y, pointingParseResult.ElementLabel);
            if (willSpeak) await textToSpeechSpeaker!.SpeakAsync(bubbleText, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            isRunning = false;
            return ""; // Interrupted by a new press, which now owns the state and the overlay.
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Question failed: {exception}");
            bubbleText = $"Clicky couldn't answer: {exception.Message}";
            interactionStateMachine.Apply(textToSpeechSpeaker is null
                ? CompanionInteractionTrigger.InteractionEnded : CompanionInteractionTrigger.ReplyReady);
            ShowFinishedResponse(bubbleText);
            if (textToSpeechSpeaker is not null) await SpeakErrorLineAsync(textToSpeechSpeaker, cancellationToken);
        }

        isRunning = false;
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

using System.Diagnostics;
using System.Windows;
using Clicky.Windows.AI;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Companion;

/// <summary>
/// Tray "Ask about my screen (typed)": the full answer path (capture -> route -> bubble -> point) without the voice pipeline,
/// so each backend can be checked on its own. Runs on the UI thread, where the overlay manager and history live.
/// </summary>
internal sealed class TypedScreenQuestionRunner(
    CursorOverlayManager cursorOverlayManager, DisplayMonitorCatalog displayMonitorCatalog,
    ChatBackendRouter chatBackendRouter, CompanionConversationHistory conversationHistory,
    CompanionInteractionStateMachine? interactionStateMachine = null)
{
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
        await AnswerAsync(userQuestion);
    }

    /// <summary>One full turn for <paramref name="userQuestion"/>; returns the text put in the bubble (reply or error).</summary>
    internal async Task<string> AnswerAsync(string userQuestion)
    {
        isRunning = true;
        string bubbleText;
        cursorOverlayManager.ShowTransiently();
        interactionStateMachine.Apply(CompanionInteractionTrigger.TypedQuestionAsked);
        try
        {
            // Let the closed input box leave the compositor before it can end up in the screenshot.
            await Task.Delay(150);
            Stopwatch stopwatch = Stopwatch.StartNew();
            IReadOnlyList<CompanionScreenCapture> screenCaptures =
                await CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync(displayMonitorCatalog.Monitors, CancellationToken.None);
            TimeSpan captureDuration = stopwatch.Elapsed;

            RoutedChatResponse routedResponse = await chatBackendRouter.GetResponseAsync(CompanionJobKind.Answer,
                CompanionScreenQuestion.BuildChatRequest(userQuestion, screenCaptures, conversationHistory.GetExchanges()),
                CancellationToken.None);
            Trace.WriteLine($"Typed question answered by {routedResponse.AnsweringBackendKind}: capture " +
                $"{captureDuration.TotalMilliseconds:F0} ms, answer {(stopwatch.Elapsed - captureDuration).TotalMilliseconds:F0} ms.");

            conversationHistory.Record(userQuestion, routedResponse.ResponseText);
            PointingParseResult pointingParseResult = PointTagParser.ParsePointingCoordinates(routedResponse.ResponseText);
            // Idle before pointing, like the Mac: the spinner would hide the triangle's flight.
            interactionStateMachine.Apply(CompanionInteractionTrigger.InteractionEnded);
            bubbleText = pointingParseResult.SpokenText;
            ShowFinishedResponse(bubbleText);
            if (CompanionScreenQuestion.ResolvePointTarget(pointingParseResult, screenCaptures) is { } pointTarget)
                cursorOverlayManager.PointAt(pointTarget.X, pointTarget.Y, pointingParseResult.ElementLabel);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Typed question failed: {exception}");
            interactionStateMachine.Apply(CompanionInteractionTrigger.InteractionEnded);
            bubbleText = $"Clicky couldn't answer: {exception.Message}";
            ShowFinishedResponse(bubbleText);
        }
        finally
        {
            isRunning = false;
            cursorOverlayManager.ScheduleTransientHide();
        }
        return bubbleText;
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

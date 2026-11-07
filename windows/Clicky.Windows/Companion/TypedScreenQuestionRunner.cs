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

    /// <summary>The app in front at the press, for the voice turn's context (by the release, Clicky's own UI may be in front).</summary>
    private ForegroundAppInfo? frontAppFromPress;

    /// <summary>What the user drew with the mouse while asking (E11 T11.23), for the voice turn that follows the release.</summary>
    private HighlightStroke? highlightFromPress;

    /// <summary>The release's highlighter stroke (or null); the next voice turn paints it into its screenshot and describes it.</summary>
    public void SetHighlightForNextVoiceTurn(HighlightStroke? stroke) => highlightFromPress = stroke;

    /// <summary>The browser page read at the press (E11 T11.26), for the voice turn that follows.</summary>
    private Task<string?>? pageFromPress;

    /// <summary>
    /// The history version of the last turn that read untrusted text (a page, web results). Text from it stays in the history (and
    /// the session) for up to 10 more exchanges, so side effects stay gated that long, not just in that turn.
    /// </summary>
    private long? untrustedTextAtVersion;

    /// <summary>Whether to keep each turn on disk for the bench (config saveTurns, E11 T11.28).</summary>
    public Func<bool> ShouldSaveTurns { get; set; } = () => false;

    /// <summary>Whether to read the page when a browser is in front (config readBrowserPage).</summary>
    public Func<bool> ShouldReadBrowserPage { get; set; } = () => true;

    /// <summary>Whether the last question was spoken; a walkthrough's follow-ups carry it on.</summary>
    private bool lastQuestionWasVoice = true;

    /// <summary>More app lines for each turn's context (memory, the browser page, a highlight, jobs); added by later features.</summary>
    public List<Func<IEnumerable<string>>> TurnContextSources { get; } = [];

    /// <summary>
    /// Push-to-talk press: capture now, while the user talks, so the turn doesn't wait for it after the release. The screen at
    /// the press is what the question is about (HeyClicky captures at release). A tap or an empty transcript leaves it unused;
    /// the next press replaces it.
    /// </summary>
    public void CaptureOnPress()
    {
        frontAppFromPress = ForegroundAppInfo.Read();
        pageFromPress = ShouldReadBrowserPage() ? Automation.BrowserPageReader.ReadContextLineAsync(frontAppFromPress) : null;
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

    /// <summary>Clicky is quitting: no CLI process may outlive it.</summary>
    public void Shutdown() => chatBackendRouter.Shutdown();

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
        Walkthrough?.StopAll(); // A new question ends any guided step in progress.
        StopCurrentTurn();
    }

    /// <summary>Cancels the turn in flight and its speech; the walkthrough does this itself before a step's follow-up.</summary>
    internal void StopCurrentTurn()
    {
        currentAnswerCancellation?.Cancel();
        textToSpeechSpeaker?.StopSpeaking();
        cursorOverlayManager.HideResponse();
    }

    /// <summary>What the model remembers about the user (E11 T11.24); its [memory] line goes into every turn's context.</summary>
    public CompanionMemory? Memory
    {
        get => memory;
        set
        {
            memory = value;
            if (value is not null) TurnContextSources.Add(() => value.ContextLine() is { } line ? [line] : []);
        }
    }

    private CompanionMemory? memory;

    /// <summary>Background jobs (E11 T11.27); their [jobs] line goes into every turn's context.</summary>
    public Agents.AgentJobManager? AgentJobs
    {
        get => agentJobs;
        set
        {
            agentJobs = value;
            if (value is not null) TurnContextSources.Add(() => value.ContextLine() is { } line ? [line] : []);
        }
    }

    private Agents.AgentJobManager? agentJobs;

    /// <summary>The guided walkthrough (E11 T11.12-T11.17), attached after construction since it starts this runner's follow-ups.</summary>
    public GuidedWalkthrough? Walkthrough { get; set; }

    /// <summary>The walkthrough step the last reply was on (its step(n)); a follow-up continues from it.</summary>
    private int walkthroughStep;

    /// <summary>The next step's turn, after the user did what the last one asked (clicked its target, or acted on screen).</summary>
    public Task<string> AnswerFollowUpAsync(TurnTrigger trigger) => AnswerAsync("", trigger: trigger);

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
        Task<IReadOnlyList<CompanionScreenCapture>>? screenCapturesTask = null, TurnTrigger? trigger = null)
    {
        trigger ??= TurnTrigger.Question;
        int thisTurnId = turnId ?? TurnEventLog.StartTurn(trigger.IsWalkthrough ? trigger.Kind.ToString() : "question");
        if (!trigger.IsWalkthrough) walkthroughStep = 0;
        IReadOnlyList<string> appNotes = Walkthrough?.TakeNotes() ?? [];
        bool isVoice = trigger.IsWalkthrough ? lastQuestionWasVoice : screenCapturesTask is not null || turnId is not null;
        if (!trigger.IsWalkthrough) lastQuestionWasVoice = isVoice;
        ForegroundAppInfo? frontApp = screenCapturesTask is not null ? frontAppFromPress : ForegroundAppInfo.Read();
        HighlightStroke? highlight = screenCapturesTask is not null ? highlightFromPress : null;
        highlightFromPress = null;
        Task<string?>? pageRead = screenCapturesTask is not null ? pageFromPress
            : ShouldReadBrowserPage() ? Automation.BrowserPageReader.ReadContextLineAsync(frontApp) : null;
        pageFromPress = null;
        string? highlightLine = null;
        string userPrompt = CompanionScreenQuestion.BuildUserPrompt(userQuestion, trigger);
        string historyUserText = userQuestion.Length > 0 ? userQuestion : trigger.AppFact ?? "";
        bool armedATargetThisTurn = false;
        int armedTargetLine = -1;
        // Untrusted text reached the model this turn (a page's text, web results): side effects need the user's own click.
        bool turnHasUntrustedText = untrustedTextAtVersion is { } taintedVersion
            && conversationHistory.Version - taintedVersion < CompanionConversationHistory.MaximumExchangeCount;
        bool turnReadUntrustedText = false; // This turn itself read a page or the web: the gate restarts from this exchange.
        (CompanionChatRequest Request, RoutedChatResponse Response)? turnToSave = null;
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
        ReplyPlayback replyPlayback = null!; // Assigned next; PerformReplyCall reads it only once the reply plays.
        replyPlayback = new(textToSpeechSpeaker,
            onSpeechStarting: () => interactionStateMachine.Apply(CompanionInteractionTrigger.ReplyReady), // Responding while speaking.
            showText: cursorOverlayManager.ShowResponseText, perform: PerformReplyCall, cancellationToken);
        // A target counts only in the reply's last line: when more lines follow, it was just shown, not a step to wait on.
        replyPlayback.LineAdded += lineCount =>
        {
            if (armedATargetThisTurn && armedTargetLine < lineCount - 1 && Walkthrough?.HasTarget == true)
                Walkthrough.DisarmTarget("more lines follow it");
        };

        void PerformReplyCall(ProgramCall call, int lineIndex)
        {
            switch (call.Name)
            {
                case "point" when call.LeadingNumbers() is [var x, var y, ..]:
                    PointAtReplyTarget(x, y, call.ScreenNumber, call.Label);
                    break;
                case "click" or "hover" when call.LeadingNumbers() is [var left, var top, var right, var bottom, ..]:
                    PointAtReplyTarget((left + right) / 2, (top + bottom) / 2, call.ScreenNumber, call.Label);
                    if (lineIndex >= replyPlayback.LineCount - 1)
                    {
                        armedATargetThisTurn = true;
                        armedTargetLine = lineIndex;
                        _ = ArmTargetAsync(call, left, top, right, bottom, replyPlayback.Step ?? Math.Max(1, trigger.WalkthroughStep ?? 1),
                            screenCaptures, cancellationToken);
                    }
                    break;
                case "clear":
                    cursorOverlayManager.ClearDrawing();
                    break;
                case "remember" or "forget" or "agent" or "agent_send" when turnHasUntrustedText:
                    Trace.WriteLine($"{call.Name}() refused: page or web text was read recently, and it could have asked for it.");
                    break;
                case "remember" or "forget" or "agent" or "agent_send" when trigger.IsWalkthrough:
                    Trace.WriteLine($"{call.Name}() refused: it only follows something the user said, not a walkthrough step.");
                    break;
                case "remember" when call.Text(0) is { Length: > 0 } fact:
                    Memory?.Remember(fact);
                    break;
                case "forget" when call.Text(0) is { Length: > 0 } words:
                    Memory?.Forget(words);
                    break;
                case "copy" when call.Text(0) is { } textToCopy:
                    if (turnHasUntrustedText) ResultCardWindow.Show("Copy this?", textToCopy); // The user's click copies it.
                    else CompanionActions.Copy(textToCopy);
                    break;
                case "open" when call.Text(0) is { } link:
                    // Settings pages open at once; a web link waits for the user's click: text on the screen, a page or the web
                    // could have asked for it, and a link can carry data out.
                    if (link.TrimStart().StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) CompanionActions.Open(link);
                    else if (CompanionActions.IsAllowedLink(link, out _)) ResultCardWindow.Show("Open this link?", link, "Open", () => CompanionActions.Open(link));
                    break;
                case "agent" when call.Arguments.OfType<string>().ToArray() is [var jobTitle, var jobTask, ..]:
                    // A job writes files and runs for minutes: it starts only from the user's own click on its card.
                    if (AgentJobs is null) Trace.WriteLine("agent() skipped: background jobs aren't set up.");
                    else ResultCardWindow.Show($"Start a background job: {jobTitle}?", jobTask, "Start", () => AgentJobs.Start(jobTitle, jobTask));
                    break;
                case "agent_send" when call.Arguments.OfType<string>().ToArray() is [var sendJobId, var jobMessage, ..]:
                    if (AgentJobs?.Send(sendJobId, jobMessage) != true) Trace.WriteLine($"agent_send() found no job {sendJobId}.");
                    break;
                case "agent_stop" when call.Text(0) is { } stopJobId:
                    if (AgentJobs?.Stop(stopJobId) != true) Trace.WriteLine($"agent_stop() found no running job {stopJobId}.");
                    break;
                case "result":
                    string[] resultTexts = call.Arguments.OfType<string>().ToArray();
                    if (resultTexts.Length > 0)
                        ResultCardWindow.Show(resultTexts.Length >= 2 ? resultTexts[0] : "", resultTexts.Length >= 2 ? resultTexts[1] : resultTexts[0]);
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
            if (highlight is not null) (screenCaptures, highlightLine) = await ApplyHighlightAsync(screenCaptures, highlight);

            Task playingTheReply = PlayTheReplyAsItArrivesAsync();
            RoutedChatResponse routedResponse;
            try
            {
                string? pageLine = pageRead is null ? null : await pageRead;
                if (pageLine is not null) turnHasUntrustedText = turnReadUntrustedText = true;
                string turnContext = CompanionTurnContext.Build(DateTime.Now, frontApp, isVoice, trigger.WalkthroughStep,
                    [.. appNotes, highlightLine ?? "", pageLine ?? "", conversationHistory.EarlierSummary ?? "", .. TurnContextSources.SelectMany(source => source())]);
                CompanionChatRequest request = CompanionScreenQuestion.BuildChatRequest(userPrompt, screenCaptures, conversationHistory.GetExchanges())
                    with { TurnContext = turnContext, HistoryVersion = conversationHistory.Version, OnWebContent = () => turnHasUntrustedText = turnReadUntrustedText = true };
                routedResponse = await chatBackendRouter.GetResponseAsync(CompanionJobKind.Answer, request,
                    cancellationToken, textDelta =>
                    {
                        TurnEventLog.Append(thisTurnId, CompanionTurnEventKind.TextDelta, textDelta);
                        textDeltas.Writer.TryWrite(textDelta);
                    });
                turnToSave = (request, routedResponse);
                if (turnReadUntrustedText) untrustedTextAtVersion = conversationHistory.Version;
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
            conversationHistory.Record(historyUserText, replyPlayback.HistoryText);
            bubbleText = replyPlayback.VisibleText;
            // Responding (or Idle) before pointing: both draw the triangle, which the spinner would hide during the flight.
            if (!replyPlayback.HasStartedSpeaking) interactionStateMachine.Apply(CompanionInteractionTrigger.InteractionEnded);
            ShowFinishedResponse(bubbleText);
            walkthroughStep = replyPlayback.Step ?? trigger.WalkthroughStep ?? walkthroughStep;
            await replyPlayback.WaitUntilPlayedAsync();
            WatchForTheNextStep(replyPlayback, armedATargetThisTurn, screenCaptures, cancellationToken);
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
        string timingLine = CompanionTurnTiming.Describe(TurnEventLog.ReadTurn(thisTurnId));
        Trace.WriteLine(timingLine);
        if (turnToSave is { } saved && ShouldSaveTurns())
            TurnRecorder.Save(thisTurnId, saved.Request, saved.Response.ResponseText, saved.Response.AnsweringBackendKind.ToString(), timingLine);
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

    /// <summary>The highlighter stroke painted into its screen's capture, and the [highlight] line describing it.</summary>
    private static async Task<(IReadOnlyList<CompanionScreenCapture> Captures, string? Line)> ApplyHighlightAsync(
        IReadOnlyList<CompanionScreenCapture> screenCaptures, HighlightStroke stroke)
    {
        int captureIndex = -1;
        for (int index = 0; index < screenCaptures.Count; index++)
            if (screenCaptures[index].CapturedMonitor.Contains(stroke.Points[0].X, stroke.Points[0].Y)) captureIndex = index;
        if (captureIndex < 0) return (screenCaptures, null);
        CompanionScreenCapture capture = screenCaptures[captureIndex];
        CompanionScreenCapture[] painted = [.. screenCaptures];
        try
        {
            painted[captureIndex] = HighlightGesture.PaintInto(capture, stroke);
        }
        catch (Exception exception)
        {
            // Only the painting is lost: the turn still gets the screenshot and the words describing the stroke.
            Trace.TraceWarning($"Couldn't paint the highlight into the screenshot: {exception.Message}");
        }
        string described = HighlightGesture.Describe(stroke, capture.CapturedMonitor.Bounds);
        string? text = await HighlightGesture.ReadTextUnderAsync(stroke);
        string screen = screenCaptures.Count > 1 ? $" on screen {captureIndex + 1}" : "";
        string line = $"[highlight] while asking, they {described}{screen} (it's drawn in the screenshot)" + (text is null ? "" : $"; text there: \"{text}\"");
        Trace.WriteLine($"Highlight: they {described}{screen}{(text is null ? "" : " (text under it read)")}."); // Not the text itself: the log is a file.
        return (painted, line);
    }

    /// <summary>
    /// A click/hover target in the reply's last line: snap its box to the real control, then arm it, and prestart the next
    /// step's Claude process while the user reaches for the mouse (Claude's prestart matches on the prompt alone).
    /// </summary>
    private async Task ArmTargetAsync(ProgramCall call, double left, double top, double right, double bottom, int step,
        IReadOnlyList<CompanionScreenCapture> screenCaptures, CancellationToken cancellationToken)
    {
        if (Walkthrough is null || CompanionScreenQuestion.CaptureForScreen(call.ScreenNumber, screenCaptures) is not { } capture) return;
        Point? topLeft = CompanionScreenQuestion.ResolveScreenshotPoint(
            new ScreenshotPixelCoordinate(Math.Min(left, right), Math.Min(top, bottom)), call.ScreenNumber, screenCaptures);
        Point? bottomRight = CompanionScreenQuestion.ResolveScreenshotPoint(
            new ScreenshotPixelCoordinate(Math.Max(left, right), Math.Max(top, bottom)), call.ScreenNumber, screenCaptures);
        if (topLeft is null || bottomRight is null) return;
        Rect box = new(topLeft.Value, bottomRight.Value);
        PixelRect monitorBounds = capture.CapturedMonitor.Bounds;
        Rect? snapped = await Automation.UiaControlSnapper.SnapAsync(box,
            new Rect(monitorBounds.Left, monitorBounds.Top, monitorBounds.Width, monitorBounds.Height));
        if (cancellationToken.IsCancellationRequested) return;
        Walkthrough.ArmTarget(new GuidedTarget(snapped ?? box, call.Label ?? "the target", call.Name == "hover", step, monitorBounds,
            capture.CapturedMonitor.DpiScaleX));
        PrestartTheNextStep();
    }

    /// <summary>
    /// After a reply has played: a step without a target whose last line tells the user to do something arms the step
    /// watcher; any marks left on screen are watched so they go once the screen changes under them.
    /// </summary>
    private void WatchForTheNextStep(ReplyPlayback replyPlayback, bool armedATarget, IReadOnlyList<CompanionScreenCapture> screenCaptures,
        CancellationToken cancellationToken)
    {
        if (Walkthrough is null || cancellationToken.IsCancellationRequested) return;
        if (screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen) is not { } cursorCapture) return;
        PixelRect monitorBounds = cursorCapture.CapturedMonitor.Bounds;
        if (!armedATarget && replyPlayback.HasMarks && GuidedWalkthrough.IsInstruction(replyPlayback.LastLineText))
        {
            Walkthrough.ArmStepWatch(monitorBounds, Math.Max(1, walkthroughStep));
            PrestartTheNextStep();
        }
        if (cursorOverlayManager.HasDrawing) Walkthrough.WatchDrawing(monitorBounds);
    }

    /// <summary>
    /// Starts the next step's CLI while the user reaches for the mouse. The step's screenshots don't exist yet: Claude's prestart
    /// matches on the prompt alone, and Codex's reserves one image file per monitor and gets the pictures at send time.
    /// </summary>
    private void PrestartTheNextStep() =>
        chatBackendRouter.Prestart(CompanionJobKind.Answer, CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt,
            displayMonitorCatalog.Monitors.Select(_ => new LabeledScreenshotImage([], "")).ToArray());

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

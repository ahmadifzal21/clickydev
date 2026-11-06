using System.Threading.Channels;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Companion;

/// <summary>
/// One turn's reply on its way out while it is still being written: the text grows in the bubble, each complete line goes to
/// the speaker, and each line's [POINT] tag moves the cursor when that line starts playing (step-list walkthroughs). A tag that
/// ends a later sentence of a line belongs to the whole line, so it points as soon as that line has started (a classic
/// one-tag reply points when the reply is complete, as before). Runs on the UI thread.
/// </summary>
internal sealed class ReplyPlayback(
    ITextToSpeechSpeaker? textToSpeechSpeaker, Action onSpeechStarting, Action<string> showText,
    Action<PointingParseResult> pointAt, CancellationToken cancellationToken)
{
    private readonly ReplyLineSplitter replyLineSplitter = new();
    private readonly Channel<SpokenLine> spokenLines = Channel.CreateUnbounded<SpokenLine>(new UnboundedChannelOptions { SingleReader = true });
    private LineProgress? currentLineProgress;
    private PointingParseResult? lastPointing;

    private sealed class LineProgress
    {
        public bool HasStarted { get; set; }
        public PointingParseResult? PendingPointing { get; set; }
    }

    public bool HasReceivedText { get; private set; }
    public bool HasStartedSpeaking { get; private set; }

    /// <summary>Finishes when everything queued has been said (or the speech was stopped).</summary>
    public Task Speech { get; private set; } = Task.CompletedTask;

    /// <summary>The reply as the bubble shows it, without tags.</summary>
    public string VisibleText => replyLineSplitter.VisibleText;

    public void AddText(string textDelta)
    {
        HasReceivedText = true;
        foreach (ReplyLine replyLine in replyLineSplitter.Add(textDelta)) Queue(replyLine);
        if (!cancellationToken.IsCancellationRequested) showText(replyLineSplitter.VisibleText);
    }

    /// <summary>The reply is complete; <paramref name="fullReplyText"/> is used when nothing was streamed.</summary>
    public void Finish(string fullReplyText)
    {
        if (!HasReceivedText) foreach (ReplyLine replyLine in replyLineSplitter.Add(fullReplyText)) Queue(replyLine);
        foreach (ReplyLine replyLine in replyLineSplitter.Finish()) Queue(replyLine);
        spokenLines.Writer.TryComplete();
    }

    /// <summary>Without speech nothing marks a line's start, so the cursor goes to the reply's last target (the old behaviour).</summary>
    public void PointAtLastTargetIfSilent()
    {
        if (!HasStartedSpeaking && lastPointing is { } pointing && !cancellationToken.IsCancellationRequested) pointAt(pointing);
    }

    /// <summary>The turn failed or was cancelled: nothing more will be queued.</summary>
    public void Stop() => spokenLines.Writer.TryComplete();

    private void Queue(ReplyLine replyLine)
    {
        PointingParseResult pointing = PointTagParser.ParsePointingCoordinates(replyLine.Text);
        bool hasTarget = pointing.ScreenshotCoordinate is not null;
        if (hasTarget) lastPointing = pointing;
        if (textToSpeechSpeaker is null || cancellationToken.IsCancellationRequested) return;

        if (replyLine.StartsLine || currentLineProgress is null) currentLineProgress = new LineProgress();
        LineProgress lineProgress = currentLineProgress;
        string spokenText = pointing.SpokenText.Trim();
        if (!replyLine.StartsLine)
        {
            if (hasTarget)
            {
                if (lineProgress.HasStarted) PointAtIfStillRunning(pointing);
                else lineProgress.PendingPointing = pointing;
            }
            if (spokenText.Length > 0) Speak(new SpokenLine(spokenText));
            return;
        }

        if (spokenText.Length == 0 && !hasTarget) return;
        Speak(new SpokenLine(spokenText, () =>
        {
            lineProgress.HasStarted = true;
            if (hasTarget) PointAtIfStillRunning(pointing);
            if (lineProgress.PendingPointing is { } pendingPointing)
            {
                lineProgress.PendingPointing = null;
                PointAtIfStillRunning(pendingPointing);
            }
        }));
    }

    private void Speak(SpokenLine spokenLine)
    {
        if (!HasStartedSpeaking)
        {
            HasStartedSpeaking = true;
            onSpeechStarting();
            Speech = textToSpeechSpeaker!.SpeakLinesAsync(spokenLines.Reader, cancellationToken);
        }
        spokenLines.Writer.TryWrite(spokenLine);
    }

    private void PointAtIfStillRunning(PointingParseResult pointing)
    {
        if (!cancellationToken.IsCancellationRequested) pointAt(pointing);
    }
}

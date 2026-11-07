using System.Text;
using System.Threading.Channels;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Companion;

/// <summary>
/// The pacer (E11 T11.5): plays one turn's reply program while it is still being written. Each <c>say</c> line goes to the
/// speaker as soon as it is complete, and owns the marks and actions written after it (those written before the first say
/// belong to the first). They wait for their line to start playing and are then spread across the line's length, so the
/// drawing happens while that line is said. Without a speaker each line is given a reading time instead. <c>step</c> and
/// <c>seen</c> are recorded for the turn. Runs on the UI thread.
/// </summary>
internal sealed class ReplyPlayback(
    ITextToSpeechSpeaker? textToSpeechSpeaker, Action onSpeechStarting, Action<string> showText,
    Action<ProgramCall, int> perform, CancellationToken cancellationToken)
{
    /// <summary>Speaking rate used to guess a line's length; Kokoro's default voice is close to this.</summary>
    internal const double EstimatedWordsPerSecond = 2.6;

    private readonly ReplyProgramReader replyProgramReader = new();
    private readonly Channel<SpokenLine> spokenLines = Channel.CreateUnbounded<SpokenLine>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<LineGroup> silentLines = Channel.CreateUnbounded<LineGroup>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<ProgramCall> callsBeforeTheFirstLine = [];
    private readonly List<Task> markReleases = [];
    private readonly List<string> seenNotes = [];
    private readonly List<string> markedLabels = [];
    private LineGroup? currentLine;
    private LineGroup? currentLineStart;
    private Task silentPlayback = Task.CompletedTask;

    /// <summary>One spoken line and the calls it owns.</summary>
    private sealed class LineGroup(string text, int index)
    {
        public string Text { get; } = text;
        public int Index { get; } = index;
        public double WordCount { get; set; } = CountWords(text);
        public List<ProgramCall> PendingCalls { get; } = [];
        public bool HasStarted { get; set; }
    }

    public bool HasReceivedText { get; private set; }

    /// <summary>How many spoken lines the reply has so far (sentences continuing a prose line don't count).</summary>
    public int LineCount { get; private set; }

    /// <summary>The last spoken line so far, whole (continuing sentences included).</summary>
    public string LastLineText { get; private set; } = "";

    /// <summary>True once the reply has pointed, drawn or targeted anything.</summary>
    public bool HasMarks { get; private set; }

    /// <summary>A new line arrived; the argument is the new <see cref="LineCount"/>. Calls are passed their line's index.</summary>
    public event Action<int>? LineAdded;
    public bool HasStartedSpeaking { get; private set; }

    /// <summary>Finishes when everything queued has been said (or the speech was stopped).</summary>
    public Task Speech { get; private set; } = Task.CompletedTask;

    /// <summary>Speech, silent pacing and every mark released: the reply has fully played. Call after <see cref="Finish"/>.</summary>
    public async Task WaitUntilPlayedAsync()
    {
        await Speech;
        await silentPlayback;
        await Task.WhenAll(markReleases.ToArray()); // Every line has started by now, so every spread is in the list.
    }

    /// <summary>The reply as the bubble shows it: the spoken lines only.</summary>
    public string VisibleText => replyProgramReader.VisibleText;

    /// <summary>The last <c>step(n)</c> of the reply, or null.</summary>
    public int? Step { get; private set; }

    /// <summary>The reply for history: spoken lines, what was marked, and the model's own seen notes; never coordinates.</summary>
    public string HistoryText
    {
        get
        {
            StringBuilder history = new(VisibleText);
            if (markedLabels.Count > 0) history.Append($"\n[showed: {string.Join(", ", markedLabels.Distinct())}]");
            foreach (string seenNote in seenNotes) history.Append($"\n[seen: {seenNote}]");
            return history.ToString();
        }
    }

    public void AddText(string textDelta)
    {
        HasReceivedText = true;
        foreach (ProgramCall call in replyProgramReader.Add(textDelta)) Handle(call);
        if (!cancellationToken.IsCancellationRequested) showText(replyProgramReader.VisibleText);
    }

    /// <summary>The reply is complete; <paramref name="fullReplyText"/> is used when nothing was streamed.</summary>
    public void Finish(string fullReplyText)
    {
        if (!HasReceivedText) foreach (ProgramCall call in replyProgramReader.Add(fullReplyText)) Handle(call);
        foreach (ProgramCall call in replyProgramReader.Finish()) Handle(call);
        // A program with marks but nothing to say: there is no line to wait for.
        if (currentLine is null) foreach (ProgramCall call in callsBeforeTheFirstLine) PerformIfStillRunning(call, 0);
        callsBeforeTheFirstLine.Clear();
        spokenLines.Writer.TryComplete();
        silentLines.Writer.TryComplete();
    }

    /// <summary>The turn failed or was cancelled: nothing more will be queued.</summary>
    public void Stop()
    {
        spokenLines.Writer.TryComplete();
        silentLines.Writer.TryComplete();
    }

    private void Handle(ProgramCall call)
    {
        switch (call.Name)
        {
            case "say":
                StartLine(call);
                return;
            case "step":
                if (call.Number(0) is { } stepNumber) Step = (int)stepNumber;
                return;
            case "seen":
                if (call.Text(0) is { Length: > 0 } seenNote) seenNotes.Add(seenNote);
                return;
        }
        if (call.Label is { Length: > 0 } label && call.Name is "point" or "click" or "hover" or "ring" or "box" or "underline" or "highlight")
            markedLabels.Add(label);
        if (call.Name is "point" or "click" or "hover" || ScreenMarkBuilder.DrawingCallNames.Contains(call.Name)) HasMarks = true;

        // A sentence that continues a prose line hands its marks to the line's first sentence.
        LineGroup? owner = currentLine is not null && currentLine != currentLineStart ? currentLineStart : currentLine;
        if (owner is null)
        {
            callsBeforeTheFirstLine.Add(call);
            return;
        }
        if (owner.HasStarted) PerformIfStillRunning(call, owner.Index); // Its line is already playing: no reason to wait.
        else owner.PendingCalls.Add(call);
    }

    private void StartLine(ProgramCall sayCall)
    {
        string text = sayCall.Text(0) ?? "";
        if (sayCall.ContinuesLine && currentLineStart is not null)
        {
            // Spoken as its own piece (speech starts sooner), but its marks join the line's first sentence.
            currentLineStart.WordCount += CountWords(text);
            LastLineText += " " + text;
            currentLine = new LineGroup(text, currentLineStart.Index);
            QueueLine(currentLine, text, onStarted: null);
            return;
        }
        LineGroup line = new(text, LineCount);
        LineCount++;
        LastLineText = text;
        if (currentLine is null)
        {
            line.PendingCalls.AddRange(callsBeforeTheFirstLine);
            callsBeforeTheFirstLine.Clear();
        }
        currentLine = line;
        currentLineStart = line;
        QueueLine(line, text, () => Release(line));
        LineAdded?.Invoke(LineCount);
    }

    private void QueueLine(LineGroup line, string text, Action? onStarted)
    {
        if (cancellationToken.IsCancellationRequested) return;
        if (textToSpeechSpeaker is null)
        {
            if (onStarted is null) return; // A continuing sentence: its reading time is counted in the line's.
            if (silentPlayback.IsCompleted) silentPlayback = PlaySilentlyAsync();
            silentLines.Writer.TryWrite(line);
            return;
        }
        if (!HasStartedSpeaking)
        {
            HasStartedSpeaking = true;
            onSpeechStarting();
            Speech = textToSpeechSpeaker.SpeakLinesAsync(spokenLines.Reader, cancellationToken);
        }
        spokenLines.Writer.TryWrite(new SpokenLine(text, onStarted));
    }

    /// <summary>The line has started playing: its calls go out now, spread over most of its length.</summary>
    private void Release(LineGroup line)
    {
        line.HasStarted = true;
        List<ProgramCall> calls = [.. line.PendingCalls];
        line.PendingCalls.Clear();
        if (calls.Count == 0) return;
        PerformIfStillRunning(calls[0], line.Index);
        if (calls.Count == 1) return;
        TimeSpan spread = TimeSpan.FromSeconds(0.8 * line.WordCount / EstimatedWordsPerSecond);
        markReleases.Add(PerformSpreadAsync(calls, line.Index, spread));
    }

    private async Task PerformSpreadAsync(List<ProgramCall> calls, int lineIndex, TimeSpan spread)
    {
        try
        {
            for (int index = 1; index < calls.Count; index++)
            {
                await Task.Delay(spread / calls.Count, cancellationToken);
                PerformIfStillRunning(calls[index], lineIndex);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>No voice: each line gets a reading time (0.8 s + 0.28 s a word, 1.6-6 s) before the next one starts.</summary>
    private async Task PlaySilentlyAsync()
    {
        try
        {
            LineGroup? previousLine = null;
            await foreach (LineGroup line in silentLines.Reader.ReadAllAsync(cancellationToken))
            {
                if (previousLine is not null) await Task.Delay(ReadingTime(previousLine.WordCount), cancellationToken);
                previousLine = line;
                Release(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static TimeSpan ReadingTime(double wordCount) => TimeSpan.FromSeconds(Math.Clamp(0.8 + 0.28 * wordCount, 1.6, 6));

    private void PerformIfStillRunning(ProgramCall call, int lineIndex)
    {
        if (!cancellationToken.IsCancellationRequested) perform(call, lineIndex);
    }

    private static double CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

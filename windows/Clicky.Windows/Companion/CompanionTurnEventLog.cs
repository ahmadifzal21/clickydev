using System.Diagnostics;
using System.Threading.Channels;

namespace Clicky.Windows.Companion;

internal enum CompanionTurnEventKind
{
    /// <summary>Text: where the turn's clock starts ("release" or "question").</summary>
    TurnStarted,

    /// <summary>Text: the transcript (voice turns only).</summary>
    TranscriptReady,
    CaptureDone,

    /// <summary>Text: the next piece of the reply as the backend streams it.</summary>
    TextDelta,

    /// <summary>Text: the whole reply, [POINT] tags included.</summary>
    ReplyReady,

    /// <summary>Text: the spoken line whose audio just started.</summary>
    SpeechStarted,

    /// <summary>Text: the label of what the cursor flew to.</summary>
    Pointed,

    /// <summary>Text: the error shown in the bubble.</summary>
    Failed,
    Cancelled,
    TurnEnded
}

/// <param name="Sequence">Rises by one across the whole log, so a reader resumes with "everything after N".</param>
/// <param name="Elapsed">Time since the log was created; turn timings are differences of these.</param>
internal sealed record CompanionTurnEvent(long Sequence, int TurnId, CompanionTurnEventKind Kind, string Text, TimeSpan Elapsed);

/// <summary>
/// One ordered list of what happens in each turn (HeyClicky's runner keeps the same kind of list between its agents and the
/// app). Producers append; readers replay from a sequence number and then follow live, so a reader that starts late (the
/// timing log, the panel) misses nothing. In-process only; the CLIs stay child processes. Thread-safe.
/// </summary>
internal sealed class CompanionTurnEventLog
{
    /// <summary>Old turns are dropped beyond this, so the log doesn't grow with every question.</summary>
    private const int KeptTurnCount = 20;

    private readonly object gate = new();
    private readonly List<CompanionTurnEvent> events = [];
    private readonly List<ChannelWriter<CompanionTurnEvent>> liveReaders = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long lastSequence;
    private int lastTurnId;

    /// <summary>Starts a turn and returns its id; <paramref name="startedAt"/> names what the turn's clock is measured from.</summary>
    public int StartTurn(string startedAt)
    {
        lock (gate)
        {
            int turnId = ++lastTurnId;
            AppendLocked(turnId, CompanionTurnEventKind.TurnStarted, startedAt);
            int oldestKeptTurnId = turnId - KeptTurnCount + 1;
            events.RemoveAll(turnEvent => turnEvent.TurnId < oldestKeptTurnId);
            return turnId;
        }
    }

    public CompanionTurnEvent Append(int turnId, CompanionTurnEventKind kind, string text = "")
    {
        lock (gate) return AppendLocked(turnId, kind, text);
    }

    /// <summary>One turn's kept events, oldest first.</summary>
    public IReadOnlyList<CompanionTurnEvent> ReadTurn(int turnId)
    {
        lock (gate) return events.Where(turnEvent => turnEvent.TurnId == turnId).ToArray();
    }

    /// <summary>The kept events after <paramref name="afterSequence"/> (0 = all), oldest first.</summary>
    public IReadOnlyList<CompanionTurnEvent> ReadAfter(long afterSequence)
    {
        lock (gate) return events.Where(turnEvent => turnEvent.Sequence > afterSequence).ToArray();
    }

    /// <summary>
    /// Replays the kept events after <paramref name="afterSequence"/>, then every new one as it is appended, in order, until
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public ChannelReader<CompanionTurnEvent> Follow(long afterSequence, CancellationToken cancellationToken)
    {
        Channel<CompanionTurnEvent> channel = Channel.CreateUnbounded<CompanionTurnEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (gate)
        {
            foreach (CompanionTurnEvent turnEvent in events.Where(turnEvent => turnEvent.Sequence > afterSequence)) channel.Writer.TryWrite(turnEvent);
            liveReaders.Add(channel.Writer);
        }
        cancellationToken.Register(() =>
        {
            lock (gate) liveReaders.Remove(channel.Writer);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    private CompanionTurnEvent AppendLocked(int turnId, CompanionTurnEventKind kind, string text)
    {
        CompanionTurnEvent turnEvent = new(++lastSequence, turnId, kind, text, clock.Elapsed);
        events.Add(turnEvent);
        foreach (ChannelWriter<CompanionTurnEvent> liveReader in liveReaders) liveReader.TryWrite(turnEvent);
        return turnEvent;
    }
}

/// <summary>A turn's stage times as one log line, measured from its start (the key release or the typed question).</summary>
internal static class CompanionTurnTiming
{
    private static readonly Dictionary<CompanionTurnEventKind, string> StageNames = new()
    {
        [CompanionTurnEventKind.TranscriptReady] = "transcript",
        [CompanionTurnEventKind.CaptureDone] = "capture",
        [CompanionTurnEventKind.TextDelta] = "first text",
        [CompanionTurnEventKind.ReplyReady] = "answer",
        [CompanionTurnEventKind.SpeechStarted] = "first sound",
        [CompanionTurnEventKind.Pointed] = "pointed",
        [CompanionTurnEventKind.Failed] = "failed",
        [CompanionTurnEventKind.Cancelled] = "cancelled",
        [CompanionTurnEventKind.TurnEnded] = "done"
    };

    /// <summary>
    /// E.g. "Turn 3 timing from release: transcript 0.92 s, capture 0.93 s, answer 9.80 s, first sound 11.40 s, done 15.10 s".
    /// Only the first event of each stage counts.
    /// </summary>
    public static string Describe(IReadOnlyList<CompanionTurnEvent> turnEvents)
    {
        CompanionTurnEvent? turnStarted = turnEvents.FirstOrDefault(turnEvent => turnEvent.Kind == CompanionTurnEventKind.TurnStarted);
        if (turnStarted is null) return "Turn timing: the turn's start is no longer in the log.";
        IEnumerable<string> stages = turnEvents
            .Where(turnEvent => StageNames.ContainsKey(turnEvent.Kind))
            .GroupBy(turnEvent => turnEvent.Kind).Select(stageEvents => stageEvents.First()).OrderBy(turnEvent => turnEvent.Sequence)
            .Select(turnEvent => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{StageNames[turnEvent.Kind]} {(turnEvent.Elapsed - turnStarted.Elapsed).TotalSeconds:0.00} s"));
        return $"Turn {turnStarted.TurnId} timing from {turnStarted.Text}: {string.Join(", ", stages)}";
    }
}

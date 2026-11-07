using Clicky.Windows.AI;

namespace Clicky.Windows.Companion;

/// <summary>
/// The last <see cref="MaximumExchangeCount"/> question/answer pairs sent with each request, like the Mac's
/// CompanionManager.conversationHistory. Not thread-safe: the app only touches it on the UI thread.
/// </summary>
public sealed class CompanionConversationHistory
{
    /// <summary>Same cap as the Mac, so context (and per-request cost) stays bounded.</summary>
    public const int MaximumExchangeCount = 10;

    /// <summary>How many seen notes from exchanges that fell out of the window are kept, as a cheap memory of earlier on.</summary>
    public const int MaximumEarlierNotes = 12;

    private readonly List<ConversationExchange> exchanges = [];
    private readonly List<string> earlierNotes = [];

    /// <summary>Exchanges recorded so far, ever (E11 T11.18): a live Claude session holding this many needs only the new turn.</summary>
    public long Version { get; private set; }

    /// <summary>The model's own notes on what the user was doing, from exchanges older than the window (E11 T11.20).</summary>
    public string? EarlierSummary => earlierNotes.Count == 0 ? null : "[earlier] " + string.Join("; ", earlierNotes);

    /// <summary>A copy, so a request in flight isn't affected by an exchange recorded meanwhile.</summary>
    public IReadOnlyList<ConversationExchange> GetExchanges() => exchanges.ToArray();

    /// <summary>
    /// Stores the exchange, dropping the oldest beyond the cap. The runner passes the reply's history text (spoken lines, what
    /// was shown, seen notes; E11 T11.7); a trailing [POINT] tag is still stripped (Mac: "so it doesn't confuse future context").
    /// </summary>
    public void Record(string userTranscript, string fullResponseText)
    {
        exchanges.Add(new ConversationExchange(userTranscript, PointTagParser.ParsePointingCoordinates(fullResponseText).SpokenText));
        Version++;
        if (exchanges.Count <= MaximumExchangeCount) return;
        int dropCount = exchanges.Count - MaximumExchangeCount;
        foreach (ConversationExchange dropped in exchanges.Take(dropCount))
            foreach (string line in dropped.AssistantResponse.Split('\n'))
                if (line.StartsWith("[seen: ", StringComparison.Ordinal) && line.EndsWith(']')) earlierNotes.Add(line[7..^1]);
        if (earlierNotes.Count > MaximumEarlierNotes) earlierNotes.RemoveRange(0, earlierNotes.Count - MaximumEarlierNotes);
        exchanges.RemoveRange(0, dropCount);
    }
}

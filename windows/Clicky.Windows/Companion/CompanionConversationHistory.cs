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

    private readonly List<ConversationExchange> exchanges = [];

    /// <summary>A copy, so a request in flight isn't affected by an exchange recorded meanwhile.</summary>
    public IReadOnlyList<ConversationExchange> GetExchanges() => exchanges.ToArray();

    /// <summary>
    /// Stores the exchange with the trailing [POINT] tag stripped (Mac: "so it doesn't confuse future context"),
    /// dropping the oldest exchanges beyond the cap.
    /// </summary>
    public void Record(string userTranscript, string fullResponseText)
    {
        exchanges.Add(new ConversationExchange(userTranscript, PointTagParser.ParsePointingCoordinates(fullResponseText).SpokenText));
        if (exchanges.Count > MaximumExchangeCount) exchanges.RemoveRange(0, exchanges.Count - MaximumExchangeCount);
    }
}

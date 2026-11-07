using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// One screenshot plus the text label the model sees right after it, such as
/// "screen 1 of 2 — cursor is on this screen (primary focus) (image dimensions: 1280x720 pixels)".
/// </summary>
/// <param name="JpegImageBytes">JPEG-encoded screenshot.</param>
/// <param name="ImageLabel">Label sent alongside the image; matches the Mac wording.</param>
public sealed record LabeledScreenshotImage(byte[] JpegImageBytes, string ImageLabel);

/// <summary>One prior exchange, so the model remembers the conversation (Mac keeps the last 10).</summary>
public sealed record ConversationExchange(string UserTranscript, string AssistantResponse);

/// <summary>
/// Everything a backend needs for one vision request. Mirrors the arguments of
/// ClaudeAPI.analyzeImageStreaming in Swift.
/// </summary>
public sealed record CompanionChatRequest(
    string SystemPrompt,
    IReadOnlyList<LabeledScreenshotImage> LabeledScreenshotImages,
    IReadOnlyList<ConversationExchange> ConversationHistory,
    string UserPrompt)
{
    /// <summary>The app's [context] lines for this turn (E11 T11.19), sent before the screenshots and the user's words.</summary>
    public string? TurnContext { get; init; }

    /// <summary>
    /// How many exchanges the conversation had recorded when this request was built (it only grows, past the 10 that are kept).
    /// A live Claude session holding exactly that conversation takes only the new turn (E11 T11.18); null means no session.
    /// </summary>
    public long? HistoryVersion { get; init; }

    /// <summary>Called (on the backend's thread) when the model used web search this turn: its results are untrusted text.</summary>
    public Action? OnWebContent { get; init; }
}

/// <summary>
/// A provider that turns a <see cref="CompanionChatRequest"/> into the model's full text reply
/// (still containing any [POINT] tag; parsing happens in PointTagParser).
/// </summary>
public interface ICompanionChatBackend
{
    ChatBackendKind BackendKind { get; }

    /// <summary>Human-readable name for the tray menu, e.g. "Claude Code CLI".</summary>
    string DisplayName { get; }

    /// <summary>Clicky is quitting: stop anything kept alive between requests.</summary>
    void Shutdown() => DiscardPrestarted();

    /// <summary>
    /// Sends the request and returns the full reply text. Throws on failure or timeout,
    /// so the router can try the fallback backend.
    /// </summary>
    /// <param name="onTextDelta">
    /// Called (on any thread) with each piece of the reply as it arrives, so speech can start before the reply is complete.
    /// A backend that can't stream calls it once with the whole reply, or not at all.
    /// </param>
    Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null);

    /// <summary>
    /// Starts the slow part of a coming request now (a CLI process, at the push-to-talk press), so it overlaps the user's speech.
    /// The next request with this system prompt and these screenshots uses it; a later prestart replaces it. Never throws.
    /// Backends with nothing to start (HTTP) keep this no-op.
    /// </summary>
    void Prestart(string systemPrompt, IReadOnlyList<LabeledScreenshotImage> screenshots)
    {
    }

    /// <summary>Kills a prestarted process nobody will use (a tap, an empty transcript, Clicky quitting).</summary>
    void DiscardPrestarted()
    {
    }
}

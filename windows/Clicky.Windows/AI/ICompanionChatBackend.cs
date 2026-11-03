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
    string UserPrompt);

/// <summary>
/// A provider that turns a <see cref="CompanionChatRequest"/> into the model's full text reply
/// (still containing any [POINT] tag; parsing happens in PointTagParser).
/// </summary>
public interface ICompanionChatBackend
{
    ChatBackendKind BackendKind { get; }

    /// <summary>Human-readable name for the tray menu, e.g. "Claude Code CLI".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Sends the request and returns the full reply text. Throws on failure or timeout,
    /// so the router can try the fallback backend.
    /// </summary>
    Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken);
}

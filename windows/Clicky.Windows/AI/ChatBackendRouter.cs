using System.Diagnostics;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>The reply plus which backend produced it (the fallback, if the routed one failed).</summary>
public sealed record RoutedChatResponse(string ResponseText, ChatBackendKind AnsweringBackendKind);

/// <summary>
/// Sends each job to the backend the user picked for it and, if that backend throws or times out before any text arrived, tries
/// the configured fallback exactly once. Once text has streamed (and may already be spoken), a failure ends the turn instead.
/// Routing is read on every request, so tray / panel changes apply to the next question.
/// </summary>
internal sealed class ChatBackendRouter(Func<JobBackendRouting> getCurrentRouting, IEnumerable<ICompanionChatBackend> chatBackends)
{
    private readonly IReadOnlyDictionary<ChatBackendKind, ICompanionChatBackend> chatBackendsByKind =
        chatBackends.ToDictionary(chatBackend => chatBackend.BackendKind);

    public async Task<RoutedChatResponse> GetResponseAsync(
        CompanionJobKind companionJobKind, CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        JobBackendRouting routing = getCurrentRouting();
        ChatBackendKind routedBackendKind = routing.GetBackendForJob(companionJobKind);
        bool hasStreamedText = false;
        Action<string>? onRoutedTextDelta = onTextDelta is null ? null : textDelta =>
        {
            hasStreamedText = true;
            onTextDelta(textDelta);
        };
        try
        {
            return await GetResponseFromAsync(routedBackendKind, companionChatRequest, cancellationToken, onRoutedTextDelta).ConfigureAwait(false);
        }
        catch (Exception routedException) when (!cancellationToken.IsCancellationRequested && !hasStreamedText &&
                                                routing.Fallback is { } fallbackBackendKind && fallbackBackendKind != routedBackendKind)
        {
            Trace.TraceWarning($"{companionJobKind} via {routedBackendKind} failed, trying {fallbackBackendKind}: {routedException.Message}");
            try
            {
                return await GetResponseFromAsync(fallbackBackendKind, companionChatRequest, cancellationToken, onTextDelta).ConfigureAwait(false);
            }
            catch (Exception fallbackException) when (!cancellationToken.IsCancellationRequested)
            {
                // Both messages matter: the first says why the user's choice failed, the second why the rescue did.
                throw new InvalidOperationException(
                    $"{routedException.Message} Fallback {DescribeBackend(fallbackBackendKind)} also failed: {fallbackException.Message}",
                    new AggregateException(routedException, fallbackException));
            }
        }
    }

    /// <summary>Prestarts the request on the backend the job is routed to now (see <see cref="ICompanionChatBackend.Prestart"/>).</summary>
    public void Prestart(CompanionJobKind companionJobKind, string systemPrompt, IReadOnlyList<LabeledScreenshotImage> screenshots)
    {
        if (chatBackendsByKind.TryGetValue(getCurrentRouting().GetBackendForJob(companionJobKind), out ICompanionChatBackend? chatBackend))
            chatBackend.Prestart(systemPrompt, screenshots);
    }

    public void DiscardPrestarted()
    {
        foreach (ICompanionChatBackend chatBackend in chatBackendsByKind.Values) chatBackend.DiscardPrestarted();
    }

    private async Task<RoutedChatResponse> GetResponseFromAsync(
        ChatBackendKind chatBackendKind, CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta)
    {
        if (!chatBackendsByKind.TryGetValue(chatBackendKind, out ICompanionChatBackend? chatBackend))
            throw new InvalidOperationException($"No {chatBackendKind} backend is available.");
        string responseText = await chatBackend.GetResponseTextAsync(companionChatRequest, cancellationToken, onTextDelta).ConfigureAwait(false);
        return new RoutedChatResponse(responseText, chatBackendKind);
    }

    private string DescribeBackend(ChatBackendKind chatBackendKind) =>
        chatBackendsByKind.TryGetValue(chatBackendKind, out ICompanionChatBackend? chatBackend) ? chatBackend.DisplayName : chatBackendKind.ToString();
}

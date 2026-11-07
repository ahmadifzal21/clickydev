using System.IO;
using System.Net.Http;
using System.Text;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// Sends the request through the user's deployed <c>worker/</c> (<c>POST /chat</c>), which adds the Anthropic API key and
/// streams the Messages API SSE back. Same request shape and parsing as the Mac's ClaudeAPI.analyzeImageStreaming.
/// </summary>
internal sealed class CloudflareWorkerChatBackend(CloudflareWorkerBackendSettings settings, HttpClient? httpClient = null)
    : ICompanionChatBackend
{
    /// <summary>One shared client keeps the TLS connection warm between questions; timeouts come from our own token.</summary>
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient httpClient = httpClient ?? SharedHttpClient;

    public ChatBackendKind BackendKind => ChatBackendKind.CloudflareWorker;
    public string DisplayName => "Cloudflare Worker";

    public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri chatUri = CloudflareWorkerProtocol.BuildChatUri(settings);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds));
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, chatUri)
            {
                Content = new StringContent(
                    CloudflareWorkerProtocol.BuildRequestJson(companionChatRequest, settings.Model), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage httpResponse = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                string errorBody = await httpResponse.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                throw new InvalidOperationException(CloudflareWorkerProtocol.DescribeHttpError((int)httpResponse.StatusCode, errorBody));
            }
            await using Stream eventStream = await httpResponse.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            using var eventStreamReader = new StreamReader(eventStream, Encoding.UTF8);
            return await CloudflareWorkerProtocol.ReadResponseTextAsync(eventStreamReader, timeoutSource.Token, onTextDelta).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Cloudflare Worker did not respond within {settings.ResponseTimeoutSeconds} seconds.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"Could not reach the Cloudflare Worker at {chatUri.GetLeftPart(UriPartial.Authority)}: {exception.Message}", exception);
        }
    }
}

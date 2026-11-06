using System.IO;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// Pure request building and SSE parsing for the Worker's <c>/chat</c> route, which forwards the body to the Anthropic
/// Messages API unchanged and streams its server-sent events back. Kept apart from HttpClient so it is unit-testable.
/// </summary>
internal static class CloudflareWorkerProtocol
{
    /// <summary>Same cap as the Mac's streaming request; replies are one or two spoken sentences plus a [POINT] tag.</summary>
    public const int MaximumResponseTokens = 1024;

    /// <summary>Returns "{WorkerBaseUrl}/chat", or throws a "not set up" error the user can act on.</summary>
    public static Uri BuildChatUri(CloudflareWorkerBackendSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WorkerBaseUrl))
            throw new InvalidOperationException(
                "The Cloudflare Worker backend is not set up. Deploy worker/ and set cloudflareWorker.workerBaseUrl in clicky.config.json.");
        if (!Uri.TryCreate(settings.WorkerBaseUrl.Trim().TrimEnd('/') + "/chat", UriKind.Absolute, out Uri? chatUri) ||
            (chatUri.Scheme != Uri.UriSchemeHttps && chatUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException(
                $"cloudflareWorker.workerBaseUrl '{settings.WorkerBaseUrl}' is not an http(s) URL, e.g. https://clicky-proxy.you.workers.dev.");
        return chatUri;
    }

    /// <summary>Anthropic Messages body, matching ClaudeAPI.analyzeImageStreaming: history as plain text turns, then images + labels + question.</summary>
    public static string BuildRequestJson(CompanionChatRequest companionChatRequest, string model)
    {
        var messages = new List<object>();
        foreach (ConversationExchange exchange in companionChatRequest.ConversationHistory)
        {
            messages.Add(new { role = "user", content = exchange.UserTranscript });
            messages.Add(new { role = "assistant", content = exchange.AssistantResponse });
        }
        messages.Add(new { role = "user", content = ClaudeCodeCliProtocol.BuildCurrentUserContentBlocks(companionChatRequest) });
        return JsonSerializer.Serialize(new
        {
            model,
            max_tokens = MaximumResponseTokens,
            stream = true,
            system = companionChatRequest.SystemPrompt,
            messages
        });
    }

    /// <summary>
    /// Reads the SSE stream to the end and returns the concatenated <c>text_delta</c> chunks, handing each to
    /// <paramref name="onTextDelta"/> as it arrives. An in-stream <c>error</c> event (e.g. overloaded) throws; the router falls
    /// back only if no text had arrived yet.
    /// </summary>
    public static async Task<string> ReadResponseTextAsync(TextReader eventStreamReader, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        var responseText = new StringBuilder();
        while (await eventStreamReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string eventData = line[5..].TrimStart();
            if (eventData == "[DONE]") break;

            JsonElement eventPayload;
            try { eventPayload = JsonSerializer.Deserialize<JsonElement>(eventData); }
            catch (JsonException) { continue; } // The Mac skips malformed lines too.
            if (eventPayload.ValueKind != JsonValueKind.Object) continue;

            switch (GetStringProperty(eventPayload, "type"))
            {
                case "content_block_delta"
                    when eventPayload.TryGetProperty("delta", out JsonElement delta) &&
                         GetStringProperty(delta, "type") == "text_delta" &&
                         GetStringProperty(delta, "text") is { } textChunk:
                    responseText.Append(textChunk);
                    onTextDelta?.Invoke(textChunk);
                    break;
                case "error":
                    throw new InvalidOperationException($"The Claude API reported an error mid-answer: {DescribeApiError(eventData)}");
                case "message_stop":
                    return RequireText(responseText);
            }
        }
        return RequireText(responseText);
    }

    /// <summary>Message for a non-2xx response from the Worker (its own 500/404, or Anthropic's error JSON passed through).</summary>
    public static string DescribeHttpError(int statusCode, string responseBody)
    {
        string details = DescribeApiError(responseBody);
        string hint = statusCode switch
        {
            401 or 403 => " Check the ANTHROPIC_API_KEY secret on the Worker.",
            404 => " Check cloudflareWorker.workerBaseUrl points at the deployed worker/.",
            _ => ""
        };
        return $"The Cloudflare Worker returned HTTP {statusCode}.{hint}{(details.Length > 0 ? " " + details : "")}";
    }

    /// <summary>Pulls error.message out of Anthropic's {"type":"error","error":{...}} JSON; otherwise the trimmed raw body.</summary>
    private static string DescribeApiError(string body)
    {
        try
        {
            JsonElement root = JsonSerializer.Deserialize<JsonElement>(body);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString()!;
                if (error.ValueKind == JsonValueKind.Object && GetStringProperty(error, "message") is { } message)
                    return GetStringProperty(error, "type") is { } errorType ? $"{errorType}: {message}" : message;
            }
        }
        catch (JsonException) { }
        string trimmed = body.Trim();
        return trimmed.Length > 1500 ? trimmed[..1500] + "…" : trimmed;
    }

    private static string RequireText(StringBuilder responseText) => responseText.Length > 0
        ? responseText.ToString()
        : throw new InvalidOperationException("The Cloudflare Worker returned an empty answer.");

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}

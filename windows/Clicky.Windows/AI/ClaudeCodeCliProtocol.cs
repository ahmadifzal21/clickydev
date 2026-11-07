using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

internal static class ClaudeCodeCliProtocol
{
    public static ProcessStartInfo BuildStartInfo(ClaudeCodeCliBackendSettings settings, string systemPromptFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ExecutablePath);
        var startInfo = new ProcessStartInfo(settings.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // The prompt goes by file: an npm "claude.cmd" shim runs through cmd.exe, which can't carry newlines (or a literal %)
        // in an argument. Safe mode preserves the user's login but excludes project/user hooks, plugins and instructions.
        string[] arguments =
        [
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--system-prompt-file", systemPromptFilePath, "--tools", "", "--safe-mode", "--strict-mcp-config",
            "--disable-slash-commands", "--no-session-persistence"
        ];
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(settings.Model);
        }
        if (!string.IsNullOrWhiteSpace(settings.Effort))
        {
            startInfo.ArgumentList.Add("--effort");
            startInfo.ArgumentList.Add(settings.Effort);
        }
        return startInfo;
    }

    public static string SerializeInput(CompanionChatRequest companionChatRequest)
    {
        var input = new StringBuilder();
        foreach (ConversationExchange exchange in companionChatRequest.ConversationHistory)
        {
            // SDKUserMessage.shouldQuery=false seeds history without paying for another answer to an old question.
            input.AppendLine(SerializeUserMessage(exchange.UserTranscript, shouldQuery: false));
            string messageUuid = Guid.NewGuid().ToString();
            input.AppendLine(JsonSerializer.Serialize(new
            {
                type = "assistant",
                uuid = messageUuid,
                session_id = "",
                parent_tool_use_id = (string?)null,
                message = new
                {
                    id = messageUuid, type = "message", role = "assistant", model = "<synthetic>",
                    content = new[] { new { type = "text", text = exchange.AssistantResponse } },
                    stop_reason = "end_turn", stop_sequence = (string?)null,
                    usage = new { input_tokens = 0, output_tokens = 0 }
                }
            }));
        }

        input.AppendLine(SerializeUserMessage(BuildCurrentUserContentBlocks(companionChatRequest), shouldQuery: true));
        return input.ToString();
    }

    /// <summary>
    /// Anthropic Messages content for the current turn, like the Mac: each image followed by its label, then the question.
    /// Shared with the Cloudflare Worker backend, which sends the same Messages API shape.
    /// </summary>
    public static List<object> BuildCurrentUserContentBlocks(CompanionChatRequest companionChatRequest)
    {
        var contentBlocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(companionChatRequest.TurnContext))
            contentBlocks.Add(new { type = "text", text = companionChatRequest.TurnContext });
        foreach (LabeledScreenshotImage screenshot in companionChatRequest.LabeledScreenshotImages)
        {
            contentBlocks.Add(new
            {
                type = "image",
                source = new { type = "base64", media_type = "image/jpeg", data = Convert.ToBase64String(screenshot.JpegImageBytes) }
            });
            contentBlocks.Add(new { type = "text", text = screenshot.ImageLabel });
        }
        contentBlocks.Add(new { type = "text", text = companionChatRequest.UserPrompt });
        return contentBlocks;
    }

    private static string SerializeUserMessage(object content, bool shouldQuery) => JsonSerializer.Serialize(new
    {
        type = "user", message = new { role = "user", content }, parent_tool_use_id = (string?)null,
        shouldQuery,
        // A screen question mentioning @paths or /commands must remain literal model input.
        client_composed = true
    });

    /// <summary>
    /// The reply text in one --include-partial-messages line
    /// (<c>{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"…"}}}</c>), else null.
    /// </summary>
    public static string? TryGetTextDelta(string outputLine)
    {
        if (!outputLine.Contains("\"text_delta\"", StringComparison.Ordinal)) return null; // Most lines aren't; skip parsing them.
        try
        {
            using JsonDocument message = JsonDocument.Parse(outputLine);
            JsonElement root = message.RootElement;
            return root.ValueKind == JsonValueKind.Object && GetStringProperty(root, "type") == "stream_event" &&
                   root.TryGetProperty("event", out JsonElement streamEvent) && streamEvent.ValueKind == JsonValueKind.Object &&
                   GetStringProperty(streamEvent, "type") == "content_block_delta" &&
                   streamEvent.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object &&
                   GetStringProperty(delta, "type") == "text_delta"
                ? GetStringProperty(delta, "text")
                : null;
        }
        catch (JsonException)
        {
            return null; // ParseResponse reports invalid output once the process ends.
        }
    }

    public static string ParseResponse(string standardOutput, string standardError, int exitCode)
    {
        JsonElement? finalResult = null;
        using var outputReader = new StringReader(standardOutput);
        try
        {
            while (outputReader.ReadLine() is { } outputLine)
            {
                if (string.IsNullOrWhiteSpace(outputLine)) continue;
                using JsonDocument message = JsonDocument.Parse(outputLine);
                if (message.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (GetStringProperty(message.RootElement, "type") == "result") finalResult = message.RootElement.Clone();
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Claude Code returned invalid stream JSON. Update Claude Code and try again.", exception);
        }

        string? responseText = finalResult is { } result ? GetStringProperty(result, "result") : null;
        bool isSuccessful = finalResult is { } finalMessage &&
            GetStringProperty(finalMessage, "subtype") == "success" &&
            finalMessage.TryGetProperty("is_error", out JsonElement isError) && isError.ValueKind == JsonValueKind.False;
        if (exitCode != 0 || !isSuccessful)
        {
            var errorDetails = new List<string>();
            if (!string.IsNullOrWhiteSpace(standardError)) errorDetails.Add(standardError.Trim());
            if (!isSuccessful && !string.IsNullOrWhiteSpace(responseText)) errorDetails.Add(responseText);
            if (finalResult is { } errorResult && errorResult.TryGetProperty("errors", out JsonElement errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                errorDetails.AddRange(errors.EnumerateArray().Where(error => error.ValueKind == JsonValueKind.String)
                    .Select(error => error.GetString()!));
            }
            string details = string.Join("\n", errorDetails);
            string[] authenticationErrors = ["not logged in", "authentication_failed", "authentication_error", "invalid api key", "OAuth token has expired", "/login"];
            if (authenticationErrors.Any(error => details.Contains(error, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Claude Code is not logged in or its login has expired. Run 'claude auth login' in a terminal, then try again.");

            if (details.Length > 1500) details = details[..1500] + "…";
            string reason = exitCode != 0 ? $"exited with code {exitCode}" :
                finalResult is null ? "did not return a final result" : "could not complete the request";
            throw new InvalidOperationException($"Claude Code {reason}.{(details.Length > 0 ? " " + details : "")}");
        }
        if (string.IsNullOrWhiteSpace(responseText)) throw new InvalidOperationException("Claude Code returned an empty answer.");
        return responseText;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}

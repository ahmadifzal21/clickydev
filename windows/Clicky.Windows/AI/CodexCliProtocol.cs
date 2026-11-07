using System.Diagnostics;
using System.Text;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// Pure argument / prompt / result handling for <c>codex exec</c>, kept apart from the process and temp files so it can be
/// unit-tested without the real CLI.
/// </summary>
internal static class CodexCliProtocol
{
    public static ProcessStartInfo BuildStartInfo(
        CodexCliBackendSettings settings, IReadOnlyList<string> screenshotFilePaths, string lastMessageFilePath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ExecutablePath);
        var startInfo = new ProcessStartInfo(settings.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // An empty scratch directory: Codex has nothing of the user's to read even inside its read-only sandbox.
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // --ignore-user-config skips config.toml (hooks, notify, custom provider, xhigh effort): ~6 s instead of 20-28 s per
        // answer. Login still comes from CODEX_HOME; codexCli.model in clicky.config.json picks the model.
        string[] arguments =
        [
            "exec", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only", "--ephemeral", "--color", "never", "--json",
            "--output-last-message", lastMessageFilePath
        ];
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(settings.Model);
        }
        if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"model_reasoning_effort=\"{settings.ReasoningEffort}\"");
        }
        foreach (string screenshotFilePath in screenshotFilePaths)
        {
            startInfo.ArgumentList.Add("--image");
            startInfo.ArgumentList.Add(screenshotFilePath);
        }
        // --image takes several values, so "--" stops it from swallowing "-" (read the prompt from stdin, which has no length limit).
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    /// <summary>codex exec has no system-prompt or history input, so both are folded into the one prompt.</summary>
    public static string BuildPrompt(CompanionChatRequest companionChatRequest)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(companionChatRequest.SystemPrompt.Trim()).AppendLine();
        prompt.AppendLine("Answer directly. Do not run commands, edit files or use tools.").AppendLine();
        if (companionChatRequest.ConversationHistory.Count > 0)
        {
            prompt.AppendLine("Conversation so far:");
            foreach (ConversationExchange exchange in companionChatRequest.ConversationHistory)
            {
                prompt.AppendLine($"User: {exchange.UserTranscript}");
                prompt.AppendLine($"Assistant: {exchange.AssistantResponse}");
            }
            prompt.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(companionChatRequest.TurnContext))
            prompt.AppendLine(companionChatRequest.TurnContext.Trim()).AppendLine();
        IReadOnlyList<LabeledScreenshotImage> screenshots = companionChatRequest.LabeledScreenshotImages;
        if (screenshots.Count > 0)
        {
            prompt.AppendLine("Attached images, in order:");
            for (int index = 0; index < screenshots.Count; index++)
                prompt.AppendLine($"Image {index + 1}: {screenshots[index].ImageLabel}");
            prompt.AppendLine();
        }
        prompt.AppendLine($"User: {companionChatRequest.UserPrompt}");
        return prompt.ToString();
    }

    /// <summary>
    /// The reply text in one --json event line (<c>{"type":"item.completed","item":{"type":"agent_message","text":"…"}}</c>), else
    /// null. Codex sends the message whole, not in pieces, so this only saves waiting for the process to exit.
    /// </summary>
    public static string? TryGetAgentMessageText(string outputLine)
    {
        if (!outputLine.Contains("\"agent_message\"", StringComparison.Ordinal)) return null;
        try
        {
            using System.Text.Json.JsonDocument eventDocument = System.Text.Json.JsonDocument.Parse(outputLine);
            System.Text.Json.JsonElement root = eventDocument.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   root.TryGetProperty("type", out var eventType) && eventType.ValueKind == System.Text.Json.JsonValueKind.String &&
                   eventType.GetString() == "item.completed" &&
                   root.TryGetProperty("item", out var item) && item.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   item.TryGetProperty("type", out var itemType) && itemType.ValueKind == System.Text.Json.JsonValueKind.String &&
                   itemType.GetString() == "agent_message" &&
                   item.TryGetProperty("text", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <param name="lastMessageText">Contents of the -o file, or null if Codex never wrote it.</param>
    public static string ParseResponse(string? lastMessageText, string standardError, int exitCode)
    {
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(lastMessageText)) return lastMessageText.Trim();

        string details = standardError.Trim();
        string[] authenticationErrors = ["not logged in", "codex login", "401 unauthorized", "unauthorized", "token_expired", "refresh token"];
        if (authenticationErrors.Any(error => details.Contains(error, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Codex is not logged in or its login has expired. Run 'codex login' in a terminal, then try again.");

        // Codex surrounds its "ERROR: ..." lines with config warnings and hook chatter; those lines are the actual reason.
        string[] errorLines = details.Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("ERROR:", StringComparison.Ordinal)).Distinct().ToArray();
        if (errorLines.Length > 0) details = string.Join("\n", errorLines);
        // Otherwise: Codex echoes the whole prompt to stderr, so keep the tail where the error is.
        if (details.Length > 1500) details = "…" + details[^1500..];
        string reason = exitCode != 0 ? $"exited with code {exitCode}" : "returned an empty answer";
        throw new InvalidOperationException($"Codex {reason}.{(details.Length > 0 ? " " + details : "")}");
    }
}

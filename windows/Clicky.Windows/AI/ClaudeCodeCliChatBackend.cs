using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// One ephemeral, tools-disabled CLI invocation per request, using the user's existing Claude Code login. A push-to-talk press
/// can prestart that invocation (<see cref="Prestart"/>): Claude Code spends about a second starting up before it reads stdin.
/// </summary>
internal sealed class ClaudeCodeCliChatBackend(ClaudeCodeCliBackendSettings settings) : ICompanionChatBackend
{
    private readonly PrestartedCliProcessSlot prestartedProcess = new();

    public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
    public string DisplayName => "Claude Code CLI";

    public void Prestart(string systemPrompt, IReadOnlyList<LabeledScreenshotImage> screenshots)
    {
        string systemPromptFilePath = NewSystemPromptFilePath();
        try
        {
            File.WriteAllText(systemPromptFilePath, systemPrompt);
            CliChatProcess chatProcess = CliChatProcess.Start(ClaudeCodeCliProtocol.BuildStartInfo(settings, systemPromptFilePath));
            prestartedProcess.Put(new PrestartedCliProcess(chatProcess, GetMatchKey(systemPrompt), systemPromptFilePath,
                () => DeleteSystemPromptFile(systemPromptFilePath)));
        }
        catch (Exception exception)
        {
            // The request will start its own process (and report a missing executable properly).
            Trace.TraceWarning($"Couldn't prestart Claude Code: {exception.Message}");
            DeleteSystemPromptFile(systemPromptFilePath);
        }
    }

    public void DiscardPrestarted() => prestartedProcess.Discard();

    public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object requestMatchKey = GetMatchKey(companionChatRequest.SystemPrompt);
        using PrestartedCliProcess? prestarted = prestartedProcess.Take(matchKey => matchKey.Equals(requestMatchKey));
        string systemPromptFilePath = prestarted?.WorkPath ?? NewSystemPromptFilePath();
        Action<string>? onOutputLine = onTextDelta is null ? null : outputLine =>
        {
            if (ClaudeCodeCliProtocol.TryGetTextDelta(outputLine) is { Length: > 0 } textDelta) onTextDelta(textDelta);
        };
        try
        {
            string standardInput = ClaudeCodeCliProtocol.SerializeInput(companionChatRequest);
            TimeSpan responseTimeout = TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds);
            (string StandardOutput, string StandardError, int ExitCode) result;
            if (prestarted is not null)
            {
                result = await prestarted.Process.SendAsync(standardInput, responseTimeout, cancellationToken, onOutputLine).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(systemPromptFilePath, companionChatRequest.SystemPrompt, cancellationToken).ConfigureAwait(false);
                result = await CliChatProcess.RunAsync(ClaudeCodeCliProtocol.BuildStartInfo(settings, systemPromptFilePath),
                    standardInput, responseTimeout, cancellationToken, onOutputLine).ConfigureAwait(false);
            }
            return ClaudeCodeCliProtocol.ParseResponse(result.StandardOutput, result.StandardError, result.ExitCode);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new InvalidOperationException(
                $"Claude Code was not found at '{settings.ExecutablePath}'. Install Claude Code or set claudeCodeCli.executablePath in clicky.config.json to its executable.", exception);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Claude Code did not respond within {settings.ResponseTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            if (prestarted is null) DeleteSystemPromptFile(systemPromptFilePath); // A prestarted one deletes its own on dispose.
        }
    }

    /// <summary>A prestarted process suits a request with the same system prompt, executable, model and effort (settings can change).</summary>
    private object GetMatchKey(string systemPrompt) => (systemPrompt, settings.ExecutablePath, settings.Model, settings.Effort);

    private static string NewSystemPromptFilePath() => Path.Combine(Path.GetTempPath(), $"clicky-claude-{Guid.NewGuid():N}.txt");

    private static void DeleteSystemPromptFile(string systemPromptFilePath)
    {
        try { File.Delete(systemPromptFilePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

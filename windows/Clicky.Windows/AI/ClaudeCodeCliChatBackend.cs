using System.ComponentModel;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>One ephemeral, tools-disabled CLI invocation per request, using the user's existing Claude Code login.</summary>
internal sealed class ClaudeCodeCliChatBackend(ClaudeCodeCliBackendSettings settings) : ICompanionChatBackend
{
    public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
    public string DisplayName => "Claude Code CLI";

    public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await CliChatProcess.RunAsync(
                ClaudeCodeCliProtocol.BuildStartInfo(settings, companionChatRequest.SystemPrompt),
                ClaudeCodeCliProtocol.SerializeInput(companionChatRequest),
                TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds), cancellationToken).ConfigureAwait(false);
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
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// Claude Code CLI with tools disabled, using the user's existing Claude Code login. By default one process is kept across
/// turns (E11 T11.18, <see cref="ClaudeSessionProcess"/>): a request whose conversation the session already holds sends only
/// its new turn; otherwise a fresh session is started and given the text history first. A session is replaced when it has
/// been idle 10 minutes, holds 32 screenshots, or its input has grown past 60k tokens (E11 T11.20: the fresh one starts from
/// the text history, which is far smaller than the screenshots). Without a session (KeepSessionAlive off, or a request with
/// no history version) each request gets one process, as before. Either way a press can prestart the process
/// (<see cref="Prestart"/>): Claude Code spends about a second starting up before it reads stdin.
/// </summary>
internal sealed class ClaudeCodeCliChatBackend(ClaudeCodeCliBackendSettings settings) : ICompanionChatBackend
{
    internal static readonly TimeSpan SessionIdleLimit = TimeSpan.FromMinutes(10);
    internal const int SessionScreenshotLimit = 32;
    internal const int SessionInputTokenLimit = 60_000;

    private readonly PrestartedCliProcessSlot prestartedProcess = new();
    private readonly object sessionGate = new();
    private ClaudeSessionProcess? session;

    public ChatBackendKind BackendKind => ChatBackendKind.ClaudeCodeCli;
    public string DisplayName => "Claude Code CLI";

    public void Prestart(string systemPrompt, IReadOnlyList<LabeledScreenshotImage> screenshots)
    {
        if (settings.KeepSessionAlive)
        {
            lock (sessionGate)
            {
                // A live session with this prompt is already warm; otherwise start one now so the next turn skips start-up.
                if (session is { IsAlive: true } liveSession && liveSession.MatchKey.Equals(GetMatchKey(systemPrompt)) && !IsWornOut(liveSession)) return;
                try { ReplaceSession(StartSession(systemPrompt)); }
                catch (Exception exception)
                {
                    Trace.TraceWarning($"Couldn't prestart a Claude session: {exception.Message}"); // The request reports it properly.
                    ReplaceSession(null);
                }
            }
            return;
        }
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

    /// <summary>A tap or an empty transcript: drop a prestarted process; a session that holds a conversation stays.</summary>
    public void DiscardPrestarted()
    {
        prestartedProcess.Discard();
        lock (sessionGate)
        {
            if (session is { HasConversation: false }) ReplaceSession(null);
        }
    }

    public void Shutdown()
    {
        prestartedProcess.Discard();
        lock (sessionGate) ReplaceSession(null);
    }

    public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action<string>? onOutputLine = outputLine =>
        {
            if (companionChatRequest.OnWebContent is { } onWebContent && ClaudeCodeCliProtocol.IsToolUse(outputLine)) onWebContent();
            if (onTextDelta is not null && ClaudeCodeCliProtocol.TryGetTextDelta(outputLine) is { Length: > 0 } textDelta) onTextDelta(textDelta);
        };
        try
        {
            if (settings.KeepSessionAlive && companionChatRequest.HistoryVersion is { } historyVersion)
                return await GetResponseFromSessionAsync(companionChatRequest, historyVersion, cancellationToken, onOutputLine).ConfigureAwait(false);
            return await GetResponseFromOneProcessAsync(companionChatRequest, cancellationToken, onOutputLine).ConfigureAwait(false);
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

    private async Task<string> GetResponseFromSessionAsync(CompanionChatRequest companionChatRequest, long historyVersion,
        CancellationToken cancellationToken, Action<string>? onOutputLine)
    {
        ClaudeSessionProcess turnSession;
        string standardInput;
        lock (sessionGate)
        {
            object requestMatchKey = GetMatchKey(companionChatRequest.SystemPrompt);
            bool canContinue = session is { IsAlive: true, IsInTurn: false } liveSession && liveSession.MatchKey.Equals(requestMatchKey)
                && !IsWornOut(liveSession) && (!liveSession.HasConversation || liveSession.HistoryVersion == historyVersion);
            if (!canContinue)
            {
                if (session is not null) Trace.WriteLine($"Claude session replaced ({DescribeWhy(session, requestMatchKey, historyVersion)}).");
                ReplaceSession(StartSession(companionChatRequest.SystemPrompt));
            }
            turnSession = session!;
            // A session with no conversation yet gets the whole (text) history first; a live one only the new turn.
            standardInput = turnSession.HasConversation
                ? ClaudeCodeCliProtocol.SerializeCurrentTurn(companionChatRequest)
                : ClaudeCodeCliProtocol.SerializeInput(companionChatRequest);
        }
        try
        {
            string turnOutput = await turnSession.RunTurnAsync(standardInput, TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds),
                cancellationToken, onOutputLine).ConfigureAwait(false);
            string responseText = ClaudeCodeCliProtocol.ParseResponse(turnOutput, "", 0);
            turnSession.HistoryVersion = historyVersion + 1; // The runner records exactly this one exchange next.
            turnSession.ScreenshotCount += companionChatRequest.LabeledScreenshotImages.Count;
            return responseText;
        }
        catch
        {
            // Cancelled, timed out or failed: the session's conversation no longer matches what the runner keeps.
            lock (sessionGate) if (session == turnSession) ReplaceSession(null);
            throw;
        }
    }

    private async Task<string> GetResponseFromOneProcessAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onOutputLine)
    {
        object requestMatchKey = GetMatchKey(companionChatRequest.SystemPrompt);
        using PrestartedCliProcess? prestarted = prestartedProcess.Take(matchKey => matchKey.Equals(requestMatchKey));
        string systemPromptFilePath = prestarted?.WorkPath ?? NewSystemPromptFilePath();
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
        finally
        {
            if (prestarted is null) DeleteSystemPromptFile(systemPromptFilePath); // A prestarted one deletes its own on dispose.
        }
    }

    /// <summary>Too old or too big to keep going: the next turn starts a fresh session from the text history (compaction).</summary>
    private static bool IsWornOut(ClaudeSessionProcess candidate) =>
        candidate.HasConversation && (candidate.IdleTime >= SessionIdleLimit || candidate.ScreenshotCount >= SessionScreenshotLimit
            || candidate.LastInputTokens >= SessionInputTokenLimit);

    private static string DescribeWhy(ClaudeSessionProcess old, object requestMatchKey, long historyVersion) =>
        !old.IsAlive ? "it had exited" :
        old.IsInTurn ? "it was still answering" :
        !old.MatchKey.Equals(requestMatchKey) ? "the prompt or settings changed" :
        old.IdleTime >= SessionIdleLimit ? $"idle {old.IdleTime.TotalMinutes:0} min" :
        old.ScreenshotCount >= SessionScreenshotLimit ? $"{old.ScreenshotCount} screenshots, compacting" :
        old.LastInputTokens >= SessionInputTokenLimit ? $"{old.LastInputTokens / 1000}k input tokens, compacting" :
        $"conversation moved on (session {old.HistoryVersion}, request {historyVersion})";

    private ClaudeSessionProcess StartSession(string systemPrompt)
    {
        string systemPromptFilePath = NewSystemPromptFilePath();
        File.WriteAllText(systemPromptFilePath, systemPrompt);
        try
        {
            return ClaudeSessionProcess.Start(ClaudeCodeCliProtocol.BuildStartInfo(settings, systemPromptFilePath), GetMatchKey(systemPrompt),
                systemPromptFilePath);
        }
        catch
        {
            DeleteSystemPromptFile(systemPromptFilePath);
            throw;
        }
    }

    /// <summary>Call holding <see cref="sessionGate"/>.</summary>
    private void ReplaceSession(ClaudeSessionProcess? replacement)
    {
        ClaudeSessionProcess? old = session;
        session = replacement;
        old?.Dispose();
    }

    /// <summary>A process suits a request with the same system prompt, executable, model and effort (settings can change).</summary>
    private object GetMatchKey(string systemPrompt) => (systemPrompt, settings.ExecutablePath, settings.Model, settings.Effort);

    private static string NewSystemPromptFilePath() => Path.Combine(Path.GetTempPath(), $"clicky-claude-{Guid.NewGuid():N}.txt");

    private static void DeleteSystemPromptFile(string systemPromptFilePath)
    {
        try { File.Delete(systemPromptFilePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Clicky.Windows.AI;

/// <summary>
/// One Claude Code process kept alive across turns (E11 T11.18). <c>claude -p --input-format stream-json</c> answers each user
/// message written to its stdin with a <c>result</c> line and waits for the next while stdin stays open, keeping the whole
/// conversation, screenshots included, in its context (and in the API's prompt cache). A turn that is cancelled or times out
/// kills the process; the backend then starts a fresh one and replays the text history. One turn at a time.
/// </summary>
internal sealed class ClaudeSessionProcess : IDisposable
{
    private readonly Process process;
    private readonly Channel<string> outputLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly StringBuilder recentErrorOutput = new();
    private readonly Stopwatch idleClock = Stopwatch.StartNew();
    private int isInTurn;

    private ClaudeSessionProcess(ProcessStartInfo startInfo, object matchKey, string promptFilePath)
    {
        MatchKey = matchKey;
        PromptFilePath = promptFilePath;
        startInfo.FileName = CliChatProcess.ResolveExecutablePath(startInfo.FileName, Environment.GetEnvironmentVariable("PATH"));
        process = new Process { StartInfo = startInfo };
        process.Start();
        _ = PumpOutputAsync();
        _ = PumpErrorsAsync();
    }

    public static ClaudeSessionProcess Start(ProcessStartInfo startInfo, object matchKey, string promptFilePath) =>
        new(startInfo, matchKey, promptFilePath);

    /// <summary>The system prompt, executable, model and effort it was started with.</summary>
    public object MatchKey { get; }

    /// <summary>Its system prompt file, deleted with the process.</summary>
    public string PromptFilePath { get; }

    public bool IsAlive => !process.HasExited;

    /// <summary>False until its first turn: a fresh process can still be given the whole history.</summary>
    public bool HasConversation { get; private set; }

    /// <summary>The conversation history version (see CompanionChatRequest.HistoryVersion) the session holds after its last turn.</summary>
    public long HistoryVersion { get; set; } = -1;

    public int ScreenshotCount { get; set; }

    /// <summary>Input tokens of the last turn, cached ones included: how big the conversation has grown.</summary>
    public int LastInputTokens { get; private set; }

    public TimeSpan IdleTime => idleClock.Elapsed;

    public bool IsInTurn => Volatile.Read(ref isInTurn) == 1;

    /// <summary>
    /// Writes <paramref name="standardInput"/> (stream-json lines ending with the turn's user message) and returns this turn's
    /// output lines up to and including its <c>result</c> line. Throws <see cref="TimeoutException"/> or
    /// <see cref="OperationCanceledException"/> (both kill the process) or <see cref="InvalidOperationException"/> if it exits.
    /// </summary>
    public async Task<string> RunTurnAsync(string standardInput, TimeSpan responseTimeout, CancellationToken cancellationToken,
        Action<string>? onOutputLine)
    {
        if (Interlocked.Exchange(ref isInTurn, 1) != 0) throw new InvalidOperationException("This Claude session is already answering.");
        HasConversation = true;
        using CancellationTokenSource timeout = new(responseTimeout);
        using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        StringBuilder turnOutput = new();
        try
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), combined.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(combined.Token).ConfigureAwait(false);
            while (true)
            {
                if (!await outputLines.Reader.WaitToReadAsync(combined.Token).ConfigureAwait(false))
                    throw new InvalidOperationException($"Claude Code stopped mid-conversation: {RecentErrors()}");
                while (outputLines.Reader.TryRead(out string? outputLine))
                {
                    turnOutput.AppendLine(outputLine);
                    onOutputLine?.Invoke(outputLine);
                    if (IsResultLine(outputLine, out int inputTokens))
                    {
                        LastInputTokens = inputTokens;
                        idleClock.Restart();
                        return turnOutput.ToString();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            Kill();
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException($"Claude Code exceeded the {responseTimeout.TotalSeconds:g} second response timeout.");
            throw new InvalidOperationException($"Claude Code stopped mid-conversation: {RecentErrors()}", exception);
        }
        finally
        {
            Volatile.Write(ref isInTurn, 0);
        }
    }

    /// <summary>True for the turn's <c>result</c> line; its input tokens include the cached and newly cached ones.</summary>
    internal static bool IsResultLine(string outputLine, out int inputTokens)
    {
        inputTokens = 0;
        if (!outputLine.Contains("\"result\"", StringComparison.Ordinal)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(outputLine);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "result") return false;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                foreach (string field in new[] { "input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" })
                    if (usage.TryGetProperty(field, out JsonElement count) && count.TryGetInt32(out int tokens)) inputTokens += tokens;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task PumpOutputAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (line.Length > 0) outputLines.Writer.TryWrite(line);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            outputLines.Writer.TryComplete();
        }
    }

    private async Task PumpErrorsAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (recentErrorOutput)
                {
                    recentErrorOutput.AppendLine(line);
                    if (recentErrorOutput.Length > 4000) recentErrorOutput.Remove(0, recentErrorOutput.Length - 4000);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string RecentErrors()
    {
        lock (recentErrorOutput) return recentErrorOutput.Length > 0 ? recentErrorOutput.ToString().Trim() : "no error output";
    }

    private void Kill()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or AggregateException)
        {
            Trace.TraceWarning($"Could not stop the Claude session: {exception.Message}");
        }
    }

    public void Dispose()
    {
        Kill();
        process.Dispose();
        try { File.Delete(PromptFilePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

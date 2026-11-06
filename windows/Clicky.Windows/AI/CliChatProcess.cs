using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Clicky.Windows.AI;

/// <summary>
/// One CLI child process for one request. <see cref="RunAsync"/> starts it and sends the request at once; a push-to-talk turn
/// can instead <see cref="Start"/> it at the key press (its start-up then overlaps the user's speech) and
/// <see cref="SendAsync"/> the request after the release. Dispose kills it (whole tree) if it is still running.
/// </summary>
internal sealed class CliChatProcess : IDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource readerCancellation = new();
    private readonly Task<string> standardOutput;
    private readonly Task<string> standardError;
    private volatile Action<string>? onStandardOutputLine;
    private int isSent;

    private CliChatProcess(ProcessStartInfo startInfo, bool readsOutputByLine)
    {
        startInfo.FileName = ResolveExecutablePath(startInfo.FileName, Environment.GetEnvironmentVariable("PATH"));
        process = new Process { StartInfo = startInfo };
        process.Start();
        standardOutput = readsOutputByLine
            ? ReadAllLinesAsync(process.StandardOutput, line => onStandardOutputLine?.Invoke(line), readerCancellation.Token)
            : process.StandardOutput.ReadToEndAsync(readerCancellation.Token);
        standardError = process.StandardError.ReadToEndAsync(readerCancellation.Token);
    }

    /// <summary>
    /// Starts the process; it waits for its request on stdin. Output is read line by line from the start, so the callback given
    /// to <see cref="SendAsync"/> sees every line after the request.
    /// </summary>
    public static CliChatProcess Start(ProcessStartInfo startInfo) => new(startInfo, readsOutputByLine: true);

    public bool HasExited => process.HasExited;

    public static async Task<(string StandardOutput, string StandardError, int ExitCode)> RunAsync(
        ProcessStartInfo startInfo, string standardInput, TimeSpan responseTimeout, CancellationToken cancellationToken,
        Action<string>? onStandardOutputLine = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (responseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTimeout), "Response timeout must be positive.");
        using CliChatProcess chatProcess = new(startInfo, readsOutputByLine: onStandardOutputLine is not null);
        return await chatProcess.SendAsync(standardInput, responseTimeout, cancellationToken, onStandardOutputLine).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the request to stdin and waits for the process to exit. The timeout counts from here. Throws
    /// <see cref="TimeoutException"/> on timeout and <see cref="OperationCanceledException"/> on cancel (both kill the process).
    /// One request per process.
    /// </summary>
    public async Task<(string StandardOutput, string StandardError, int ExitCode)> SendAsync(
        string standardInput, TimeSpan responseTimeout, CancellationToken cancellationToken, Action<string>? onStandardOutputLine = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (responseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTimeout), "Response timeout must be positive.");
        if (Interlocked.Exchange(ref isSent, 1) != 0) throw new InvalidOperationException("This chat process already has its request.");
        this.onStandardOutputLine = onStandardOutputLine;
        using var timeout = new CancellationTokenSource(responseTimeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken processCancellationToken = combinedCancellation.Token;

        // Killing also unblocks a full stdin pipe when the child stops reading; cancelling a pipe write alone may not.
        using CancellationTokenRegistration cancellationRegistration = processCancellationToken.Register(() =>
        {
            try { KillIfRunning(process); }
            catch (Exception exception) when (exception is Win32Exception or AggregateException)
            {
                Trace.TraceWarning($"Could not stop chat process: {exception.Message}");
            }
            readerCancellation.Cancel();
        });
        try
        {
            IOException? inputException = null;
            try
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), processCancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(processCancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException exception)
            {
                // An early CLI failure can close stdin before the images finish; keep its exit code and error message.
                inputException = exception;
            }
            await process.WaitForExitAsync(processCancellationToken).ConfigureAwait(false);
            string outputText = await standardOutput.ConfigureAwait(false);
            string errorText = await standardError.ConfigureAwait(false);
            processCancellationToken.ThrowIfCancellationRequested();
            if (inputException is not null && process.ExitCode == 0) throw inputException;
            return (outputText, errorText, process.ExitCode);
        }
        catch (Exception) when (processCancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Chat process exceeded the {responseTimeout.TotalSeconds:g} second response timeout.");
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Kills the process (if running), waits for it, and observes the readers so nothing is left unobserved.</summary>
    private async Task StopAsync()
    {
        try { KillIfRunning(process); }
        catch (Exception exception) when (exception is Win32Exception or AggregateException)
        {
            Trace.TraceWarning($"Could not stop chat process: {exception.Message}");
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        // Observe cancelled readers before disposing their streams; their failure has already been surfaced by SendAsync.
        try { await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false); }
        catch (OperationCanceledException) when (readerCancellation.IsCancellationRequested) { }
    }

    /// <summary>Kills an unused (or finished) process, e.g. one prestarted for a question that was never asked.</summary>
    public void Dispose()
    {
        readerCancellation.Cancel();
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException exception)
        {
            Trace.TraceWarning($"Could not stop chat process cleanly: {exception.InnerException?.Message}");
        }
        process.Dispose();
        readerCancellation.Dispose();
    }

    /// <summary>Reads stdout to the end, handing each line to <paramref name="onLine"/> as soon as it arrives (streamed replies).</summary>
    private static async Task<string> ReadAllLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        var allText = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            allText.Append(line).Append('\n');
            onLine(line);
        }
        return allText.ToString();
    }

    /// <summary>
    /// CreateProcess only appends ".exe" to a bare name, but npm installs CLIs as "codex.cmd" / "claude.cmd" shims, so a bare
    /// name is looked up on PATH as .exe, then .cmd, then .bat. Anything with a directory or extension is used as given.
    /// </summary>
    // A .cmd shim runs through cmd.exe, which re-parses arguments: keep every CLI argument single-line and free of % and ^
    // (prompts go by file or stdin).
    internal static string ResolveExecutablePath(string fileName, string? searchPath)
    {
        if (fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || Path.HasExtension(fileName))
            return fileName;
        foreach (string directory in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (string extension in (string[])[".exe", ".cmd", ".bat"])
            {
                string candidatePath = Path.Combine(directory.Trim('"'), fileName + extension);
                if (File.Exists(candidatePath)) return candidatePath;
            }
        return fileName;
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}

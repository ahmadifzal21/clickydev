using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Clicky.Windows.AI;

internal static class CliChatProcess
{
    public static async Task<(string StandardOutput, string StandardError, int ExitCode)> RunAsync(
        ProcessStartInfo startInfo, string standardInput, TimeSpan responseTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (responseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(responseTimeout), "Response timeout must be positive.");
        using var timeout = new CancellationTokenSource(responseTimeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken processCancellationToken = combinedCancellation.Token;
        startInfo.FileName = ResolveExecutablePath(startInfo.FileName, Environment.GetEnvironmentVariable("PATH"));
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Killing also unblocks a full stdin pipe when the child stops reading; cancelling a pipe write alone may not.
        using CancellationTokenRegistration cancellationRegistration = processCancellationToken.Register(() =>
        {
            try { KillIfRunning(process); }
            catch (Exception exception) when (exception is Win32Exception or AggregateException)
            {
                Trace.TraceWarning($"Could not stop chat process: {exception.Message}");
            }
        });
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(processCancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(processCancellationToken);
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
            KillIfRunning(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Observe cancelled readers before disposing their streams; their failure has already been surfaced above.
            try { await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false); }
            catch (OperationCanceledException) when (processCancellationToken.IsCancellationRequested) { }
        }
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

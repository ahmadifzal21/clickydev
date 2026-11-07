using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// One ephemeral, read-only <c>codex exec</c> run per request, using the user's existing Codex login. Codex only takes images
/// as files, so each request gets its own temp folder (screenshots + last-message file) that is deleted afterwards. A run can be
/// prestarted (<see cref="Prestart"/>): Codex spends about 1.5 s starting up before it reads the prompt from stdin. It reads its
/// --image files only after stdin is complete (checked against codex-cli, E11 T11.22), so a prestarted run reserves the image
/// paths and the request writes the screenshots there just before sending the prompt: a walkthrough step can prestart before
/// its screenshot exists.
/// </summary>
internal sealed class CodexCliChatBackend(CodexCliBackendSettings settings) : ICompanionChatBackend
{
    private readonly PrestartedCliProcessSlot prestartedProcess = new();

    public ChatBackendKind BackendKind => ChatBackendKind.CodexCli;
    public string DisplayName => "Codex CLI";

    /// <summary>What a prestarted run was started with; it suits a request with as many screenshots and the same settings.</summary>
    private sealed record PrestartKey(int ScreenshotCount, string ExecutablePath, string? Model, string? ReasoningEffort)
    {
        public bool Suits(IReadOnlyList<LabeledScreenshotImage> screenshots, CodexCliBackendSettings currentSettings) =>
            ExecutablePath == currentSettings.ExecutablePath && Model == currentSettings.Model &&
            ReasoningEffort == currentSettings.ReasoningEffort && ScreenshotCount == screenshots.Count;
    }

    /// <summary>Screenshots with no bytes yet (a walkthrough step before its capture) only reserve their files.</summary>
    public void Prestart(string systemPrompt, IReadOnlyList<LabeledScreenshotImage> screenshots)
    {
        string requestDirectory = NewRequestDirectory();
        try
        {
            List<string> screenshotFilePaths = WriteScreenshots(requestDirectory, screenshots);
            CliChatProcess chatProcess = CliChatProcess.Start(CodexCliProtocol.BuildStartInfo(
                settings, screenshotFilePaths, LastMessageFilePath(requestDirectory), requestDirectory));
            prestartedProcess.Put(new PrestartedCliProcess(chatProcess,
                new PrestartKey(screenshots.Count, settings.ExecutablePath, settings.Model, settings.ReasoningEffort),
                requestDirectory, () => _ = DeleteRequestDirectoryAsync(requestDirectory)));
        }
        catch (Exception exception)
        {
            // The request will start its own run (and report a missing executable properly).
            Trace.TraceWarning($"Couldn't prestart Codex: {exception.Message}");
            _ = DeleteRequestDirectoryAsync(requestDirectory);
        }
    }

    public void DiscardPrestarted() => prestartedProcess.Discard();

    public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using PrestartedCliProcess? prestarted = prestartedProcess.Take(matchKey =>
            matchKey is PrestartKey prestartKey && prestartKey.Suits(companionChatRequest.LabeledScreenshotImages, settings));
        string requestDirectory = prestarted?.WorkPath ?? NewRequestDirectory();
        // shortcut: a second agent message would be spoken too, though only the last is the answer; tools are off, so Codex sends
        // one. Upgrade if multi-message replies show up in the logs.
        Action<string>? onOutputLine = outputLine =>
        {
            if (companionChatRequest.OnWebContent is { } onWebContent && outputLine.Contains("\"web_search\"", StringComparison.Ordinal)) onWebContent();
            if (onTextDelta is not null && CodexCliProtocol.TryGetAgentMessageText(outputLine) is { Length: > 0 } messageText) onTextDelta(messageText);
        };
        try
        {
            string prompt = CodexCliProtocol.BuildPrompt(companionChatRequest);
            TimeSpan responseTimeout = TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds);
            string lastMessageFilePath = LastMessageFilePath(requestDirectory);
            (string StandardOutput, string StandardError, int ExitCode) result;
            if (prestarted is not null)
            {
                // The run was started with these paths; the screenshots (the same ones, or the step's fresh ones) go there now.
                await Task.Run(() => WriteScreenshots(requestDirectory, companionChatRequest.LabeledScreenshotImages), cancellationToken).ConfigureAwait(false);
                result = await prestarted.Process.SendAsync(prompt, responseTimeout, cancellationToken, onOutputLine).ConfigureAwait(false);
            }
            else
            {
                List<string> screenshotFilePaths = await Task.Run(() => WriteScreenshots(requestDirectory, companionChatRequest.LabeledScreenshotImages),
                    cancellationToken).ConfigureAwait(false);
                result = await CliChatProcess.RunAsync(
                    CodexCliProtocol.BuildStartInfo(settings, screenshotFilePaths, lastMessageFilePath, requestDirectory),
                    prompt, responseTimeout, cancellationToken, onOutputLine).ConfigureAwait(false);
            }
            string? lastMessageText = File.Exists(lastMessageFilePath)
                ? await File.ReadAllTextAsync(lastMessageFilePath, CancellationToken.None).ConfigureAwait(false)
                : null;
            return CodexCliProtocol.ParseResponse(lastMessageText, result.StandardError, result.ExitCode);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new InvalidOperationException(
                $"Codex was not found at '{settings.ExecutablePath}'. Install Codex or set codexCli.executablePath in clicky.config.json to its executable.", exception);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Codex did not respond within {settings.ResponseTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            if (prestarted is null) await DeleteRequestDirectoryAsync(requestDirectory).ConfigureAwait(false); // A prestarted run cleans up on dispose.
        }
    }

    private static string NewRequestDirectory()
    {
        string requestDirectory = Path.Combine(Path.GetTempPath(), "clicky-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(requestDirectory);
        return requestDirectory;
    }

    private static string LastMessageFilePath(string requestDirectory) => Path.Combine(requestDirectory, "last-message.txt");

    private static List<string> WriteScreenshots(string requestDirectory, IReadOnlyList<LabeledScreenshotImage> screenshots)
    {
        var screenshotFilePaths = new List<string>();
        for (int index = 0; index < screenshots.Count; index++)
        {
            string screenshotFilePath = Path.Combine(requestDirectory, $"screen-{index + 1}.jpg");
            if (screenshots[index].JpegImageBytes.Length > 0) File.WriteAllBytes(screenshotFilePath, screenshots[index].JpegImageBytes);
            screenshotFilePaths.Add(screenshotFilePath);
        }
        return screenshotFilePaths;
    }

    /// <summary>
    /// CliChatProcess waits for Codex itself, but after a cancel the rest of the killed tree can hold this folder (its working
    /// directory) for a moment longer, so retry briefly.
    /// </summary>
    private static async Task DeleteRequestDirectoryAsync(string requestDirectory)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { Directory.Delete(requestDirectory, recursive: true); break; }
            catch (DirectoryNotFoundException) { break; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < 10) { await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); continue; }
                Trace.TraceWarning($"Could not delete Codex temp folder '{requestDirectory}': {exception.Message}");
                break;
            }
        }
    }
}

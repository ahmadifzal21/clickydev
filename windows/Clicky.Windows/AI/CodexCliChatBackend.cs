using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.AI;

/// <summary>
/// One ephemeral, read-only <c>codex exec</c> run per request, using the user's existing Codex login. Codex only takes images
/// as files, so each request gets its own temp folder (screenshots + last-message file) that is deleted afterwards.
/// </summary>
internal sealed class CodexCliChatBackend(CodexCliBackendSettings settings) : ICompanionChatBackend
{
    public ChatBackendKind BackendKind => ChatBackendKind.CodexCli;
    public string DisplayName => "Codex CLI";

    public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken,
        Action<string>? onTextDelta = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string requestDirectory = Path.Combine(Path.GetTempPath(), "clicky-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(requestDirectory);
        try
        {
            var screenshotFilePaths = new List<string>();
            for (int index = 0; index < companionChatRequest.LabeledScreenshotImages.Count; index++)
            {
                string screenshotFilePath = Path.Combine(requestDirectory, $"screen-{index + 1}.jpg");
                await File.WriteAllBytesAsync(screenshotFilePath, companionChatRequest.LabeledScreenshotImages[index].JpegImageBytes, cancellationToken)
                    .ConfigureAwait(false);
                screenshotFilePaths.Add(screenshotFilePath);
            }
            string lastMessageFilePath = Path.Combine(requestDirectory, "last-message.txt");

            var result = await CliChatProcess.RunAsync(
                CodexCliProtocol.BuildStartInfo(settings, screenshotFilePaths, lastMessageFilePath, requestDirectory),
                CodexCliProtocol.BuildPrompt(companionChatRequest),
                TimeSpan.FromSeconds(settings.ResponseTimeoutSeconds), cancellationToken,
                // shortcut: a second agent message would be spoken too, though only the last is the answer; tools are off, so
                // Codex sends one. Upgrade if multi-message replies show up in the logs.
                onTextDelta is null ? null : outputLine =>
                {
                    if (CodexCliProtocol.TryGetAgentMessageText(outputLine) is { Length: > 0 } messageText) onTextDelta(messageText);
                }).ConfigureAwait(false);
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
            // CliChatProcess waits for Codex itself, but after a cancel the rest of the killed tree can hold this folder
            // (its working directory) for a moment longer, so retry briefly.
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
}

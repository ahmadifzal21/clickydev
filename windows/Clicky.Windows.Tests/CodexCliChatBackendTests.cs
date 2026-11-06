using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public class CodexCliChatBackendTests
{
    [Fact]
    public async Task MissingExecutable_ExplainsInstallationAndDeletesTempScreenshots()
    {
        string[] temporaryFoldersBefore = Directory.GetDirectories(Path.GetTempPath(), "clicky-codex-*");
        var backend = new CodexCliChatBackend(new CodexCliBackendSettings
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "codex.exe")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.GetResponseTextAsync(new("system", [new([0xFF, 0xD8], "screen 1 of 1")], [], "hello"), CancellationToken.None));

        Assert.Equal(ChatBackendKind.CodexCli, backend.BackendKind);
        Assert.Equal("Codex CLI", backend.DisplayName);
        Assert.Contains("Install Codex", exception.Message);
        Assert.Contains("executablePath", exception.Message);
        Assert.Equal(temporaryFoldersBefore, Directory.GetDirectories(Path.GetTempPath(), "clicky-codex-*"));
    }

    [Fact]
    public async Task CancelledBeforeStart_ThrowsWithoutCreatingTempFolder()
    {
        var backend = new CodexCliChatBackend(new CodexCliBackendSettings { ExecutablePath = "does-not-exist.exe" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.GetResponseTextAsync(new("system", [], [], "hello"), new CancellationToken(canceled: true)));
    }
}

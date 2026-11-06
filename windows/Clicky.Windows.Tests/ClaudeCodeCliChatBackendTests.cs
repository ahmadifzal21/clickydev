using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public class ClaudeCodeCliChatBackendTests
{
    [Fact]
    public async Task MissingExecutable_ExplainsInstallationAndConfig()
    {
        var backend = new ClaudeCodeCliChatBackend(new ClaudeCodeCliBackendSettings
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "claude.exe")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.GetResponseTextAsync(new("system", [], [], "hello"), CancellationToken.None));

        Assert.Equal(ChatBackendKind.ClaudeCodeCli, backend.BackendKind);
        Assert.Equal("Claude Code CLI", backend.DisplayName);
        Assert.Contains("Install Claude Code", exception.Message);
        Assert.Contains("executablePath", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidTimeout_IsRejectedBeforeStarting(int responseTimeoutSeconds)
    {
        var backend = new ClaudeCodeCliChatBackend(new ClaudeCodeCliBackendSettings
        {
            ExecutablePath = "does-not-exist.exe", ResponseTimeoutSeconds = responseTimeoutSeconds
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            backend.GetResponseTextAsync(new("system", [], [], "hello"), CancellationToken.None));
    }
}

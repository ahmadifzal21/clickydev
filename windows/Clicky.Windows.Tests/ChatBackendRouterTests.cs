using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public class ChatBackendRouterTests
{
    private static readonly CompanionChatRequest SampleRequest = new("system", [], [], "hello");

    [Theory]
    [InlineData(CompanionJobKind.Answer, ChatBackendKind.CodexCli)]
    [InlineData(CompanionJobKind.Pointing, ChatBackendKind.CloudflareWorker)]
    public async Task RoutesEachJobToItsConfiguredBackend(CompanionJobKind job, ChatBackendKind expectedBackend)
    {
        var backends = AllBackends();
        var router = new ChatBackendRouter(() => new JobBackendRouting
        {
            Answer = ChatBackendKind.CodexCli, Pointing = ChatBackendKind.CloudflareWorker, Fallback = ChatBackendKind.ClaudeCodeCli
        }, backends);

        RoutedChatResponse response = await router.GetResponseAsync(job, SampleRequest, CancellationToken.None);

        Assert.Equal(expectedBackend, response.AnsweringBackendKind);
        Assert.Equal($"reply from {expectedBackend}", response.ResponseText);
        Assert.Equal([expectedBackend], backends.Where(backend => backend.CallCount > 0).Select(backend => backend.BackendKind));
    }

    [Fact]
    public async Task RoutingIsReadPerRequest()
    {
        var routing = new JobBackendRouting { Answer = ChatBackendKind.ClaudeCodeCli };
        var router = new ChatBackendRouter(() => routing, AllBackends());
        Assert.Equal(ChatBackendKind.ClaudeCodeCli, (await router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, default)).AnsweringBackendKind);

        routing.Answer = ChatBackendKind.CodexCli;

        Assert.Equal(ChatBackendKind.CodexCli, (await router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, default)).AnsweringBackendKind);
    }

    [Fact]
    public async Task FailedBackendFallsBackExactlyOnce()
    {
        var backends = AllBackends(failing: ChatBackendKind.ClaudeCodeCli);
        var router = new ChatBackendRouter(() => new JobBackendRouting { Fallback = ChatBackendKind.CodexCli }, backends);

        RoutedChatResponse response = await router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, CancellationToken.None);

        Assert.Equal(ChatBackendKind.CodexCli, response.AnsweringBackendKind);
        Assert.Equal([1, 1, 0], backends.Select(backend => backend.CallCount));
    }

    [Fact]
    public async Task BothFailing_ReportsBothErrorsWithoutAThirdTry()
    {
        var backends = AllBackends(failing: [ChatBackendKind.ClaudeCodeCli, ChatBackendKind.CodexCli]);
        var router = new ChatBackendRouter(() => new JobBackendRouting { Fallback = ChatBackendKind.CodexCli }, backends);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, CancellationToken.None));

        Assert.Equal("ClaudeCodeCli broke. Fallback CodexCli display also failed: CodexCli broke.", exception.Message);
        Assert.Equal([1, 1, 0], backends.Select(backend => backend.CallCount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ChatBackendKind.ClaudeCodeCli)]
    public async Task NoFallbackOrSameBackend_RethrowsOriginalError(ChatBackendKind? fallback)
    {
        var backends = AllBackends(failing: ChatBackendKind.ClaudeCodeCli);
        var router = new ChatBackendRouter(() => new JobBackendRouting { Fallback = fallback }, backends);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, CancellationToken.None));

        Assert.Equal("ClaudeCodeCli broke.", exception.Message);
        Assert.Equal([1, 0, 0], backends.Select(backend => backend.CallCount));
    }

    [Fact]
    public async Task CallerCancellation_DoesNotTriggerFallback()
    {
        using var cancellationSource = new CancellationTokenSource();
        var backends = AllBackends();
        backends[0].OnCall = () => { cancellationSource.Cancel(); throw new OperationCanceledException(cancellationSource.Token); };
        var router = new ChatBackendRouter(() => new JobBackendRouting { Fallback = ChatBackendKind.CodexCli }, backends);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, cancellationSource.Token));
        Assert.Equal(0, backends[1].CallCount);
    }

    [Fact]
    public async Task MissingBackend_IsAnErrorThatCanFallBack()
    {
        var codex = new FakeChatBackend(ChatBackendKind.CodexCli);
        var router = new ChatBackendRouter(() => new JobBackendRouting { Fallback = ChatBackendKind.CodexCli }, [codex]);

        Assert.Equal(ChatBackendKind.CodexCli, (await router.GetResponseAsync(CompanionJobKind.Answer, SampleRequest, default)).AnsweringBackendKind);
    }

    private static FakeChatBackend[] AllBackends(params ChatBackendKind[] failing) =>
        Enum.GetValues<ChatBackendKind>().Select(kind => new FakeChatBackend(kind)
        {
            OnCall = failing.Contains(kind) ? () => throw new TimeoutException($"{kind} broke.") : null
        }).ToArray();

    private sealed class FakeChatBackend(ChatBackendKind backendKind) : ICompanionChatBackend
    {
        public int CallCount { get; private set; }
        public Action? OnCall { get; set; }
        public ChatBackendKind BackendKind => backendKind;
        public string DisplayName => $"{backendKind} display";

        public Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken)
        {
            CallCount++;
            OnCall?.Invoke();
            return Task.FromResult($"reply from {backendKind}");
        }
    }
}

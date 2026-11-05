using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public class CloudflareWorkerChatBackendTests
{
    private static readonly CompanionChatRequest SampleRequest = new(
        "You are Clicky.",
        [new([0xFF, 0xD8, 0x01], "screen 1 of 1 — cursor is on this screen")],
        [new("what is this?", "a settings window")],
        "where is save?");

    private const string SampleEventStream =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\"}}\n\n" +
        "event: ping\ndata: {\"type\": \"ping\"}\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Click \"}}\n\n" +
        "data: not json\n\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"save [POINT:10,20:save]\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildChatUri_EmptyBaseUrl_SaysNotSetUp(string workerBaseUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CloudflareWorkerProtocol.BuildChatUri(new CloudflareWorkerBackendSettings { WorkerBaseUrl = workerBaseUrl }));
        Assert.Contains("not set up", exception.Message);
        Assert.Contains("workerBaseUrl", exception.Message);
    }

    [Theory]
    [InlineData("https://clicky.example.workers.dev", "https://clicky.example.workers.dev/chat")]
    [InlineData(" https://clicky.example.workers.dev/ ", "https://clicky.example.workers.dev/chat")]
    public void BuildChatUri_AppendsChatRoute(string workerBaseUrl, string expectedUri) =>
        Assert.Equal(expectedUri, CloudflareWorkerProtocol.BuildChatUri(new() { WorkerBaseUrl = workerBaseUrl }).ToString());

    [Theory]
    [InlineData("clicky.example.workers.dev")]
    [InlineData("ftp://clicky.example")]
    public void BuildChatUri_RejectsNonHttpUrls(string workerBaseUrl) =>
        Assert.Throws<InvalidOperationException>(() => CloudflareWorkerProtocol.BuildChatUri(new() { WorkerBaseUrl = workerBaseUrl }));

    [Fact]
    public void BuildRequestJson_MatchesMacMessagesShape()
    {
        using JsonDocument body = JsonDocument.Parse(CloudflareWorkerProtocol.BuildRequestJson(SampleRequest, "claude-test"));
        JsonElement root = body.RootElement;

        Assert.Equal("claude-test", root.GetProperty("model").GetString());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("You are Clicky.", root.GetProperty("system").GetString());

        JsonElement[] messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("what is this?", messages[0].GetProperty("content").GetString());
        Assert.Equal("a settings window", messages[1].GetProperty("content").GetString());

        JsonElement[] blocks = messages[2].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(["image", "text", "text"], blocks.Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("image/jpeg", blocks[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String([0xFF, 0xD8, 0x01]), blocks[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("screen 1 of 1 — cursor is on this screen", blocks[1].GetProperty("text").GetString());
        Assert.Equal("where is save?", blocks[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadResponseText_JoinsTextDeltasAndSkipsOtherEvents() =>
        Assert.Equal("Click save [POINT:10,20:save]",
            await CloudflareWorkerProtocol.ReadResponseTextAsync(new StringReader(SampleEventStream), CancellationToken.None));

    [Fact]
    public async Task ReadResponseText_ErrorEventThrowsWithApiMessage()
    {
        const string stream =
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Half\"}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CloudflareWorkerProtocol.ReadResponseTextAsync(new StringReader(stream), CancellationToken.None));
        Assert.Contains("overloaded_error: Overloaded", exception.Message);
    }

    [Fact]
    public async Task ReadResponseText_NoTextThrowsEmptyAnswer()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CloudflareWorkerProtocol.ReadResponseTextAsync(new StringReader("data: {\"type\":\"message_stop\"}\n\n"), CancellationToken.None));
        Assert.Contains("empty answer", exception.Message);
    }

    [Fact]
    public async Task Backend_PostsToChatAndReturnsStreamedText()
    {
        HttpRequestMessage? sentRequest = null;
        string? sentBody = null;
        var backend = CreateBackend(async request =>
        {
            sentRequest = request;
            sentBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleEventStream, Encoding.UTF8, "text/event-stream") };
        });

        string responseText = await backend.GetResponseTextAsync(SampleRequest, CancellationToken.None);

        Assert.Equal("Click save [POINT:10,20:save]", responseText);
        Assert.Equal(HttpMethod.Post, sentRequest!.Method);
        Assert.Equal("https://worker.test/chat", sentRequest.RequestUri!.ToString());
        Assert.Equal("application/json", sentRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("\"stream\":true", sentBody);
        Assert.Equal(ChatBackendKind.CloudflareWorker, backend.BackendKind);
    }

    [Fact]
    public async Task Backend_HttpErrorIncludesStatusAndAnthropicMessage()
    {
        var backend = CreateBackend(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}")
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetResponseTextAsync(SampleRequest, CancellationToken.None));
        Assert.Contains("HTTP 401", exception.Message);
        Assert.Contains("ANTHROPIC_API_KEY", exception.Message);
        Assert.Contains("authentication_error: invalid x-api-key", exception.Message);
    }

    [Fact]
    public async Task Backend_UnreachableWorkerIsAClearError()
    {
        var backend = CreateBackend(_ => throw new HttpRequestException("No such host is known."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GetResponseTextAsync(SampleRequest, CancellationToken.None));
        Assert.Contains("Could not reach the Cloudflare Worker at https://worker.test", exception.Message);
    }

    [Fact]
    public async Task Backend_SlowWorkerTimesOut()
    {
        var backend = CreateBackend(_ => throw new UnreachableException(), responseTimeoutSeconds: 1, honourCancellation: true);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => backend.GetResponseTextAsync(SampleRequest, CancellationToken.None));
        Assert.Contains("1 seconds", exception.Message);
    }

    [Fact]
    public async Task Backend_CallerCancellationIsNotReportedAsTimeout()
    {
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var backend = CreateBackend(_ => throw new UnreachableException(), honourCancellation: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.GetResponseTextAsync(SampleRequest, cancellationSource.Token));
    }

    private static CloudflareWorkerChatBackend CreateBackend(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, int responseTimeoutSeconds = 30, bool honourCancellation = false) =>
        new(new CloudflareWorkerBackendSettings { WorkerBaseUrl = "https://worker.test", ResponseTimeoutSeconds = responseTimeoutSeconds },
            new HttpClient(new FakeHttpMessageHandler(respond, honourCancellation)));

    /// <summary>Stands in for the network. With honourCancellation it waits for the request token, like a hung server.</summary>
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, bool honourCancellation)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (honourCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await respond(request);
        }
    }
}

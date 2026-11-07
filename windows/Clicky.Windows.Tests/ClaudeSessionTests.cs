using System.IO;
using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

/// <summary>
/// E11 T11.18/T11.20: the kept-alive Claude session, against a fake <c>claude</c> that answers each stream-json turn with its
/// process id and how many questions and replayed history messages it has seen.
/// </summary>
public sealed class ClaudeSessionTests : IDisposable
{
    private const string FakeClaudeScript = """
        $queries = 0; $replayed = 0
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            if ($line -notmatch '"type":"user"') { continue }
            if ($line -match '"shouldQuery":false') { $replayed++; continue }
            $queries++
            $text = "pid $PID query $queries replayed $replayed"
            if ($line -match 'please hang') { Start-Sleep -Seconds 30 }
            [Console]::Out.WriteLine('{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"' + $text + '"}}}')
            [Console]::Out.WriteLine('{"type":"result","subtype":"success","is_error":false,"result":"' + $text + '","usage":{"input_tokens":5,"cache_read_input_tokens":1000,"cache_creation_input_tokens":0}}')
            [Console]::Out.Flush()
        }
        """;

    private readonly string fakeFolder = Path.Combine(Path.GetTempPath(), "clicky-fake-claude-" + Guid.NewGuid().ToString("N"));

    public ClaudeSessionTests()
    {
        Directory.CreateDirectory(fakeFolder);
        File.WriteAllText(Path.Combine(fakeFolder, "fake-claude.ps1"), FakeClaudeScript);
        File.WriteAllText(Path.Combine(fakeFolder, "claude.cmd"),
            "@powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-claude.ps1\" %*\r\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(fakeFolder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ClaudeCodeCliChatBackend Backend(bool keepSessionAlive = true) => new(new ClaudeCodeCliBackendSettings
    {
        ExecutablePath = Path.Combine(fakeFolder, "claude.cmd"), Model = null, Effort = null, KeepSessionAlive = keepSessionAlive,
        ResponseTimeoutSeconds = 20
    });

    private static CompanionChatRequest Request(long historyVersion, int historyCount, string question = "q") =>
        new("system", [], Enumerable.Range(1, historyCount).Select(index => new ConversationExchange($"u{index}", $"a{index}")).ToArray(), question)
        {
            HistoryVersion = historyVersion
        };

    private static string Ask(ClaudeCodeCliChatBackend backend, CompanionChatRequest request, CancellationToken cancellationToken = default) =>
        backend.GetResponseTextAsync(request, cancellationToken).GetAwaiter().GetResult();

    private static string Pid(string reply) => reply.Split(' ')[1];

    [Fact]
    public void FollowUpTurns_GoToTheSameProcess_WithoutReplayingHistory()
    {
        ClaudeCodeCliChatBackend backend = Backend();
        try
        {
            string first = Ask(backend, Request(0, 0));
            string second = Ask(backend, Request(1, 1));
            string third = Ask(backend, Request(2, 2));

            Assert.EndsWith("query 1 replayed 0", first);
            Assert.EndsWith("query 2 replayed 0", second);
            Assert.EndsWith("query 3 replayed 0", third);
            Assert.Equal(Pid(first), Pid(third));
        }
        finally { backend.Shutdown(); }
    }

    [Fact]
    public void AConversationTheSessionDoesntHold_StartsAFreshOne_ThatGetsTheHistory()
    {
        ClaudeCodeCliChatBackend backend = Backend();
        try
        {
            string first = Ask(backend, Request(0, 0));
            string other = Ask(backend, Request(7, 3)); // e.g. the last turn was cancelled, so the session never saw its exchange.

            Assert.NotEqual(Pid(first), Pid(other));
            Assert.EndsWith("query 1 replayed 3", other);
        }
        finally { backend.Shutdown(); }
    }

    [Fact]
    public void APrestartedSession_TakesTheFirstTurnWithItsHistory()
    {
        ClaudeCodeCliChatBackend backend = Backend();
        try
        {
            backend.Prestart("system", []);
            Thread.Sleep(500);
            string reply = Ask(backend, Request(4, 2));

            Assert.EndsWith("query 1 replayed 2", reply);
            Assert.EndsWith("query 2 replayed 2", Ask(backend, Request(5, 3)));
        }
        finally { backend.Shutdown(); }
    }

    [Fact]
    public void ACancelledTurn_KillsTheSession_AndTheNextTurnStartsOver()
    {
        ClaudeCodeCliChatBackend backend = Backend();
        try
        {
            string first = Ask(backend, Request(0, 0));
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
            Assert.ThrowsAny<OperationCanceledException>(() => Ask(backend, Request(1, 1, "please hang"), cancellation.Token));

            string after = Ask(backend, Request(1, 1));
            Assert.NotEqual(Pid(first), Pid(after));
            Assert.EndsWith("query 1 replayed 1", after);
        }
        finally { backend.Shutdown(); }
    }

    [Fact]
    public void WithoutKeepSessionAlive_EachTurnIsItsOwnProcess()
    {
        ClaudeCodeCliChatBackend backend = Backend(keepSessionAlive: false);
        string first = Ask(backend, Request(0, 0));
        string second = Ask(backend, Request(1, 1));

        Assert.NotEqual(Pid(first), Pid(second));
        Assert.EndsWith("query 1 replayed 1", second);
    }

    [Fact]
    public void ResultLine_CountsCachedInputTokensToo()
    {
        Assert.True(ClaudeSessionProcess.IsResultLine(
            """{"type":"result","subtype":"success","usage":{"input_tokens":4,"cache_read_input_tokens":60000,"cache_creation_input_tokens":12}}""",
            out int inputTokens));
        Assert.Equal(60016, inputTokens);
        Assert.False(ClaudeSessionProcess.IsResultLine("""{"type":"assistant","result":"x"}""", out _));
    }
}

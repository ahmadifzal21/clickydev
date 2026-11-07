using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public class CodexCliProtocolTests
{
    [Fact]
    public void AgentMessage_IsReadFromItsCompletedEvent_AndOtherEventsGiveNothing()
    {
        Assert.Equal("The sky is blue.", CodexCliProtocol.TryGetAgentMessageText(
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"The sky is blue."}}"""));
        Assert.Null(CodexCliProtocol.TryGetAgentMessageText("""{"type":"item.started","item":{"id":"item_0","type":"agent_message","text":""}}"""));
        Assert.Null(CodexCliProtocol.TryGetAgentMessageText("""{"type":"item.completed","item":{"id":"item_1","type":"reasoning","text":"agent_message"}}"""));
        Assert.Null(CodexCliProtocol.TryGetAgentMessageText("""{"type":"turn.completed","usage":{}}"""));
    }

    [Fact]
    public void BuildStartInfo_IsReadOnlyEphemeralWithOneImageFlagPerScreenshotAndPromptOnStdin()
    {
        var startInfo = CodexCliProtocol.BuildStartInfo(
            new CodexCliBackendSettings { ExecutablePath = "codex", Model = "gpt-test", ReasoningEffort = "low", WebSearch = false },
            [@"C:\t\screen-1.jpg", @"C:\t\screen 2.jpg"], @"C:\t\last-message.txt", @"C:\t");

        Assert.Equal(
            [
                "exec", "--ignore-user-config", "--skip-git-repo-check", "--sandbox", "read-only", "--ephemeral", "--color", "never", "--json",
                "--output-last-message", @"C:\t\last-message.txt", "--model", "gpt-test", "-c", "model_reasoning_effort=\"low\"",
                "--image", @"C:\t\screen-1.jpg", "--image", @"C:\t\screen 2.jpg", "--", "-"
            ],
            startInfo.ArgumentList);
        Assert.Equal(@"C:\t", startInfo.WorkingDirectory);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void BuildStartInfo_WithoutModelOrImages_OmitsThoseFlags()
    {
        var startInfo = CodexCliProtocol.BuildStartInfo(new CodexCliBackendSettings { ReasoningEffort = null, WebSearch = false }, [], "out.txt", ".");

        Assert.DoesNotContain("--model", startInfo.ArgumentList);
        Assert.DoesNotContain("-c", startInfo.ArgumentList);
        Assert.DoesNotContain("--image", startInfo.ArgumentList);
        Assert.Equal(["--", "-"], startInfo.ArgumentList.TakeLast(2));
    }

    [Fact]
    public void BuildPrompt_FoldsSystemPromptHistoryAndImageLabelsInOrder()
    {
        string prompt = CodexCliProtocol.BuildPrompt(new CompanionChatRequest(
            "You are Clicky.",
            [new([1], "screen 1 of 2 — cursor is on this screen"), new([2], "screen 2 of 2")],
            [new("what is this?", "a settings window")],
            "where is save?"));

        int system = prompt.IndexOf("You are Clicky.", StringComparison.Ordinal);
        int history = prompt.IndexOf("User: what is this?\r\nAssistant: a settings window", StringComparison.Ordinal);
        int firstImage = prompt.IndexOf("Image 1: screen 1 of 2 — cursor is on this screen", StringComparison.Ordinal);
        int secondImage = prompt.IndexOf("Image 2: screen 2 of 2", StringComparison.Ordinal);
        int question = prompt.IndexOf("User: where is save?", StringComparison.Ordinal);
        Assert.True(system == 0 && system < history && history < firstImage && firstImage < secondImage && secondImage < question, prompt);
    }

    [Fact]
    public void BuildPrompt_WithoutHistoryOrImages_HasNoEmptySections()
    {
        string prompt = CodexCliProtocol.BuildPrompt(new CompanionChatRequest("system", [], [], "hi"));

        Assert.DoesNotContain("Conversation so far", prompt);
        Assert.DoesNotContain("Attached images", prompt);
        Assert.EndsWith("User: hi\r\n", prompt);
    }

    [Fact]
    public void ParseResponse_Success_ReturnsTrimmedLastMessage() =>
        Assert.Equal("Click Save. [POINT:10,20:save]", CodexCliProtocol.ParseResponse("\nClick Save. [POINT:10,20:save]\n", "noise", 0));

    [Theory]
    [InlineData("Error: Not logged in. Run codex login")]
    [InlineData("unexpected status 401 Unauthorized")]
    public void ParseResponse_AuthenticationFailure_ExplainsCodexLogin(string standardError)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CodexCliProtocol.ParseResponse(null, standardError, 1));

        Assert.Contains("codex login", exception.Message);
    }

    [Fact]
    public void ParseResponse_FailureOrEmptyAnswer_IncludesExitCodeAndStderrTail()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            CodexCliProtocol.ParseResponse("partial", new string('x', 3000) + "model not found", 2));
        var empty = Assert.Throws<InvalidOperationException>(() => CodexCliProtocol.ParseResponse("  ", "", 0));

        Assert.Contains("exited with code 2", failure.Message);
        Assert.EndsWith("model not found", failure.Message);
        Assert.True(failure.Message.Length < 1600);
        Assert.Equal("Codex returned an empty answer.", empty.Message);
    }

    [Fact]
    public void ParseResponse_ErrorLines_ReplaceSurroundingNoiseAndAreDeduplicated()
    {
        const string standardError =
            "warning: `content_hash` is ignored.\r\nhook: UserPromptSubmit\r\n" +
            "ERROR: You've hit your usage limit. Try again at 5:54 PM.\r\nERROR: You've hit your usage limit. Try again at 5:54 PM.\r\n";

        var exception = Assert.Throws<InvalidOperationException>(() => CodexCliProtocol.ParseResponse(null, standardError, 1));

        Assert.Equal("Codex exited with code 1. ERROR: You've hit your usage limit. Try again at 5:54 PM.", exception.Message);
    }
}

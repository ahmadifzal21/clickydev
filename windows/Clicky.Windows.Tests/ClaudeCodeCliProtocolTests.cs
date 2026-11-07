using System.Text.Json;
using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public class ClaudeCodeCliProtocolTests
{
    [Fact]
    public void TextDelta_IsReadFromPartialMessageLines_AndOtherLinesGiveNothing()
    {
        Assert.Equal("The sky gl", ClaudeCodeCliProtocol.TryGetTextDelta(
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"The sky gl"}},"session_id":"s"}"""));
        Assert.Null(ClaudeCodeCliProtocol.TryGetTextDelta(
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}}"""));
        Assert.Null(ClaudeCodeCliProtocol.TryGetTextDelta("""{"type":"result","subtype":"success","result":"text_delta"}"""));
        Assert.Null(ClaudeCodeCliProtocol.TryGetTextDelta("not json but mentions \"text_delta\""));
    }

    [Fact]
    public void Arguments_PassPromptByFileAndModelLiteral_AndDisableActionsAndPersistence()
    {
        const string systemPromptFilePath = @"C:\Temp Dir\clicky-claude-1.txt";
        var settings = new ClaudeCodeCliBackendSettings { ExecutablePath = @"C:\Claude Code\claude.exe", Model = "opus", Effort = "low", WebTools = false };

        var startInfo = ClaudeCodeCliProtocol.BuildStartInfo(settings, systemPromptFilePath);

        Assert.Equal(settings.ExecutablePath, startInfo.FileName);
        Assert.Equal(new[]
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--system-prompt-file", systemPromptFilePath, "--tools", "", "--safe-mode", "--strict-mcp-config",
            "--disable-slash-commands", "--no-session-persistence", "--model", "opus", "--effort", "low"
        }, startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UnspecifiedModel_UsesCliDefault(string? model)
    {
        var startInfo = ClaudeCodeCliProtocol.BuildStartInfo(new ClaudeCodeCliBackendSettings { Model = model, Effort = model }, "prompt");

        Assert.DoesNotContain("--model", startInfo.ArgumentList);
        Assert.DoesNotContain("--effort", startInfo.ArgumentList);
    }

    [Fact]
    public void Input_ReplaysPriorTurnsWithoutQuerying_ThenSendsLabeledImagesAndCurrentQuestion()
    {
        var request = new CompanionChatRequest("system", [new([1, 2, 3], "screen 1"), new([4, 5], "screen 2")],
            [new("previous\nquestion", "previous answer"), new("follow up", "follow up answer")], "Where is café?\n\"here\"");

        string input = ClaudeCodeCliProtocol.SerializeInput(request);
        string[] lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.EndsWith("\n", input);
        Assert.Equal(5, lines.Length);
        using JsonDocument previousUser = JsonDocument.Parse(lines[0]);
        Assert.Equal("user", previousUser.RootElement.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("previous\nquestion", previousUser.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.False(previousUser.RootElement.GetProperty("shouldQuery").GetBoolean());
        Assert.True(previousUser.RootElement.GetProperty("client_composed").GetBoolean());
        using JsonDocument previousAssistant = JsonDocument.Parse(lines[1]);
        Assert.Equal("assistant", previousAssistant.RootElement.GetProperty("type").GetString());
        Assert.Equal("assistant", previousAssistant.RootElement.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("previous answer", previousAssistant.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(Guid.TryParse(previousAssistant.RootElement.GetProperty("uuid").GetString(), out _));
        using JsonDocument nextUser = JsonDocument.Parse(lines[2]);
        Assert.Equal("follow up", nextUser.RootElement.GetProperty("message").GetProperty("content").GetString());
        Assert.False(nextUser.RootElement.GetProperty("shouldQuery").GetBoolean());
        using JsonDocument currentUser = JsonDocument.Parse(lines[4]);
        Assert.True(currentUser.RootElement.GetProperty("shouldQuery").GetBoolean());
        Assert.True(currentUser.RootElement.GetProperty("client_composed").GetBoolean());
        JsonElement content = currentUser.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(5, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("base64", content[0].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, content[0].GetProperty("source").GetProperty("data").GetBytesFromBase64());
        Assert.Equal("screen 1", content[1].GetProperty("text").GetString());
        Assert.Equal(new byte[] { 4, 5 }, content[2].GetProperty("source").GetProperty("data").GetBytesFromBase64());
        Assert.Equal("screen 2", content[3].GetProperty("text").GetString());
        Assert.Equal(request.UserPrompt, content[4].GetProperty("text").GetString());
    }

    [Fact]
    public void Input_WithoutHistoryOrScreenshots_IsOneUserMessage()
    {
        using JsonDocument input = JsonDocument.Parse(ClaudeCodeCliProtocol.SerializeInput(new("system", [], [], "hello")));

        Assert.Equal("hello", input.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Output_UsesFinalResult_NotAssistantOrPartialEvents_AndKeepsPointTag()
    {
        const string output = """
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"partial"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"older result"}
            {"type":"result","subtype":"success","is_error":false,"result":"Click here. [POINT:40,50:button:screen2]"}
            """;

        Assert.Equal("Click here. [POINT:40,50:button:screen2]", ClaudeCodeCliProtocol.ParseResponse(output, "diagnostic", 0));
    }

    [Theory]
    [InlineData("", "final result")]
    [InlineData("not json", "JSON")]
    [InlineData("[]", "JSON")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"  \"}", "empty")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false}", "empty")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"errors\":[\"quota exhausted\"]}", "quota exhausted")]
    [InlineData("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"request failed\"}", "request failed")]
    public void UnsuccessfulOrMalformedOutput_Throws(string output, string expectedMessage)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ClaudeCodeCliProtocol.ParseResponse(output, "", 0));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Not logged in · Please run /login")]
    [InlineData("authentication_failed")]
    [InlineData("OAuth token has expired")]
    public void AuthenticationFailure_ExplainsHowToLogIn(string error)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ClaudeCodeCliProtocol.ParseResponse("", error, 1));

        Assert.Contains("claude auth login", exception.Message);
    }

    [Fact]
    public void AuthenticationFailureInResult_IsNotReturnedAsAnAnswer()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ClaudeCodeCliProtocol.ParseResponse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":\"Not logged in · Please run /login\"}", "", 0));

        Assert.Contains("claude auth login", exception.Message);
    }

    [Fact]
    public void NonzeroExit_IsNeverAcceptedAsSuccess()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ClaudeCodeCliProtocol.ParseResponse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"answer\"}", "bad flag", 2));

        Assert.Contains("bad flag", exception.Message);
        Assert.Contains("2", exception.Message);
    }
}

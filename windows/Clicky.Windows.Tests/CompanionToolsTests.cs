using System.IO;
using Clicky.Windows.AI;
using Clicky.Windows.Automation;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.24-T11.26: memory, the actions a reply can take, and the web tools each CLI gets.</summary>
public sealed class CompanionToolsTests : IDisposable
{
    private readonly string memoryFilePath = Path.Combine(Path.GetTempPath(), $"clicky-memory-{Guid.NewGuid():N}", "memory.md");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(memoryFilePath)!, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Memory_RemembersOnceEach_ForgetsByWords_AndFeedsTheContext()
    {
        CompanionMemory memory = new(memoryFilePath);
        Assert.Null(memory.ContextLine());

        memory.Remember("name is Sam");
        memory.Remember("Name is Sam"); // Same fact.
        memory.Remember("mostly works in Visual Studio\nand Word");
        memory.Remember("has a 3D printer");
        memory.Forget("visual studio");

        Assert.Equal(["name is Sam", "has a 3D printer"], memory.Facts);
        Assert.Equal("[memory] name is Sam; has a 3D printer", memory.ContextLine());
        Assert.StartsWith("# What Clicky remembers", File.ReadAllLines(memoryFilePath)[0]);
    }

    [Fact]
    public void Memory_KeepsTheNewestFacts_WhenTheContextCapIsReached()
    {
        CompanionMemory memory = new(memoryFilePath);
        for (int index = 0; index < 60; index++) memory.Remember($"fact {index} " + new string('x', 250));

        string line = memory.ContextLine()!;
        Assert.True(line.Length <= CompanionMemory.MaximumContextCharacters + 20);
        Assert.Contains("fact 59 ", line);
        Assert.DoesNotContain("fact 0 ", line);
    }

    [Theory]
    [InlineData("https://www.example.com/menu", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("ms-settings:bluetooth", true)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("notepad", false)]
    [InlineData("shell:startup", false)]
    public void OnlyWebAndSettingsLinks_CanBeOpened(string link, bool allowed) =>
        Assert.Equal(allowed, CompanionActions.IsAllowedLink(link, out _));

    [Fact]
    public void Claude_GetsOnlyTheTwoReadOnlyWebTools_PreApproved()
    {
        var startInfo = ClaudeCodeCliProtocol.BuildStartInfo(new ClaudeCodeCliBackendSettings { WebTools = true }, "prompt.txt");

        List<string> arguments = [.. startInfo.ArgumentList];
        Assert.Equal("WebSearch,WebFetch", arguments[arguments.IndexOf("--tools") + 1]);
        Assert.Equal("WebSearch,WebFetch", arguments[arguments.IndexOf("--allowedTools") + 1]);
        Assert.DoesNotContain("--permission-mode", arguments);
    }

    [Fact]
    public void Codex_SearchFlagComesBeforeExec()
    {
        var startInfo = CodexCliProtocol.BuildStartInfo(new CodexCliBackendSettings { WebSearch = true }, [], "out.txt", ".");

        Assert.Equal(["--search", "exec"], startInfo.ArgumentList.Take(2));
    }

    [Fact]
    public void ANewAssistantMessage_StartsOnAFreshLine_SoItsFenceDoesntRunIntoTheLastOne()
    {
        Assert.Equal("\n", ClaudeCodeCliProtocol.TryGetTextDelta(
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m2","content":[]}}}"""));
        (IReadOnlyList<ProgramCall> calls, _) = ReplyProgramReader.ReadAll(
            "\n```clicky\nsay(\"let me look that up.\")\n```" + "\n" + "```clicky\nsay(\"it opens at nine.\")\n```");
        Assert.Equal(["let me look that up.", "it opens at nine."], calls.Select(call => call.Text(0)));
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("msedge", true)]
    [InlineData("Code", false)]
    public void PageReading_IsOnlyForBrowsers(string processName, bool isBrowser) =>
        Assert.Equal(isBrowser, BrowserPageReader.IsBrowser(new ForegroundAppInfo(1, processName, "title")));
}

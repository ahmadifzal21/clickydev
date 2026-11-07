using System.Text.Json;
using Clicky.Windows.AI;
using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.19: the app's [context] lines, and how each CLI receives them.</summary>
public sealed class CompanionTurnContextTests
{
    [Fact]
    public void Context_NamesTheTimeAppTitleInputAndStep_ThenTheOtherAppLines()
    {
        string context = CompanionTurnContext.Build(new DateTime(2026, 11, 7, 14, 32, 0),
            new ForegroundAppInfo(12, "Code", "settings.json - heyclicky - Visual Studio Code"), isVoice: true, walkthroughStep: 2,
            ["[App] Last time they clicked somewhere else.", "", "[memory] name is Ifzal"]);

        Assert.Equal("[context] Sat 7 Nov 14:32 · app: Code \"settings.json - heyclicky - Visual Studio Code\" · voice · walkthrough step 2\n" +
            "[App] Last time they clicked somewhere else.\n[memory] name is Ifzal", context);
    }

    [Fact]
    public void Context_WithoutAnAppOrStep_StillSaysWhenAndHow() =>
        Assert.Equal("[context] Sat 7 Nov 09:05 · typed",
            CompanionTurnContext.Build(new DateTime(2026, 11, 7, 9, 5, 0), null, isVoice: false, walkthroughStep: null, []));

    [Fact]
    public void Claude_GetsTheContextAsTheFirstBlock_BeforeTheScreenshots()
    {
        CompanionChatRequest request = new("system", [new([1], "screen 1")], [], "where is save?") { TurnContext = "[context] now" };

        List<object> blocks = ClaudeCodeCliProtocol.BuildCurrentUserContentBlocks(request);

        string[] texts = blocks.Select(block => JsonSerializer.SerializeToElement(block))
            .Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(["[context] now", "screen 1", "where is save?"], texts);
        Assert.Equal("text", JsonSerializer.SerializeToElement(blocks[0]).GetProperty("type").GetString());
    }

    [Fact]
    public void Codex_GetsTheContextAsItsOwnParagraph_BeforeTheImages()
    {
        string prompt = CodexCliProtocol.BuildPrompt(new CompanionChatRequest("system", [new([1], "screen 1")], [], "where is save?")
        {
            TurnContext = "[context] now"
        });

        Assert.True(prompt.IndexOf("[context] now", StringComparison.Ordinal) < prompt.IndexOf("Attached images", StringComparison.Ordinal));
    }
}

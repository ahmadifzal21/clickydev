using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

public class CompanionConversationHistoryTests
{
    [Fact]
    public void Record_StripsPointTagFromStoredAnswer()
    {
        var history = new CompanionConversationHistory();

        history.Record("where is save?", "click save up top. [POINT:10,20:save button]");
        history.Record("what is html?", "it's markup. [POINT:none]");

        Assert.Equal(
            [new("where is save?", "click save up top."), new("what is html?", "it's markup.")],
            history.GetExchanges());
    }

    [Fact]
    public void Record_KeepsOnlyTheLastTenExchanges()
    {
        var history = new CompanionConversationHistory();
        for (int index = 1; index <= 12; index++) history.Record($"q{index}", $"a{index}");

        var exchanges = history.GetExchanges();
        Assert.Equal(CompanionConversationHistory.MaximumExchangeCount, exchanges.Count);
        Assert.Equal("q3", exchanges[0].UserTranscript);
        Assert.Equal("q12", exchanges[^1].UserTranscript);
    }

    [Fact]
    public void GetExchanges_ReturnsASnapshot()
    {
        var history = new CompanionConversationHistory();
        var before = history.GetExchanges();

        history.Record("q", "a");

        Assert.Empty(before);
    }
}

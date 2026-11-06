using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

public sealed class ReplyLineSplitterTests
{
    private static List<string> Feed(ReplyLineSplitter splitter, params string[] deltas)
    {
        List<string> lines = [];
        foreach (string delta in deltas) lines.AddRange(splitter.Add(delta).Select(line => line.Text));
        return lines;
    }

    [Fact]
    public void Sentence_IsReleasedOnceTheNextWordStarts()
    {
        ReplyLineSplitter splitter = new();

        Assert.Empty(Feed(splitter, "That's the Start ", "button. "));
        Assert.Equal(["That's the Start button."], Feed(splitter, "Click"));
        Assert.Empty(Feed(splitter, " it to open your apps."));
        Assert.Equal(["Click it to open your apps."], splitter.Finish().Select(line => line.Text));
    }

    [Fact]
    public void Newline_EndsALine_EvenWithoutPunctuation()
    {
        ReplyLineSplitter splitter = new();

        Assert.Equal(["open the file menu"], Feed(splitter, "open the file menu\n", "then pick"));
        Assert.Equal(["then pick export"], Feed(splitter, " export\n"));
        Assert.Empty(splitter.Finish());
    }

    [Fact]
    public void PointTag_StaysWithItsSentence_EvenWhenItArrivesInPieces()
    {
        ReplyLineSplitter splitter = new();

        Assert.Empty(Feed(splitter, "see the gear? ", "[POI", "NT:1100,42:se", "ttings]"));
        Assert.Equal(["see the gear? [POINT:1100,42:settings]"], splitter.Finish().Select(line => line.Text));
    }

    [Fact]
    public void StepList_GivesOneLinePerStep_WithItsOwnTag()
    {
        ReplyLineSplitter splitter = new();

        List<string> lines = Feed(splitter, "first open file. [POINT:10,5:file]\nthen hit ", "export. [POINT:40,90:export]\n");

        Assert.Equal(["first open file. [POINT:10,5:file]", "then hit export. [POINT:40,90:export]"], lines);
    }

    [Fact]
    public void Decimals_AndAbbreviationsWithoutASpace_DoNotSplit()
    {
        ReplyLineSplitter splitter = new();

        Assert.Empty(Feed(splitter, "version 3.5 is out"));
        Assert.Equal(["version 3.5 is out"], splitter.Finish().Select(line => line.Text));
    }

    [Fact]
    public void VisibleText_HidesCompleteAndUnfinishedTags()
    {
        ReplyLineSplitter splitter = new();

        Feed(splitter, "it's up top. [POINT:10,10:menu]\nnext the ");
        Assert.Equal("it's up top.\nnext the", splitter.VisibleText);
        Feed(splitter, "button [POI");
        Assert.Equal("it's up top.\nnext the button", splitter.VisibleText);
    }

    [Fact]
    public void LaterSentencesOfALine_AreMarkedAsContinuations()
    {
        ReplyLineSplitter splitter = new();

        List<ReplyLine> lines = [.. splitter.Add("open file. then save it. [POINT:1,2:save]\nnext one"), .. splitter.Finish()];

        Assert.Equal([true, false, true], lines.Select(line => line.StartsLine));
        Assert.Equal(["open file.", "then save it. [POINT:1,2:save]", "next one"], lines.Select(line => line.Text));
    }
}

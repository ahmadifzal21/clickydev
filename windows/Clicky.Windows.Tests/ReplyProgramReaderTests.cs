using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

public sealed class ReplyProgramReaderTests
{
    private static List<string> Feed(ReplyProgramReader reader, params string[] deltas)
    {
        List<string> calls = [];
        foreach (string delta in deltas) calls.AddRange(reader.Add(delta).Select(call => call.ToString()));
        calls.AddRange(reader.Finish().Select(call => call.ToString()));
        return calls;
    }

    [Fact]
    public void Parser_ReadsStringsNumbersArraysObjectsAndKeywords()
    {
        ProgramParseOutcome outcome = ProgramCallParser.TryParse(
            """trace([[1, 2], [3.5, -4]], 'it\'s "here"\n', {r: 12, "color": "#fff"}, true, null);""", 0, out ProgramCall? call, out int end);

        Assert.Equal(ProgramParseOutcome.Complete, outcome);
        Assert.Equal("trace", call!.Name);
        Assert.Equal([1, 2, 3.5, -4], call.Numbers(0));
        Assert.Equal("it's \"here\"\n", call.Text(1));
        Dictionary<string, object?> options = Assert.IsType<Dictionary<string, object?>>(call.Arguments[2]);
        Assert.Equal(12.0, options["r"]);
        Assert.Equal("#fff", options["COLOR"]);
        Assert.Equal(true, call.Arguments[3]);
        Assert.Null(call.Arguments[4]);
        Assert.Equal(end, """trace([[1, 2], [3.5, -4]], 'it\'s "here"\n', {r: 12, "color": "#fff"}, true, null);""".Length);
    }

    [Theory]
    [InlineData("say(\"half a sent")]
    [InlineData("ring(12")]
    [InlineData("ring(12, 4")]
    [InlineData("box")]
    [InlineData("say(\"x\\")]
    public void Parser_SaysIncomplete_WhileTheCallIsStillStreaming(string partial) =>
        Assert.Equal(ProgramParseOutcome.Incomplete, ProgramCallParser.TryParse(partial, 0, out _, out _));

    [Theory]
    [InlineData("hello world")]
    [InlineData("say(alert(1))")]
    [InlineData("ring(1 2)")]
    public void Parser_RejectsAnythingThatIsNotADataOnlyCall(string text) =>
        Assert.Equal(ProgramParseOutcome.Invalid, ProgramCallParser.TryParse(text, 0, out _, out _));

    [Fact]
    public void FencedProgram_StreamedCharByChar_GivesEachCallOnceInOrder()
    {
        const string reply = "```clicky\nstep(1);\nsay(\"open the menu.\")\nring(10, 20, 8)\n// a comment\nsay(\"then save; done.\")\n```\n";

        List<string> calls = Feed(new ReplyProgramReader(), reply.Select(character => character.ToString()).ToArray());

        Assert.Equal(["step(1)", "say(\"open the menu.\")", "ring(10, 20, 8)", "say(\"then save; done.\")"], calls);
    }

    [Fact]
    public void ScreenCall_SetsTheScreenOfLaterCalls()
    {
        ReplyProgramReader reader = new();
        List<ProgramCall> calls = [.. reader.Add("```js\npoint(1, 2, \"a\")\nscreen(2)\npoint(3, 4, \"b\")\n```"), .. reader.Finish()];

        Assert.Equal([null, 2], calls.Select(call => call.ScreenNumber));
    }

    [Fact]
    public void BareCallLinesOutsideAFence_AreCode_ButProseStartingWithTheSameWordIsNot()
    {
        List<string> calls = Feed(new ReplyProgramReader(), "say(\"hi there.\"); point(5, 6, \"x\")\nclick it when you're ready.\n");

        Assert.Equal(["say(\"hi there.\")", "point(5, 6, \"x\")", "say(\"click it when you're ready.\")"], calls);
    }

    [Fact]
    public void Prose_WithTagsAnywhere_BecomesSaysAndPoints_AndContinuingSentencesAreMarked()
    {
        ReplyProgramReader reader = new();
        List<ProgramCall> calls = [.. reader.Add("open file [POINT:24,12:file menu] now. then export. [POINT:60,410:export:screen2]\n"), .. reader.Finish()];

        Assert.Equal(["say(\"open file now.\")", "point(24, 12, \"file menu\")", "say(\"then export.\")", "point(60, 410, \"export\")"],
            calls.Select(call => call.ToString()));
        Assert.True(calls[2].ContinuesLine);
        Assert.Equal(2, calls[3].ScreenNumber);
        Assert.Equal("open file now. then export.", reader.VisibleText);
    }

    [Fact]
    public void BrokenLineInsideAFence_IsSkipped_AndTheRestStillReads()
    {
        List<string> calls = Feed(new ReplyProgramReader(), "```\nsay(\"one.\")\nthis is not code\nsay(\"two.\")\n```");

        Assert.Equal(["say(\"one.\")", "say(\"two.\")"], calls);
    }

    [Fact]
    public void ReadAll_GivesTheSpokenTextForThePanel()
    {
        Assert.Equal("first.\nsecond.", ReplyProgramReader.ReadAll("```clicky\nsay(\"first.\")\nring(1, 2, 3)\nsay(\"second.\")\n```").VisibleText);
        Assert.Equal("plain answer.", ReplyProgramReader.ReadAll("plain answer. [POINT:none]").VisibleText);
    }
}

using System.Text.RegularExpressions;
using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

public sealed class TurnTimelineTests
{
    [Fact]
    public void Describe_ListsStagesInOrder_KeepingOnlyTheFirstMarkOfEach()
    {
        TurnTimeline turnTimeline = new("release");
        turnTimeline.Mark("transcript");
        turnTimeline.Mark("capture");
        turnTimeline.Mark("transcript");
        turnTimeline.Mark("done");

        Assert.Matches(new Regex(@"^Turn timing from release: transcript \d+\.\d\d s, capture \d+\.\d\d s, done \d+\.\d\d s$"),
            turnTimeline.Describe());
    }
}

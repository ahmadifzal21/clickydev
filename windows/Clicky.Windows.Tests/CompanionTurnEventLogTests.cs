using System.Threading.Channels;
using Clicky.Windows.Companion;
using Clicky.Windows.Tray;

namespace Clicky.Windows.Tests;

public sealed class CompanionTurnEventLogTests
{
    [Fact]
    public void Events_GetRisingSequenceNumbers_AcrossTurns()
    {
        CompanionTurnEventLog turnEventLog = new();
        int firstTurnId = turnEventLog.StartTurn("release");
        turnEventLog.Append(firstTurnId, CompanionTurnEventKind.TurnEnded);
        int secondTurnId = turnEventLog.StartTurn("question");

        Assert.Equal([1L, 2L, 3L], turnEventLog.ReadAfter(0).Select(turnEvent => turnEvent.Sequence));
        Assert.Equal(2, secondTurnId);
        Assert.Equal([CompanionTurnEventKind.TurnStarted], turnEventLog.ReadTurn(secondTurnId).Select(turnEvent => turnEvent.Kind));
    }

    [Fact]
    public void ReadAfter_ReplaysOnlyWhatCameAfterTheGivenSequence()
    {
        CompanionTurnEventLog turnEventLog = new();
        int turnId = turnEventLog.StartTurn("release");
        CompanionTurnEvent capture = turnEventLog.Append(turnId, CompanionTurnEventKind.CaptureDone);
        turnEventLog.Append(turnId, CompanionTurnEventKind.ReplyReady, "hi");

        Assert.Equal([CompanionTurnEventKind.ReplyReady], turnEventLog.ReadAfter(capture.Sequence).Select(turnEvent => turnEvent.Kind));
    }

    [Fact]
    public async Task Follow_ReplaysForALateReader_ThenDeliversNewEventsLive_AndEndsOnCancel()
    {
        CompanionTurnEventLog turnEventLog = new();
        int turnId = turnEventLog.StartTurn("release");
        turnEventLog.Append(turnId, CompanionTurnEventKind.TranscriptReady, "where is it");
        using CancellationTokenSource stopFollowing = new();

        ChannelReader<CompanionTurnEvent> follower = turnEventLog.Follow(0, stopFollowing.Token);
        turnEventLog.Append(turnId, CompanionTurnEventKind.TurnEnded);
        stopFollowing.Cancel();
        turnEventLog.Append(turnId, CompanionTurnEventKind.Pointed, "after the reader left");

        List<CompanionTurnEventKind> seen = [];
        await foreach (CompanionTurnEvent turnEvent in follower.ReadAllAsync().WithCancellation(new CancellationTokenSource(5000).Token))
            seen.Add(turnEvent.Kind);
        Assert.Equal([CompanionTurnEventKind.TurnStarted, CompanionTurnEventKind.TranscriptReady, CompanionTurnEventKind.TurnEnded], seen);
    }

    [Fact]
    public void OldTurns_AreDropped_SoTheLogDoesNotGrowForever()
    {
        CompanionTurnEventLog turnEventLog = new();
        for (int turn = 1; turn <= 25; turn++) turnEventLog.StartTurn("question");

        Assert.Empty(turnEventLog.ReadTurn(5));
        Assert.Single(turnEventLog.ReadTurn(6));
        Assert.Equal(20, turnEventLog.ReadAfter(0).Count);
    }

    [Fact]
    public void Timing_UsesTheFirstEventOfEachStage_InOrder()
    {
        CompanionTurnEventLog turnEventLog = new();
        int turnId = turnEventLog.StartTurn("release");
        turnEventLog.Append(turnId, CompanionTurnEventKind.CaptureDone);
        turnEventLog.Append(turnId, CompanionTurnEventKind.TextDelta, "hel");
        turnEventLog.Append(turnId, CompanionTurnEventKind.TextDelta, "lo");
        turnEventLog.Append(turnId, CompanionTurnEventKind.TurnEnded);

        Assert.Matches(@"^Turn 1 timing from release: capture \d+\.\d\d s, first text \d+\.\d\d s, done \d+\.\d\d s$",
            CompanionTurnTiming.Describe(turnEventLog.ReadTurn(turnId)));
    }

    [Fact]
    public void Panel_ShowsTheAnswerWithoutItsPointTag_AndFailures_AndIgnoresOtherEvents()
    {
        Assert.Equal("Answered: it's up top.",
            CompanionPanelText.GetTurnNote(new(1, 1, CompanionTurnEventKind.ReplyReady, "it's up top. [POINT:10,10:menu]", TimeSpan.Zero)));
        Assert.Equal("Clicky couldn't answer: offline",
            CompanionPanelText.GetTurnNote(new(2, 1, CompanionTurnEventKind.Failed, "Clicky couldn't answer: offline", TimeSpan.Zero)));
        Assert.Null(CompanionPanelText.GetTurnNote(new(3, 1, CompanionTurnEventKind.CaptureDone, "", TimeSpan.Zero)));
    }
}

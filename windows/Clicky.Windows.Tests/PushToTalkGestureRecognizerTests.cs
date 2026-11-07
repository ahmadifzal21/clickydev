using Clicky.Windows.Input;

namespace Clicky.Windows.Tests;

public sealed class PushToTalkGestureRecognizerTests
{
    private static readonly PushToTalkShortcutTransition Press = PushToTalkShortcutTransition.Pressed;
    private static readonly PushToTalkShortcutTransition Release = PushToTalkShortcutTransition.Released;

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Hold_IsPushToTalk()
    {
        PushToTalkGestureRecognizer recognizer = new();

        Assert.Equal(PushToTalkGestureAction.StartListening, recognizer.Handle(Press, Ms(0)));
        Assert.Equal(PushToTalkGestureAction.StopAndAnswer, recognizer.Handle(Release, Ms(1500)));
        Assert.False(recognizer.IsHandsFree);
    }

    [Fact]
    public void SingleQuickTap_IsDropped()
    {
        PushToTalkGestureRecognizer recognizer = new();

        recognizer.Handle(Press, Ms(0));

        Assert.Equal(PushToTalkGestureAction.StopAndDiscard, recognizer.Handle(Release, Ms(200)));
        Assert.False(recognizer.IsHandsFree);
    }

    [Fact]
    public void DoubleTap_ListensHandsFree_UntilTheNextTap_WhoseReleaseIsIgnored()
    {
        PushToTalkGestureRecognizer recognizer = new();
        recognizer.Handle(Press, Ms(0));
        recognizer.Handle(Release, Ms(150));

        Assert.Equal(PushToTalkGestureAction.StartListening, recognizer.Handle(Press, Ms(450)));
        Assert.Null(recognizer.Handle(Release, Ms(600))); // Recording from this press keeps going.
        Assert.True(recognizer.IsHandsFree);
        Assert.Equal(Ms(450) + PushToTalkGestureRecognizer.HandsFreeMaximumDuration, recognizer.HandsFreeDeadline);

        Assert.Equal(PushToTalkGestureAction.StopAndAnswer, recognizer.Handle(Press, Ms(9000)));
        Assert.Null(recognizer.Handle(Release, Ms(9100)));
        Assert.False(recognizer.IsHandsFree);
        Assert.Null(recognizer.HandsFreeDeadline);
    }

    [Fact]
    public void HandsFree_StopsAndAnswers_AtItsCap()
    {
        PushToTalkGestureRecognizer recognizer = new();
        recognizer.Handle(Press, Ms(0));
        recognizer.Handle(Release, Ms(100));
        recognizer.Handle(Press, Ms(300));
        recognizer.Handle(Release, Ms(400));

        Assert.Null(recognizer.HandleTimeElapsed(Ms(30_000)));
        Assert.Equal(PushToTalkGestureAction.StopAndAnswer, recognizer.HandleTimeElapsed(Ms(60_300)));
        Assert.False(recognizer.IsHandsFree);
        Assert.Equal(PushToTalkGestureAction.StartListening, recognizer.Handle(Press, Ms(61_000)));
    }

    [Fact]
    public void SlowSecondTap_IsJustAnotherTap()
    {
        PushToTalkGestureRecognizer recognizer = new();
        recognizer.Handle(Press, Ms(0));
        recognizer.Handle(Release, Ms(100));

        recognizer.Handle(Press, Ms(700)); // More than 500 ms after the first tap's release.

        Assert.Equal(PushToTalkGestureAction.StopAndDiscard, recognizer.Handle(Release, Ms(800)));
        Assert.False(recognizer.IsHandsFree);
    }

    [Fact]
    public void HoldAfterOneTap_IsStillPushToTalk()
    {
        PushToTalkGestureRecognizer recognizer = new();
        recognizer.Handle(Press, Ms(0));
        recognizer.Handle(Release, Ms(100));

        Assert.Equal(PushToTalkGestureAction.StartListening, recognizer.Handle(Press, Ms(300)));
        Assert.Equal(PushToTalkGestureAction.StopAndAnswer, recognizer.Handle(Release, Ms(2300)));
        Assert.False(recognizer.IsHandsFree);
    }

    [Fact]
    public void TripleTap_IsDoubleTapThenSend()
    {
        PushToTalkGestureRecognizer recognizer = new();
        recognizer.Handle(Press, Ms(0));
        recognizer.Handle(Release, Ms(100));
        recognizer.Handle(Press, Ms(300));
        recognizer.Handle(Release, Ms(400));

        Assert.Equal(PushToTalkGestureAction.StopAndAnswer, recognizer.Handle(Press, Ms(600)));
        Assert.Null(recognizer.Handle(Release, Ms(700)));
        Assert.False(recognizer.IsHandsFree);
    }
}

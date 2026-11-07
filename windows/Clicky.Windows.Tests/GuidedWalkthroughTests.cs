using System.Windows;
using Clicky.Windows.Automation;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.11-T11.17: the guided-step loop with a fake mouse, a fake screen and a fake app around it.</summary>
public sealed class GuidedWalkthroughTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    private sealed class FakeMouse : IMouseEventSource
    {
        public event Action<LowLevelMouseEvent>? MouseEventObserved { add { } remove { } }
        public int ActiveLeases { get; private set; }

        public IDisposable Acquire()
        {
            ActiveLeases++;
            return new Lease(this);
        }

        private sealed class Lease(FakeMouse mouse) : IDisposable
        {
            private bool isReleased;

            public void Dispose()
            {
                if (isReleased) return;
                isReleased = true;
                mouse.ActiveLeases--;
            }
        }
    }

    /// <summary>The screen as a list of pictures: each sample takes the next one, then the last one repeats.</summary>
    private sealed class FakeScreen(params byte[][] pictures) : IScreenSampler
    {
        private int next;
        public byte[]? Sample(PixelRect area) => pictures.Length == 0 ? null : pictures[Math.Min(next++, pictures.Length - 1)];
    }

    private static byte[] Picture(byte shade) => Enumerable.Repeat(shade, 400).ToArray();

    private sealed class Harness
    {
        public FakeMouse Mouse { get; } = new();
        public List<ScreenMark> DrawnTargets { get; } = [];
        public int TargetRemovals { get; set; }
        public int Clears { get; set; }
        public int Interrupts { get; set; }
        public bool TalkKeyHeld { get; set; }
        public int ForegroundProcess { get; set; } = 4242;
        public TaskCompletionSource<TurnTrigger> FollowUp { get; } = new();
        public GuidedWalkthrough Walkthrough { get; }

        public Harness(IScreenSampler screen)
        {
            Walkthrough = new GuidedWalkthrough(Mouse, new ScreenSettleWaiter(screen), new GuidedWalkthroughHost
            {
                DrawTarget = DrawnTargets.Add,
                RemoveTarget = () => TargetRemovals++,
                ClearDrawing = () => Clears++,
                HasDrawing = () => false,
                InterruptTurn = () => Interrupts++,
                StartFollowUp = trigger => { FollowUp.TrySetResult(trigger); return Task.CompletedTask; },
                IsTalkKeyHeld = () => TalkKeyHeld,
                ForegroundProcessId = () => ForegroundProcess,
                IsClickyWindowAt = (x, _) => x >= 1900,
                CursorPosition = () => new Point(0, 0)
            });
        }
    }

    private static GuidedTarget SaveTarget(int step = 1, bool isHover = false) =>
        new(new Rect(100, 100, 60, 30), "save", isHover, step, Monitor, 1);

    private static LowLevelMouseEvent Click(int x, int y) => new(LowLevelMouseEventKind.LeftDown, x, y, 0);

    [Fact]
    public void TargetRing_IsHalfTheBoxDiagonal_WithinItsLimits_AndCountsClicksNearIt()
    {
        GuidedTarget target = SaveTarget();
        Assert.Equal(Math.Max(28, Math.Sqrt(60 * 60 + 30 * 30) / 2), target.RingRadius);
        Assert.True(target.Contains(130, 115));
        Assert.True(target.Contains(95, 95)); // Within the 8 px of slack.
        Assert.False(target.Contains(300, 300));
        Assert.Equal(180, new GuidedTarget(new Rect(0, 0, 900, 900), "huge", false, 1, Monitor, 1).RingRadius);
        Assert.Equal(56, new GuidedTarget(new Rect(0, 0, 4, 4), "tiny", false, 1, Monitor, 2).RingRadius);
    }

    [Theory]
    [InlineData("now click the export button.", true)]
    [InlineData("scroll down a little, it's under privacy.", true)]
    [InlineData("then open the file menu up top", true)]
    [InlineData("do you want me to click it?", false)]
    [InlineData("that's the save button.", false)]
    [InlineData("", false)]
    public void Instruction_IsAnImperativeStep_NotAQuestion(string line, bool isInstruction) =>
        Assert.Equal(isInstruction, GuidedWalkthrough.IsInstruction(line));

    [Theory]
    [InlineData(102, 102, 56, 26, true)]     // Nearly the model's box.
    [InlineData(100, 100, 4, 4, false)]      // Too small.
    [InlineData(0, 0, 1000, 600, false)]     // More than a quarter of the monitor.
    [InlineData(400, 400, 60, 30, false)]    // Doesn't contain the box's center.
    [InlineData(80, 90, 200, 50, true)]      // Bigger, but under 8x the area and around the center.
    [InlineData(60, 60, 400, 120, false)]    // Over 8x the box's area.
    public void SnapRules_AcceptOnlyAPlausibleControl(double x, double y, double width, double height, bool accepted) =>
        Assert.Equal(accepted, UiaControlSnapper.IsPlausibleSnap(new Rect(x, y, width, height), new Rect(100, 100, 60, 30),
            new Rect(0, 0, 1920, 1080)));

    [Fact]
    public void Settle_StopsSoonAfterTheScreenChangesAndHoldsStill()
    {
        ScreenSettleWaiter waiter = new(new FakeScreen(Picture(10), Picture(200), Picture(200)));
        (bool changed, TimeSpan waited) = waiter.WaitAsync(Monitor, null, TimeSpan.FromMilliseconds(900), CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(changed);
        Assert.InRange(waited, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(1.4));
    }

    [Fact]
    public void Settle_WaitsOutAChangeThatComesInTwoParts()
    {
        // Word: Backstage closes (200, holds still), then the Save dialog opens (120). Waiting less than 0.9 s missed the dialog.
        FakeScreen screen = new(Picture(10), Picture(200), Picture(200), Picture(200), Picture(120));
        ScreenSettleWaiter waiter = new(screen);
        waiter.WaitAsync(Monitor, null, TimeSpan.FromMilliseconds(900), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(120, screen.Sample(Monitor)![0]); // It went past the first part before settling.
    }

    [Fact]
    public void Settle_GivesUpAfterASecondAndAHalf_WhenNothingChanges()
    {
        ScreenSettleWaiter waiter = new(new FakeScreen(Picture(10)));
        (bool changed, TimeSpan waited) = waiter.WaitAsync(Monitor, null, TimeSpan.FromMilliseconds(900), CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(changed);
        Assert.InRange(waited, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public void Settle_WaitsTheFallback_WhenTheScreenCantBeRead()
    {
        ScreenSettleWaiter waiter = new(new FakeScreen());
        (_, TimeSpan waited) = waiter.WaitAsync(Monitor, null, TimeSpan.FromMilliseconds(300), CancellationToken.None).GetAwaiter().GetResult();

        Assert.InRange(waited, TimeSpan.FromMilliseconds(280), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ClickInsideTheTarget_InterruptsAndStartsTheNextStepWithAFreshFact() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10), Picture(200), Picture(200)));
        harness.Walkthrough.ArmTarget(SaveTarget(step: 2));
        Thread.Sleep(GuidedWalkthrough.RingDelay + TimeSpan.FromMilliseconds(50));
        harness.Walkthrough.Tick();
        Assert.Single(harness.DrawnTargets);
        Assert.Equal(1, harness.Mouse.ActiveLeases);

        harness.Walkthrough.HandleMouseEvent(Click(130, 115));
        StaTestThread.WaitFor(harness.FollowUp.Task);

        TurnTrigger trigger = harness.FollowUp.Task.Result;
        Assert.Equal(TurnTriggerKind.ClickedTarget, trigger.Kind);
        Assert.Equal(3, trigger.WalkthroughStep);
        Assert.Contains("They clicked \"save\"", trigger.AppFact);
        Assert.Contains("The screen changed", trigger.AppFact);
        Assert.Equal(1, harness.Interrupts);
        Assert.Equal(1, harness.TargetRemovals);
        Assert.False(harness.Walkthrough.HasTarget);
        Assert.Equal(0, harness.Mouse.ActiveLeases);
    });

    [Fact]
    public void ClickElsewhere_EndsTheGuide_AndLeavesANoteForTheNextTurn() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10)));
        harness.Walkthrough.ArmTarget(SaveTarget());

        harness.Walkthrough.HandleMouseEvent(Click(800, 600));

        Assert.False(harness.Walkthrough.HasTarget);
        Assert.False(harness.FollowUp.Task.IsCompleted);
        Assert.Contains("somewhere other than \"save\"", Assert.Single(harness.Walkthrough.TakeNotes()));
        Assert.Empty(harness.Walkthrough.TakeNotes());
    });

    [Fact]
    public void ClicksOnClickysOwnWindows_AreIgnored() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10)));
        harness.Walkthrough.ArmTarget(SaveTarget());

        harness.Walkthrough.HandleMouseEvent(Click(1910, 500));

        Assert.True(harness.Walkthrough.HasTarget);
    });

    [Fact]
    public void AnotherAppComingToTheFront_DisarmsTheTarget() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10)));
        harness.Walkthrough.ArmTarget(SaveTarget());
        harness.ForegroundProcess = 777;

        for (int tick = 0; tick < 4; tick++) harness.Walkthrough.Tick();

        Assert.False(harness.Walkthrough.HasTarget);
    });

    [Fact]
    public void TheFifteenthStep_ArmsNothing() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10)));
        harness.Walkthrough.ArmTarget(SaveTarget(step: GuidedWalkthrough.MaximumSteps));

        Assert.False(harness.Walkthrough.HasTarget);
        Assert.Equal(0, harness.Mouse.ActiveLeases);
    });

    [Fact]
    public void StepWatch_AClickThatChangesTheScreen_MovesOn() => StaTestThread.Run(() =>
    {
        // Turn-end picture, then the click's settle samples, then the comparison sample: all changed.
        Harness harness = new(new FakeScreen(Picture(10), Picture(200), Picture(200), Picture(200)));
        harness.Walkthrough.ArmStepWatch(Monitor, step: 3);

        harness.Walkthrough.HandleMouseEvent(new LowLevelMouseEvent(LowLevelMouseEventKind.Wheel, 500, 500, 0));
        StaTestThread.WaitFor(harness.FollowUp.Task);

        TurnTrigger trigger = harness.FollowUp.Task.Result;
        Assert.Equal(TurnTriggerKind.DidStep, trigger.Kind);
        Assert.Equal(4, trigger.WalkthroughStep);
        Assert.Contains("They scrolled", trigger.AppFact);
        Assert.False(harness.Walkthrough.IsWatchingForAStep);
    });

    [Fact]
    public void AfterOpeningSettings_TheNextStepFollowsWithoutAQuestion() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10)));
        harness.Walkthrough.ContinueAfterOpen(Monitor, step: 1, "ms-settings:colors");
        StaTestThread.WaitFor(harness.FollowUp.Task);

        TurnTrigger trigger = harness.FollowUp.Task.Result;
        Assert.Equal(TurnTriggerKind.DidStep, trigger.Kind);
        Assert.Equal(2, trigger.WalkthroughStep);
        Assert.Contains("Settings page you opened (ms-settings:colors)", trigger.AppFact);
    });

    [Fact]
    public void StepWatch_AClickThatChangesNothing_IsNotAStep() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10)));
        harness.Walkthrough.ArmStepWatch(Monitor, step: 1);

        harness.Walkthrough.HandleMouseEvent(Click(500, 500));
        Task settled = Task.Delay(TimeSpan.FromSeconds(2.2));
        StaTestThread.WaitFor(settled);

        Assert.False(harness.FollowUp.Task.IsCompleted);
        Assert.True(harness.Walkthrough.IsWatchingForAStep);
    });

    [Fact]
    public void StepWatch_IgnoresClicksWhileTheTalkKeyIsHeld() => StaTestThread.Run(() =>
    {
        Harness harness = new(new FakeScreen(Picture(10), Picture(200)));
        harness.Walkthrough.ArmStepWatch(Monitor, step: 1);
        harness.TalkKeyHeld = true;

        harness.Walkthrough.HandleMouseEvent(Click(500, 500));

        Assert.True(harness.Walkthrough.IsWatchingForAStep);
        Assert.False(harness.FollowUp.Task.IsCompleted);
    });
}

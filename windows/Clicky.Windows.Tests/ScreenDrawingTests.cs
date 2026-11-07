using System.Windows;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.8-T11.10: reply drawing calls become marks in physical pixels, drawn on the right monitor's overlay.</summary>
public sealed class ScreenDrawingTests
{
    // A 2560x1600 cursor screen sent at 1280x800 (scale 2), and a 1920x1080 screen to its left sent at 1280x720 (scale 1.5).
    private static readonly CompanionScreenCapture[] Captures =
    [
        Capture(true, new PixelRect(0, 0, 2560, 1600), 1280, 800),
        Capture(false, new PixelRect(-1920, 0, 0, 1080), 1280, 720)
    ];

    private static CompanionScreenCapture Capture(bool isCursorScreen, PixelRect bounds, int width, int height) =>
        new([0xFF, 0xD8], "screen", isCursorScreen, new DisplayMonitor("D", bounds, bounds, 96, 96, isCursorScreen), width, height,
            ScreenCaptureMethod.WindowsGraphicsCapture, TimeSpan.Zero);

    private static ProgramCall Call(string programLine) => ReplyProgramReader.ReadAll(programLine + "\n").Calls.Single();

    [Fact]
    public void Ring_ScalesCenterAndRadius_ToPhysicalPixels()
    {
        ScreenMark mark = ScreenMarkBuilder.FromCall(Call("ring(100, 50, 20, \"gear\")"), Captures)!;

        Assert.Equal(ScreenMarkKind.Ring, mark.Kind);
        Assert.Equal(new Point(200, 100), mark.Anchor);
        Assert.Equal(40, mark.Radius);
        Assert.Equal("gear", mark.Text);
    }

    [Fact]
    public void MarksOnScreenTwo_LandOnThatMonitor()
    {
        ProgramCall box = ReplyProgramReader.ReadAll("```clicky\nscreen(2)\nbox(0, 0, 640, 360)\n```").Calls.Single();

        ScreenMark mark = ScreenMarkBuilder.FromCall(box, Captures)!;

        Assert.Equal([new Point(-1920, 0), new Point(-960, 540)], mark.Points);
    }

    [Theory]
    [InlineData("underline(10, 20, 110)", "Underline", 2)]
    [InlineData("highlight(10, 20, 110, 40)", "Highlight", 2)]
    [InlineData("arrow(10, 20, 110, 40)", "Arrow", 2)]
    [InlineData("trace([10, 20, 30, 40, 50, 60])", "Trace", 3)]
    [InlineData("label(10, 20, \"tabs\")", "Label", 1)]
    [InlineData("scrollhint(10, 20, \"up\")", "ScrollHint", 1)]
    [InlineData("card(10, 20, \"Shortcut\", \"press **Ctrl+S**\")", "Card", 1)]
    public void EachDrawingCall_BecomesItsMark(string programLine, string kind, int pointCount)
    {
        ScreenMark mark = ScreenMarkBuilder.FromCall(Call(programLine), Captures)!;

        Assert.Equal(Enum.Parse<ScreenMarkKind>(kind), mark.Kind);
        Assert.Equal(pointCount, mark.Points.Count);
    }

    [Fact]
    public void Card_WithOneString_IsABodyWithoutATitle()
    {
        ScreenMark mark = ScreenMarkBuilder.FromCall(Call("card(10, 20, \"just a tip\")"), Captures)!;

        Assert.Equal("", mark.Title);
        Assert.Equal("just a tip", mark.Text);
    }

    [Theory]
    [InlineData("ring(10, 20)")]
    [InlineData("box(1, 2, 3)")]
    [InlineData("label(10, 20)")]
    [InlineData("trace([1, 2])")]
    public void CallsMissingNumbers_AreSkipped(string programLine) => Assert.Null(ScreenMarkBuilder.FromCall(Call(programLine), Captures));

    [Fact]
    public void CardText_MakesBulletsAndBold_AndNothingElse()
    {
        StaTestThread.Run(() =>
        {
            System.Windows.Controls.TextBlock text = ScreenDrawingLayer.RichText("**Save** often\n- one\n- two <b>x</b>", 12);
            Assert.Equal("Save often\n•  one\n•  two <b>x</b>",
                string.Concat(text.Inlines.Select(inline => inline is System.Windows.Documents.Run run ? run.Text : "\n")));
            Assert.Equal(FontWeights.SemiBold, ((System.Windows.Documents.Run)text.Inlines.First()).FontWeight);
        });
    }

    [Fact]
    public void Manager_DrawsOnTheMonitorUnderTheMark_ClearsAndKeepsTheOverlayUpWhileMarksShow() => StaTestThread.Run(() =>
    {
        using DisplayMonitorCatalog displayMonitorCatalog = new();
        using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
        DisplayMonitor monitor = displayMonitorCatalog.Monitors[0];
        Point middle = new(monitor.Bounds.Left + monitor.Bounds.Width / 2, monitor.Bounds.Top + monitor.Bounds.Height / 2);

        cursorOverlayManager.Draw(new ScreenMark(ScreenMarkKind.Ring, [middle]) { Radius = 30 });
        Assert.False(cursorOverlayManager.HasDrawing); // Hidden overlays don't draw.

        cursorOverlayManager.ShowTransiently();
        cursorOverlayManager.Draw(new ScreenMark(ScreenMarkKind.Ring, [middle]) { Radius = 30, Text = "here" });
        cursorOverlayManager.Draw(new ScreenMark(ScreenMarkKind.Card, [middle]) { Title = "Tip", Text = "body" });
        Assert.True(cursorOverlayManager.HasDrawing);

        cursorOverlayManager.ScheduleTransientHide();
        cursorOverlayManager.AdvanceFrame(middle.X, middle.Y, 5);
        cursorOverlayManager.AdvanceFrame(middle.X, middle.Y, 5);
        Assert.True(cursorOverlayManager.IsOverlayVisible); // The marks hold it up.

        cursorOverlayManager.ClearDrawing();
        Assert.False(cursorOverlayManager.HasDrawing);
        cursorOverlayManager.AdvanceFrame(middle.X, middle.Y, 1.1);
        cursorOverlayManager.AdvanceFrame(middle.X, middle.Y, 0.5);
        Assert.False(cursorOverlayManager.IsOverlayVisible);
    });

    [Fact]
    public void Manager_DropsMarksAfterTheirLifetime() => StaTestThread.Run(() =>
    {
        using DisplayMonitorCatalog displayMonitorCatalog = new();
        using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
        DisplayMonitor monitor = displayMonitorCatalog.Monitors[0];
        Point middle = new(monitor.Bounds.Left + monitor.Bounds.Width / 2, monitor.Bounds.Top + monitor.Bounds.Height / 2);
        cursorOverlayManager.ShowTransiently();
        cursorOverlayManager.Draw(new ScreenMark(ScreenMarkKind.Box, [middle, new Point(middle.X + 40, middle.Y + 20)]));

        cursorOverlayManager.AdvanceFrame(middle.X, middle.Y, CursorOverlayManager.DrawingLifetimeSeconds - 1);
        Assert.True(cursorOverlayManager.HasDrawing);
        cursorOverlayManager.AdvanceFrame(middle.X, middle.Y, 2);
        Assert.False(cursorOverlayManager.HasDrawing);
    });

    [Fact]
    public void HandDrawnShapes_StayCloseToWhatWasAskedFor()
    {
        List<Point> ring = ScreenDrawingLayer.RingPoints(new Point(100, 100), 40, seed: 7);
        Assert.All(ring, point => Assert.InRange((point - new Point(100, 100)).Length, 36, 46));

        List<Point> box = ScreenDrawingLayer.BoxPoints(new Point(10, 10), new Point(110, 60), seed: 7);
        Assert.All(box, point => { Assert.InRange(point.X, 3, 117); Assert.InRange(point.Y, 3, 67); });

        List<Point> trace = ScreenDrawingLayer.SmoothPoints([new Point(0, 0), new Point(50, 0), new Point(100, 50)]);
        Assert.Equal(new Point(0, 0), trace[0]);
        Assert.Equal(new Point(100, 50), trace[^1]);
    }
}

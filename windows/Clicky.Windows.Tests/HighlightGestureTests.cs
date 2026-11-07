using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.23: the highlighter stroke drawn while talking, how it is described and painted into the screenshot.</summary>
public sealed class HighlightGestureTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    private sealed class FakeMouse : IMouseEventSource
    {
        public event Action<LowLevelMouseEvent>? MouseEventObserved { add { } remove { } }
        public IDisposable Acquire() => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private static List<Point> Circle(Point center, double radius, int steps = 40) =>
        Enumerable.Range(0, steps + 1).Select(step => new Point(center.X + radius * Math.Cos(step * 2 * Math.PI / steps),
            center.Y + radius * Math.Sin(step * 2 * Math.PI / steps))).ToList();

    [Fact]
    public void Stroke_StartsAfterEightPixels_AndRestsBecomeDwells() => StaTestThread.Run(() =>
    {
        List<IReadOnlyList<Point>> trails = [];
        HighlightGesture gesture = new(new FakeMouse(), trails.Add, () => { });
        gesture.Begin();
        gesture.AddMoveForTest(new Point(100, 100), 0);
        gesture.AddMoveForTest(new Point(104, 100), 10); // Jitter: not yet a stroke.
        gesture.Drain();
        Assert.Empty(trails);

        gesture.AddMoveForTest(new Point(140, 100), 20);
        gesture.AddMoveForTest(new Point(142, 101), 200);
        gesture.AddMoveForTest(new Point(141, 102), 600); // Rested ~0.4 s within 12 px.
        gesture.AddMoveForTest(new Point(220, 160), 650);
        HighlightStroke stroke = gesture.End()!;

        Assert.NotEmpty(trails);
        Assert.Equal(new Point(100, 100), stroke.Points[0]);
        Assert.Equal(new Point(220, 160), stroke.Points[^1]);
        Assert.Single(stroke.Dwells);
    });

    [Fact]
    public void BarelyMovingWhileTalking_IsNoStroke() => StaTestThread.Run(() =>
    {
        HighlightGesture gesture = new(new FakeMouse(), _ => { }, () => { });
        gesture.Begin();
        gesture.AddMoveForTest(new Point(100, 100), 0);
        gesture.AddMoveForTest(new Point(103, 102), 50);

        Assert.Null(gesture.End());
    });

    [Fact]
    public void Shapes_AreToldApart()
    {
        Assert.Equal("circled", HighlightGesture.ClassifyShape(Circle(new Point(500, 500), 80)));
        Assert.Equal("underlined", HighlightGesture.ClassifyShape([new Point(100, 300), new Point(200, 304), new Point(300, 302)]));
        Assert.Equal("scribbled over", HighlightGesture.ClassifyShape(
            [new Point(100, 100), new Point(300, 160), new Point(110, 220), new Point(310, 280), new Point(120, 340)]));
        Assert.Equal("pointed at", HighlightGesture.ClassifyShape([new Point(100, 100), new Point(110, 108)]));
    }

    [Theory]
    [InlineData(100, 100, "top-left")]
    [InlineData(960, 100, "top")]
    [InlineData(1800, 540, "right")]
    [InlineData(960, 540, "center")]
    [InlineData(1800, 1000, "bottom-right")]
    public void Regions_AreANineCellGrid(double x, double y, string region) =>
        Assert.Equal(region, HighlightGesture.RegionName(new Point(x, y), Monitor));

    [Fact]
    public void Description_SaysShapeRegionAndWhereTheyPaused() =>
        Assert.Equal("circled the top-right area, pausing on the top-right",
            HighlightGesture.Describe(new HighlightStroke(Circle(new Point(1600, 200), 60), [new Point(1600, 140)]), Monitor));

    [Fact]
    public void PaintInto_DrawsTheStrokeOnTheScreenshotInCursorBlue() => StaTestThread.Run(() =>
    {
        byte[] whiteJpeg = WhiteJpeg(960, 540);
        CompanionScreenCapture capture = new(whiteJpeg, "screen", true, new DisplayMonitor("D", Monitor, Monitor, 96, 96, true), 960, 540,
            ScreenCaptureMethod.WindowsGraphicsCapture, TimeSpan.Zero);

        CompanionScreenCapture painted = HighlightGesture.PaintInto(capture, new HighlightStroke([new Point(400, 400), new Point(800, 400)], []));

        Color under = PixelAt(painted.JpegImageBytes, 300, 200); // (600, 400) physical at half scale.
        Color away = PixelAt(painted.JpegImageBytes, 300, 400);
        Assert.True(under.B > under.R + 60, $"stroke pixel {under}");
        Assert.True(away.R > 240 && away.B > 240, $"background pixel {away}");
        Assert.Equal(capture.Label, painted.Label);
    });

    private static byte[] WhiteJpeg(int width, int height)
    {
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        JpegBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color PixelAt(byte[] jpeg, int x, int y)
    {
        BitmapSource frame = new FormatConvertedBitmap(BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0],
            PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }
}

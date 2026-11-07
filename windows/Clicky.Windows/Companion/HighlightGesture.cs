using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Companion;

/// <summary>What the user drew with the mouse while talking, in physical pixels; Dwells are spots the mouse rested on.</summary>
internal sealed record HighlightStroke(IReadOnlyList<Point> Points, IReadOnlyList<Point> Dwells);

/// <summary>
/// Highlight-to-ask (E11 T11.23): while the talk key is held, moving the mouse leaves a highlighter trail on the overlay (from
/// the global mouse hook; clicks are never blocked). It starts once the mouse has moved 8 px from where it was, and a rest of
/// 0.35 s within 12 px is a dwell. On release the stroke is painted into the screenshot of its monitor and described in words
/// ("circled the top-right area"), with the text of the control under it, so the model knows exactly what "this" means.
/// </summary>
internal sealed class HighlightGesture
{
    private const double ActivationDistance = 8;
    private const double MinimumPointSpacing = 1.5;
    private const double DwellRadius = 12;
    private static readonly TimeSpan DwellTime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan TrailFade = TimeSpan.FromSeconds(1);

    private readonly IMouseEventSource mouseEventSource;
    private readonly Action<IReadOnlyList<Point>> showTrail;
    private readonly Action hideTrail;
    private readonly DispatcherTimer drainTimer;
    private readonly ConcurrentQueue<(Point Point, long Milliseconds)> pendingMoves = new();
    private readonly List<Point> points = [];
    private readonly List<Point> dwells = [];
    private IDisposable? mouseLease;
    private Point? pressPoint;
    private bool isActive;
    private (Point Point, long Milliseconds)? restStart;
    private bool restRecorded;

    public HighlightGesture(IMouseEventSource mouseEventSource, Action<IReadOnlyList<Point>> showTrail, Action hideTrail)
    {
        this.mouseEventSource = mouseEventSource;
        this.showTrail = showTrail;
        this.hideTrail = hideTrail;
        drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => Drain(), Dispatcher.CurrentDispatcher);
        drainTimer.Stop();
        mouseEventSource.MouseEventObserved += OnMouseEventFromHookThread;
    }

    public bool IsWatching => mouseLease is not null;

    /// <summary>The talk key went down: start following the mouse.</summary>
    public void Begin()
    {
        End();
        points.Clear();
        dwells.Clear();
        pressPoint = null;
        isActive = false;
        restStart = null;
        pendingMoves.Clear();
        mouseLease = mouseEventSource.Acquire();
        drainTimer.Start();
    }

    /// <summary>The talk key came up: the stroke, or null if the mouse barely moved. The trail fades out shortly after.</summary>
    public HighlightStroke? End()
    {
        if (mouseLease is null) return null;
        Drain();
        mouseLease.Dispose();
        mouseLease = null;
        drainTimer.Stop();
        if (!isActive || points.Count < 2) return null;
        HighlightStroke stroke = new([.. points], [.. dwells]);
        DispatcherTimer fade = new(TrailFade, DispatcherPriority.Background, (sender, _) => { ((DispatcherTimer)sender!).Stop(); hideTrail(); },
            Dispatcher.CurrentDispatcher);
        fade.Start();
        return stroke;
    }

    private void OnMouseEventFromHookThread(LowLevelMouseEvent mouseEvent)
    {
        if (mouseLease is null || mouseEvent.Kind != LowLevelMouseEventKind.Move) return;
        pendingMoves.Enqueue((new Point(mouseEvent.X, mouseEvent.Y), mouseEvent.TimestampMilliseconds));
    }

    /// <summary>Takes the queued moves (tests call it directly) and updates the trail.</summary>
    internal void Drain()
    {
        bool changed = false;
        while (pendingMoves.TryDequeue(out (Point Point, long Milliseconds) move)) changed |= Add(move.Point, move.Milliseconds);
        if (changed && isActive) showTrail([.. points]);
    }

    internal void AddMoveForTest(Point point, long milliseconds) => pendingMoves.Enqueue((point, milliseconds));

    private bool Add(Point point, long milliseconds)
    {
        pressPoint ??= point;
        if (!isActive)
        {
            if ((point - pressPoint.Value).Length < ActivationDistance) return false;
            isActive = true;
            points.Add(pressPoint.Value);
        }
        if (restStart is { } rest && (point - rest.Point).Length <= DwellRadius)
        {
            if (!restRecorded && milliseconds - rest.Milliseconds >= DwellTime.TotalMilliseconds)
            {
                dwells.Add(rest.Point);
                restRecorded = true;
            }
        }
        else
        {
            restStart = (point, milliseconds);
            restRecorded = false;
        }
        if (points.Count > 0 && (point - points[^1]).Length < MinimumPointSpacing) return false;
        points.Add(point);
        return true;
    }

    /// <summary>"circled the top-right area", "underlined the bottom area", "scribbled over the center area", "pointed at the left area".</summary>
    public static string Describe(HighlightStroke stroke, PixelRect monitorBounds)
    {
        Rect bounds = BoundsOf(stroke.Points);
        Point center = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        string region = RegionName(center, monitorBounds);
        string shape = ClassifyShape(stroke.Points);
        string described = shape == "pointed at" ? $"pointed at the {region} area" : $"{shape} the {region} area";
        if (stroke.Dwells.Count > 0) described += $", pausing on the {RegionName(stroke.Dwells[^1], monitorBounds)}";
        return described;
    }

    internal static string ClassifyShape(IReadOnlyList<Point> points)
    {
        Rect bounds = BoundsOf(points);
        double size = Math.Max(bounds.Width, bounds.Height);
        if (size < 24) return "pointed at";
        double pathLength = 0;
        for (int index = 1; index < points.Count; index++) pathLength += (points[index] - points[index - 1]).Length;
        double gap = (points[^1] - points[0]).Length;
        if (bounds.Height < bounds.Width * 0.25 && pathLength < bounds.Width * 1.6) return "underlined";
        if (gap < size * 0.35 && pathLength > (bounds.Width + bounds.Height) * 1.4 && pathLength < (bounds.Width + bounds.Height) * 3.2) return "circled";
        return "scribbled over";
    }

    internal static string RegionName(Point point, PixelRect monitorBounds)
    {
        double relativeX = (point.X - monitorBounds.Left) / Math.Max(1, monitorBounds.Width);
        double relativeY = (point.Y - monitorBounds.Top) / Math.Max(1, monitorBounds.Height);
        string vertical = relativeY < 1 / 3.0 ? "top" : relativeY > 2 / 3.0 ? "bottom" : "";
        string horizontal = relativeX < 1 / 3.0 ? "left" : relativeX > 2 / 3.0 ? "right" : "";
        return (vertical, horizontal) switch
        {
            ("", "") => "center",
            ("", _) => horizontal,
            (_, "") => vertical,
            _ => $"{vertical}-{horizontal}"
        };
    }

    private static Rect BoundsOf(IReadOnlyList<Point> points)
    {
        double left = points.Min(point => point.X), top = points.Min(point => point.Y);
        return new Rect(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    /// <summary>The screenshot of the stroke's monitor with the stroke painted on (8 px, 55% cursor blue), as the model will see it.</summary>
    public static CompanionScreenCapture PaintInto(CompanionScreenCapture capture, HighlightStroke stroke)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(new MemoryStream(capture.JpegImageBytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource picture = decoder.Frames[0];
        int width = capture.ScreenshotWidthPixels, height = capture.ScreenshotHeightPixels;
        double scaleX = width / (double)capture.CapturedMonitor.Bounds.Width, scaleY = height / (double)capture.CapturedMonitor.Bounds.Height;
        Point ToPicture(Point physical) => new((physical.X - capture.CapturedMonitor.Bounds.Left) * scaleX, (physical.Y - capture.CapturedMonitor.Bounds.Top) * scaleY);

        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(picture, new Rect(0, 0, width, height));
            StreamGeometry path = new();
            using (StreamGeometryContext pathContext = path.Open())
            {
                pathContext.BeginFigure(ToPicture(stroke.Points[0]), isFilled: false, isClosed: false);
                pathContext.PolyLineTo(stroke.Points.Skip(1).Select(ToPicture).ToList(), isStroked: true, isSmoothJoin: true);
            }
            Color ink = ScreenDrawingLayer.InkColor;
            Pen pen = new(new SolidColorBrush(Color.FromArgb(140, ink.R, ink.G, ink.B)), 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            context.DrawGeometry(null, pen, path);
        }
        RenderTargetBitmap rendered = new(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        JpegBitmapEncoder encoder = new() { QualityLevel = (int)(CompanionScreenCaptureLayout.JpegQuality * 100) };
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using MemoryStream output = new();
        encoder.Save(output);
        return capture with { JpegImageBytes = output.ToArray() };
    }

    /// <summary>The name or value of the control under the stroke's center, through UI Automation (300 ms at most), or null.</summary>
    public static async Task<string?> ReadTextUnderAsync(HighlightStroke stroke)
    {
        Rect bounds = BoundsOf(stroke.Points);
        Point center = stroke.Dwells.Count > 0 ? stroke.Dwells[^1] : new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread reader = new(() =>
        {
            try
            {
                AutomationElement? element = AutomationElement.FromPoint(center);
                if (element is null || element.Current.ProcessId == Environment.ProcessId) { result.TrySetResult(null); return; }
                string text = element.Current.Name;
                if (string.IsNullOrWhiteSpace(text) && element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
                    text = ((ValuePattern)valuePattern).Current.Value;
                text = text?.Trim() ?? "";
                result.TrySetResult(text.Length == 0 ? null : text.Length > 160 ? text[..160] + "…" : text);
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Highlight text not read: {exception.Message}");
                result.TrySetResult(null);
            }
        }) { IsBackground = true, Name = "Clicky highlight text" };
        reader.SetApartmentState(ApartmentState.MTA);
        reader.Start();
        return await Task.WhenAny(result.Task, Task.Delay(300)) == result.Task ? await result.Task : null;
    }
}

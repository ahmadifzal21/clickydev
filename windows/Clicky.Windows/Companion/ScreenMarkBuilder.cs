using System.Windows;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Companion;

/// <summary>
/// Turns a reply program's drawing call (screenshot pixels on one of the captured screens) into a mark in physical pixels
/// (E11 T11.9). A call with too few numbers, or for a screen that wasn't captured, gives null and is skipped.
/// </summary>
internal static class ScreenMarkBuilder
{
    public static readonly HashSet<string> DrawingCallNames = new(StringComparer.Ordinal)
    {
        "ring", "box", "underline", "highlight", "arrow", "trace", "label", "scrollhint", "card", "figure3d"
    };

    public static ScreenMark? FromCall(ProgramCall call, IReadOnlyList<CompanionScreenCapture> screenCaptures)
    {
        if (CompanionScreenQuestion.CaptureForScreen(call.ScreenNumber, screenCaptures) is not { } capture) return null;
        double scaleX = capture.CapturedMonitor.Bounds.Width / (double)capture.ScreenshotWidthPixels;
        double scaleY = capture.CapturedMonitor.Bounds.Height / (double)capture.ScreenshotHeightPixels;
        Point ToPhysical(double x, double y) => OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(capture.CapturedMonitor,
            new ScreenshotPixelCoordinate(x, y), capture.ScreenshotWidthPixels, capture.ScreenshotHeightPixels);
        double[] numbers = call.LeadingNumbers();
        string[] texts = call.Arguments.OfType<string>().ToArray();

        switch (call.Name)
        {
            case "ring" when numbers.Length >= 3:
                return new ScreenMark(ScreenMarkKind.Ring, [ToPhysical(numbers[0], numbers[1])]) { Radius = numbers[2] * scaleX, Text = call.Label };
            case "box" when numbers.Length >= 4:
                return new ScreenMark(ScreenMarkKind.Box, [ToPhysical(numbers[0], numbers[1]), ToPhysical(numbers[2], numbers[3])]) { Text = call.Label };
            case "underline" when numbers.Length >= 3:
                return new ScreenMark(ScreenMarkKind.Underline, [ToPhysical(numbers[0], numbers[1]), ToPhysical(numbers[2], numbers[1])]);
            case "highlight" when numbers.Length >= 4:
                return new ScreenMark(ScreenMarkKind.Highlight, [ToPhysical(numbers[0], numbers[1]), ToPhysical(numbers[2], numbers[3])]);
            case "arrow" when numbers.Length >= 4:
                return new ScreenMark(ScreenMarkKind.Arrow, [ToPhysical(numbers[0], numbers[1]), ToPhysical(numbers[2], numbers[3])]) { Text = call.Label };
            case "trace" when call.Numbers(0) is { Length: >= 4 } path:
                return new ScreenMark(ScreenMarkKind.Trace,
                    Enumerable.Range(0, path.Length / 2).Select(index => ToPhysical(path[index * 2], path[index * 2 + 1])).ToArray());
            case "label" when numbers.Length >= 2 && texts.Length >= 1:
                return new ScreenMark(ScreenMarkKind.Label, [ToPhysical(numbers[0], numbers[1])]) { Text = texts[0] };
            case "scrollhint" when numbers.Length >= 2:
                return new ScreenMark(ScreenMarkKind.ScrollHint, [ToPhysical(numbers[0], numbers[1])]) { Direction = texts.FirstOrDefault() ?? "down" };
            case "card" when numbers.Length >= 2 && texts.Length >= 1:
                return new ScreenMark(ScreenMarkKind.Card, [ToPhysical(numbers[0], numbers[1])])
                {
                    Title = texts.Length >= 2 ? texts[0] : "",
                    Text = texts.Length >= 2 ? texts[1] : texts[0]
                };
            case "figure3d" when numbers.Length >= 4:
                return new ScreenMark(ScreenMarkKind.Figure3D, [ToPhysical(numbers[0], numbers[1])])
                {
                    Size = new Size(numbers[2] * scaleX, numbers[3] * scaleY),
                    Title = texts.FirstOrDefault() ?? "",
                    Payload = call.Arguments.Skip(4).ToArray()
                };
            default:
                return null;
        }
    }
}

using System.Windows;
using Clicky.Windows.AI;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Companion;

/// <summary>
/// The pure halves of one "question about my screen" turn (Mac CompanionManager): building the request from the captures,
/// and turning the reply's [POINT] tag back into a physical screen position.
/// </summary>
internal static class CompanionScreenQuestion
{
    /// <summary>Labels get the screenshot size appended, as on the Mac, so the model answers in that pixel space.</summary>
    public static CompanionChatRequest BuildChatRequest(
        string userQuestion, IReadOnlyList<CompanionScreenCapture> screenCaptures, IReadOnlyList<ConversationExchange> conversationHistory) =>
        new(CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt, LabelScreenshots(screenCaptures), conversationHistory, userQuestion);

    /// <summary>The captures as the backend receives them; the same JPEG byte arrays, so a prestarted request can be matched.</summary>
    public static LabeledScreenshotImage[] LabelScreenshots(IReadOnlyList<CompanionScreenCapture> screenCaptures) =>
        screenCaptures.Select(capture => new LabeledScreenshotImage(capture.JpegImageBytes,
            $"{capture.Label} (image dimensions: {capture.ScreenshotWidthPixels}x{capture.ScreenshotHeightPixels} pixels)")).ToArray();

    /// <summary>
    /// Physical virtual-screen pixel to point at, or null for [POINT:none] / no tag. ":screenN" picks that capture (1-based, in
    /// the order sent); without it, or when N is out of range, the cursor's screen is used — the Mac's rule.
    /// </summary>
    public static Point? ResolvePointTarget(PointingParseResult pointingParseResult, IReadOnlyList<CompanionScreenCapture> screenCaptures)
    {
        if (pointingParseResult.ScreenshotCoordinate is not { } screenshotCoordinate) return null;
        CompanionScreenCapture? targetCapture =
            pointingParseResult.ScreenNumber is { } screenNumber && screenNumber >= 1 && screenNumber <= screenCaptures.Count
                ? screenCaptures[screenNumber - 1]
                : screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen);
        return targetCapture is null ? null : OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(
            targetCapture.CapturedMonitor, screenshotCoordinate, targetCapture.ScreenshotWidthPixels, targetCapture.ScreenshotHeightPixels);
    }
}

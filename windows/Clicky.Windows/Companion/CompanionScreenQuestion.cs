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

    /// <summary>
    /// The text of the turn's user message: the reason for the turn (a walkthrough step's [App] fact) before whatever the user
    /// said. A follow-up has no words of the user's. Other app lines go in the turn context.
    /// </summary>
    public static string BuildUserPrompt(string userWords, TurnTrigger trigger)
    {
        List<string> lines = [];
        if (trigger.AppFact is { Length: > 0 } appFact) lines.Add(appFact);
        if (userWords.Length > 0) lines.Add(userWords);
        return string.Join("\n", lines);
    }

    /// <summary>The captures as the backend receives them; the same JPEG byte arrays, so a prestarted request can be matched.</summary>
    public static LabeledScreenshotImage[] LabelScreenshots(IReadOnlyList<CompanionScreenCapture> screenCaptures) =>
        screenCaptures.Select(capture => new LabeledScreenshotImage(capture.JpegImageBytes,
            $"{capture.Label} (image dimensions: {capture.ScreenshotWidthPixels}x{capture.ScreenshotHeightPixels} pixels)")).ToArray();

    /// <summary>
    /// Physical virtual-screen pixel to point at, or null for [POINT:none] / no tag. ":screenN" picks that capture (1-based, in
    /// the order sent); without it, or when N is out of range, the cursor's screen is used — the Mac's rule.
    /// </summary>
    public static Point? ResolvePointTarget(PointingParseResult pointingParseResult, IReadOnlyList<CompanionScreenCapture> screenCaptures) =>
        pointingParseResult.ScreenshotCoordinate is { } screenshotCoordinate
            ? ResolveScreenshotPoint(screenshotCoordinate, pointingParseResult.ScreenNumber, screenCaptures)
            : null;

    /// <summary>A reply program's coordinate (screenshot pixels on screen <paramref name="screenNumber"/>) as a physical pixel.</summary>
    public static Point? ResolveScreenshotPoint(ScreenshotPixelCoordinate screenshotCoordinate, int? screenNumber,
        IReadOnlyList<CompanionScreenCapture> screenCaptures) =>
        CaptureForScreen(screenNumber, screenCaptures) is { } targetCapture
            ? OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(targetCapture.CapturedMonitor, screenshotCoordinate,
                targetCapture.ScreenshotWidthPixels, targetCapture.ScreenshotHeightPixels)
            : null;

    /// <summary>The capture a 1-based screen number names, or the cursor's screen without one (or when it is out of range).</summary>
    public static CompanionScreenCapture? CaptureForScreen(int? screenNumber, IReadOnlyList<CompanionScreenCapture> screenCaptures) =>
        screenNumber is { } number && number >= 1 && number <= screenCaptures.Count
            ? screenCaptures[number - 1]
            : screenCaptures.FirstOrDefault(capture => capture.IsCursorScreen);
}

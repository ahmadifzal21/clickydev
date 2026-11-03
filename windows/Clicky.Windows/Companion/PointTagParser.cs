using System.Globalization;
using System.Text.RegularExpressions;

namespace Clicky.Windows.Companion;

/// <summary>
/// A coordinate in a screenshot's pixel space (top-left origin), as the model wrote it
/// in a [POINT:x,y:...] tag. Not yet mapped to a monitor.
/// </summary>
public readonly record struct ScreenshotPixelCoordinate(double X, double Y);

/// <summary>
/// Result of parsing a [POINT:...] tag from the end of a model response.
/// Mirrors CompanionManager.PointingParseResult in Swift.
/// </summary>
/// <param name="SpokenText">The response with the tag removed. This is what gets spoken.</param>
/// <param name="ScreenshotCoordinate">Where to point, or null for [POINT:none] or no tag.</param>
/// <param name="ElementLabel">Short label such as "save button", "none" for [POINT:none], or null when there was no tag.</param>
/// <param name="ScreenNumber">1-based screen number from ":screenN", or null to use the cursor's screen.</param>
public sealed record PointingParseResult(
    string SpokenText,
    ScreenshotPixelCoordinate? ScreenshotCoordinate,
    string? ElementLabel,
    int? ScreenNumber);

/// <summary>
/// Parses [POINT:x,y:label:screenN] / [POINT:none] tags. Port of
/// CompanionManager.parsePointingCoordinates(from:). The regex is identical to the Swift one.
/// </summary>
public static class PointTagParser
{
    // Matches [POINT:none], [POINT:123,456:label], or [POINT:123,456:label:screen2],
    // only at the very end of the response (trailing whitespace allowed).
    private static readonly Regex PointTagAtEndOfResponseRegex = new(
        @"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$",
        RegexOptions.CultureInvariant);

    public static PointingParseResult ParsePointingCoordinates(string responseText)
    {
        Match pointTagMatch = PointTagAtEndOfResponseRegex.Match(responseText);
        if (!pointTagMatch.Success)
        {
            // No tag at all: speak the whole response, don't point.
            return new PointingParseResult(responseText, ScreenshotCoordinate: null, ElementLabel: null, ScreenNumber: null);
        }

        string spokenTextWithoutTag = responseText[..pointTagMatch.Index].Trim();

        Group xCoordinateGroup = pointTagMatch.Groups[1];
        Group yCoordinateGroup = pointTagMatch.Groups[2];
        bool isPointNoneTag = !xCoordinateGroup.Success || !yCoordinateGroup.Success;
        if (isPointNoneTag)
        {
            return new PointingParseResult(spokenTextWithoutTag, ScreenshotCoordinate: null, ElementLabel: "none", ScreenNumber: null);
        }

        double xCoordinate = double.Parse(xCoordinateGroup.Value, CultureInfo.InvariantCulture);
        double yCoordinate = double.Parse(yCoordinateGroup.Value, CultureInfo.InvariantCulture);

        Group elementLabelGroup = pointTagMatch.Groups[3];
        string? elementLabel = elementLabelGroup.Success ? elementLabelGroup.Value.Trim() : null;

        Group screenNumberGroup = pointTagMatch.Groups[4];
        int? screenNumber = null;
        if (screenNumberGroup.Success && int.TryParse(screenNumberGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedScreenNumber))
        {
            screenNumber = parsedScreenNumber;
        }

        return new PointingParseResult(
            spokenTextWithoutTag,
            new ScreenshotPixelCoordinate(xCoordinate, yCoordinate),
            elementLabel,
            screenNumber);
    }
}

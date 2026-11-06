using Clicky.Windows.Companion;

namespace Clicky.Windows.Tests;

public class PointTagParserTests
{
    [Fact]
    public void ResponseWithoutTag_IsSpokenWhole_AndDoesNotPoint()
    {
        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates("html is the skeleton of a web page.");

        Assert.Equal("html is the skeleton of a web page.", parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
        Assert.Null(parseResult.ElementLabel);
        Assert.Null(parseResult.ScreenNumber);
    }

    [Fact]
    public void PointNoneTag_IsStripped_AndLabelIsNone()
    {
        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates(
            "html stands for hypertext markup language. [POINT:none]");

        Assert.Equal("html stands for hypertext markup language.", parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
        Assert.Equal("none", parseResult.ElementLabel);
    }

    [Fact]
    public void CoordinateWithLabel_IsParsed()
    {
        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates(
            "click that and you'll get the color wheels. [POINT:1100,42:color inspector]");

        Assert.Equal("click that and you'll get the color wheels.", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(1100, 42), parseResult.ScreenshotCoordinate);
        Assert.Equal("color inspector", parseResult.ElementLabel);
        Assert.Null(parseResult.ScreenNumber);
    }

    [Fact]
    public void CoordinateWithLabelAndScreenNumber_IsParsed()
    {
        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates(
            "that's over on your other monitor. [POINT:400,300:terminal:screen2]");

        Assert.Equal(new ScreenshotPixelCoordinate(400, 300), parseResult.ScreenshotCoordinate);
        Assert.Equal("terminal", parseResult.ElementLabel);
        Assert.Equal(2, parseResult.ScreenNumber);
    }

    [Fact]
    public void CoordinateWithoutLabel_AndSpacesAroundComma_IsParsed()
    {
        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates("right there [POINT:10 , 20]");

        Assert.Equal(new ScreenshotPixelCoordinate(10, 20), parseResult.ScreenshotCoordinate);
        Assert.Null(parseResult.ElementLabel);
    }

    [Fact]
    public void TrailingWhitespaceAndNewlineAfterTag_StillMatches()
    {
        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates("see it? [POINT:5,6:button]  \n");

        Assert.Equal("see it?", parseResult.SpokenText);
        Assert.Equal(new ScreenshotPixelCoordinate(5, 6), parseResult.ScreenshotCoordinate);
    }

    [Fact]
    public void TagInMiddleOfResponse_IsIgnored()
    {
        // Matches the Swift regex: the tag only counts at the very end.
        const string responseWithTagInMiddle = "[POINT:5,6:button] and then some more text";

        PointingParseResult parseResult = PointTagParser.ParsePointingCoordinates(responseWithTagInMiddle);

        Assert.Equal(responseWithTagInMiddle, parseResult.SpokenText);
        Assert.Null(parseResult.ScreenshotCoordinate);
    }
}

using Clicky.Windows.AI;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Tests;

public class CompanionScreenQuestionTests
{
    // Cursor screen first, as the capture utility orders them: a 2560x1600 primary and a 1920x1080 monitor to its left.
    private static readonly CompanionScreenCapture[] Captures =
    [
        Capture("screen 1 of 2 — cursor is on this screen (primary focus)", true, new PixelRect(0, 0, 2560, 1600), 1280, 800),
        Capture("screen 2 of 2 — secondary screen", false, new PixelRect(-1920, 0, 0, 1080), 1280, 720)
    ];

    [Fact]
    public void BuildChatRequest_AppendsImageDimensionsToEachLabel()
    {
        CompanionChatRequest request = CompanionScreenQuestion.BuildChatRequest("where is save?", Captures, [new("q", "a")]);

        Assert.Equal(CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt, request.SystemPrompt);
        Assert.Equal("where is save?", request.UserPrompt);
        Assert.Equal([new ConversationExchange("q", "a")], request.ConversationHistory);
        Assert.Equal(
            [
                "screen 1 of 2 — cursor is on this screen (primary focus) (image dimensions: 1280x800 pixels)",
                "screen 2 of 2 — secondary screen (image dimensions: 1280x720 pixels)"
            ],
            request.LabeledScreenshotImages.Select(image => image.ImageLabel));
        Assert.Same(Captures[1].JpegImageBytes, request.LabeledScreenshotImages[1].JpegImageBytes);
    }

    [Theory]
    [InlineData("go here [POINT:640,400:middle]", 1280, 800)]          // no screen number -> cursor screen
    [InlineData("there [POINT:640,360:middle:screen2]", -960, 540)]     // screen 2, negative origin
    [InlineData("there [POINT:640,400:middle:screen9]", 1280, 800)]     // out of range -> cursor screen
    public void ResolvePointTarget_MapsScreenshotPixelsOntoTheRightMonitor(string reply, double expectedX, double expectedY)
    {
        var target = CompanionScreenQuestion.ResolvePointTarget(PointTagParser.ParsePointingCoordinates(reply), Captures);

        Assert.Equal(new System.Windows.Point(expectedX, expectedY), target);
    }

    [Theory]
    [InlineData("nothing to point at [POINT:none]")]
    [InlineData("no tag at all")]
    public void ResolvePointTarget_NoCoordinateMeansNoPointing(string reply) =>
        Assert.Null(CompanionScreenQuestion.ResolvePointTarget(PointTagParser.ParsePointingCoordinates(reply), Captures));

    private static CompanionScreenCapture Capture(string label, bool isCursorScreen, PixelRect bounds, int width, int height) =>
        new([0xFF, 0xD8], label, isCursorScreen, new DisplayMonitor("D", bounds, bounds, 96, 96, isCursorScreen), width, height,
            ScreenCaptureMethod.WindowsGraphicsCapture, TimeSpan.Zero);
}

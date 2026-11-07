using System.IO;

namespace Clicky.Windows.Companion;

/// <summary>
/// The system prompts. The answer prompt is Windows-only since E11 (reply programs; see Prompts/companion.md). The onboarding
/// pointing prompt is still the Mac's, verbatim, and keeps the [POINT:x,y:label:screenN] contract with PointTagParser.
/// </summary>
public static class CompanionSystemPrompts
{
    /// <summary>
    /// Prompt for every answer turn, voice or typed (E11 T11.6): the reply-program format, every call, drawing style,
    /// walkthroughs, the app's context lines and worked examples. Written for this app (not the Mac's prompt), in
    /// Companion/Prompts/companion.md, embedded in the assembly.
    /// </summary>
    public static readonly string CompanionVoiceResponseSystemPrompt = ReadEmbeddedPrompt("Clicky.Prompts.companion.md");

    private static string ReadEmbeddedPrompt(string resourceName)
    {
        using Stream stream = typeof(CompanionSystemPrompts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The prompt resource {resourceName} is missing from the build.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").Trim();
    }

    /// <summary>
    /// Prompt for point-only requests (the "pointing" job): find one concrete thing near
    /// the center of the screen and point at it with a short quip.
    /// Swift: CompanionManager.onboardingDemoSystemPrompt
    /// </summary>
    public const string OnboardingDemoPointingSystemPrompt = """
        you're clicky, a small blue cursor buddy living on the user's screen. you're showing off during onboarding — look at their screen and find ONE specific, concrete thing to point at. pick something with a clear name or identity: a specific app icon (say its name), a specific word or phrase of text you can read, a specific filename, a specific button label, a specific tab title, a specific image you can describe. do NOT point at vague things like "a window" or "some text" — be specific about exactly what you see.
        
        make a short quirky 3-6 word observation about the specific thing you picked — something fun, playful, or curious that shows you actually read/recognized it. no emojis ever. NEVER quote or repeat text you see on screen — just react to it. keep it to 6 words max, no exceptions.
        
        CRITICAL COORDINATE RULE: you MUST only pick elements near the CENTER of the screen. your x coordinate must be between 20%-80% of the image width. your y coordinate must be between 20%-80% of the image height. do NOT pick anything in the top 20%, bottom 20%, left 20%, or right 20% of the screen. no menu bar items, no dock icons, no sidebar items, no items near any edge. only things clearly in the middle area of the screen. if the only interesting things are near the edges, pick something boring in the center instead.
        
        respond with ONLY your short comment followed by the coordinate tag. nothing else. all lowercase.
        
        format: your comment [POINT:x,y:label]
        
        the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. origin (0,0) is top-left. x increases rightward, y increases downward.
        """;

    /// <summary>
    /// User prompt sent with OnboardingDemoPointingSystemPrompt. Swift: performOnboardingDemoInteraction.
    /// </summary>
    public const string OnboardingDemoPointingUserPrompt = "look around my screen and find something interesting to point at";
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Clicky.Windows.Overlay;

/// <summary>Draws the companion in one overlay's DIP space; the window decides which monitor that is.</summary>
public partial class BlueCursorView : UserControl
{
    /// <summary>The Mac draws the buddy 35 pt right of and 25 pt below the mouse so it never hides the real pointer.</summary>
    internal static readonly Vector MouseFollowOffset = new(35, 25);

    private static readonly string[] PointerPhrases =
        ["right here!", "this one!", "over here!", "click this!", "here it is!", "found it!"];

    private const double BubbleHoldSeconds = 3.0;
    private const double BubbleFadeSeconds = 0.5;
    /// <summary>Only the return flight yields to the user, and only once the mouse has clearly moved on.</summary>
    private const double ReturnFlightCancelDistanceDips = 100;

    private enum CompanionMotion { FollowingMouse, FlyingToTarget, PointingAtTarget, FlyingBackToMouse }

    private readonly CursorFollowSpring cursorFollowSpring = new();
    private readonly CursorFollowSpring bubbleScaleSpring = new(responseSeconds: 0.4, dampingFraction: 0.6);
    private CompanionMotion companionMotion = CompanionMotion.FollowingMouse;
    private Point cursorPosition;
    private PointingFlightArc? activeFlight;
    // One clock for the whole pointing sequence: every phase is driven from rendered frames, never timers.
    private double phaseElapsedSeconds;
    private string bubblePhrase = "";
    private double nextBubbleCharacterDueSeconds;
    private double? bubbleFadeStartsAtSeconds;
    private Point mouseWhenReturnFlightStarted;

    public BlueCursorView()
    {
        InitializeComponent();
    }

    /// <summary>True from take-off until the buddy is back following the mouse.</summary>
    internal bool IsPointing => companionMotion != CompanionMotion.FollowingMouse;

    /// <summary>
    /// Flies from wherever the buddy is now to beside <paramref name="targetInOverlayDips"/>, says
    /// <paramref name="bubbleText"/> (or a random pointer phrase), holds, then flies back to the mouse.
    /// A new call mid-sequence restarts the flight from the current spot, like the Mac.
    /// </summary>
    internal void PointAt(Point targetInOverlayDips, Size overlaySizeDips, string? bubbleText)
    {
        PointingBubble.Visibility = Visibility.Hidden;
        bubblePhrase = string.IsNullOrWhiteSpace(bubbleText)
            ? PointerPhrases[Random.Shared.Next(PointerPhrases.Length)]
            : bubbleText;
        StartFlight(PointingFlightArc.LandingSpotBesideTarget(targetInOverlayDips, overlaySizeDips),
            CompanionMotion.FlyingToTarget);
    }

    /// <summary>Drops any pointing sequence (e.g. another monitor took over) and resumes following from here.</summary>
    internal void CancelPointing()
    {
        if (IsPointing) ResumeFollowingMouse();
    }

    /// <summary>
    /// Called every rendered frame. While following, the spring keeps tracking even when the mouse is on another
    /// monitor so the triangle appears in the right place the moment the mouse arrives here. Only one buddy is
    /// ever visible: while another overlay is pointing, this one stays hidden.
    /// </summary>
    internal void FollowMouse(Point mouseInOverlayDips, bool isMouseOnThisMonitor, bool isAnotherOverlayPointing,
        double elapsedSeconds)
    {
        phaseElapsedSeconds += elapsedSeconds;
        switch (companionMotion)
        {
            case CompanionMotion.FollowingMouse:
                cursorPosition = cursorFollowSpring.Step(mouseInOverlayDips + MouseFollowOffset, elapsedSeconds);
                break;
            case CompanionMotion.FlyingToTarget:
                if (AdvanceFlight()) StartPointingAtTarget();
                break;
            case CompanionMotion.PointingAtTarget:
                AdvancePointingBubble(mouseInOverlayDips, elapsedSeconds);
                break;
            case CompanionMotion.FlyingBackToMouse:
                if ((mouseInOverlayDips - mouseWhenReturnFlightStarted).Length > ReturnFlightCancelDistanceDips
                    || AdvanceFlight())
                    ResumeFollowingMouse();
                break;
        }

        Canvas.SetLeft(BlueCursorTriangle, cursorPosition.X);
        Canvas.SetTop(BlueCursorTriangle, cursorPosition.Y);
        BlueCursorTriangle.Visibility = IsPointing || (isMouseOnThisMonitor && !isAnotherOverlayPointing)
            ? Visibility.Visible
            : Visibility.Hidden;
    }

    private void StartFlight(Point destination, CompanionMotion flightMotion)
    {
        activeFlight = new PointingFlightArc(cursorPosition, destination);
        phaseElapsedSeconds = 0;
        companionMotion = flightMotion;
    }

    /// <summary>Moves along the arc for this frame; returns true once landed.</summary>
    private bool AdvanceFlight()
    {
        if (activeFlight!.IsFinished(phaseElapsedSeconds))
        {
            cursorPosition = activeFlight.End;
            return true;
        }

        PointingFlightFrame flightFrame = activeFlight.FrameAt(phaseElapsedSeconds);
        cursorPosition = flightFrame.Position;
        // Direct per-frame values: drop any rest-angle easing still holding the property.
        BlueCursorTriangleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        BlueCursorTriangleRotation.Angle = flightFrame.RotationDegrees;
        SetTriangleScale(flightFrame.Scale);
        return false;
    }

    private void StartPointingAtTarget()
    {
        companionMotion = CompanionMotion.PointingAtTarget;
        phaseElapsedSeconds = 0;
        EaseTriangleBackToRest();
        PointingBubbleText.Text = "";
        nextBubbleCharacterDueSeconds = 0;
        bubbleFadeStartsAtSeconds = null;
        // Starts small and springs to full size as the first character appears.
        bubbleScaleSpring.PlaceAt(new Point(0.5, 0));
        PointingBubble.Opacity = 0;
        PointingBubble.Visibility = Visibility.Visible;
    }

    private void AdvancePointingBubble(Point mouseInOverlayDips, double elapsedSeconds)
    {
        // Variable 30-60 ms per character gives the Mac's "speaking" rhythm.
        while (PointingBubbleText.Text.Length < bubblePhrase.Length && nextBubbleCharacterDueSeconds <= phaseElapsedSeconds)
        {
            PointingBubbleText.Text = bubblePhrase[..(PointingBubbleText.Text.Length + 1)];
            nextBubbleCharacterDueSeconds += 0.03 + Random.Shared.NextDouble() * 0.03;
        }
        if (bubbleFadeStartsAtSeconds is null && PointingBubbleText.Text.Length == bubblePhrase.Length)
            bubbleFadeStartsAtSeconds = nextBubbleCharacterDueSeconds + BubbleHoldSeconds;

        double bubbleScale = bubbleScaleSpring.Step(new Point(1, 0), elapsedSeconds).X;
        PointingBubbleScale.ScaleX = PointingBubbleScale.ScaleY = bubbleScale;
        // Bright, wide glow while popping in that settles as the bubble reaches full size.
        PointingBubbleGlow.Opacity = Math.Clamp(0.5 + (1 - bubbleScale), 0, 1);
        PointingBubbleGlow.BlurRadius = Math.Max(0, 2 * (6 + (1 - bubbleScale) * 16));

        double fadeStartsAt = bubbleFadeStartsAtSeconds ?? double.PositiveInfinity;
        PointingBubble.Opacity = phaseElapsedSeconds < fadeStartsAt
            ? EaseOut(phaseElapsedSeconds / BubbleFadeSeconds)
            : 1 - EaseOut((phaseElapsedSeconds - fadeStartsAt) / BubbleFadeSeconds);
        Canvas.SetLeft(PointingBubble, cursorPosition.X + 10);
        Canvas.SetTop(PointingBubble, cursorPosition.Y + 18 - PointingBubble.ActualHeight / 2);

        if (phaseElapsedSeconds >= fadeStartsAt + BubbleFadeSeconds)
        {
            PointingBubble.Visibility = Visibility.Hidden;
            mouseWhenReturnFlightStarted = mouseInOverlayDips;
            StartFlight(mouseInOverlayDips + MouseFollowOffset, CompanionMotion.FlyingBackToMouse);
        }
    }

    private void ResumeFollowingMouse()
    {
        companionMotion = CompanionMotion.FollowingMouse;
        activeFlight = null;
        PointingBubble.Visibility = Visibility.Hidden;
        EaseTriangleBackToRest();
        // The spring picks up from where the flight left the buddy instead of jumping to the mouse.
        cursorFollowSpring.PlaceAt(cursorPosition);
    }

    /// <summary>The Mac eases the pointer angle back over 0.3 s once it is no longer flying.</summary>
    private void EaseTriangleBackToRest()
    {
        SetTriangleScale(1);
        BlueCursorTriangleRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(PointingFlightArc.RestingRotationDegrees, TimeSpan.FromSeconds(0.3))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    /// <summary>The triangle swells mid-flight and its glow (Mac radius 8 + 20 per unit of growth) brightens with it.</summary>
    private void SetTriangleScale(double scale)
    {
        BlueCursorTriangleScale.ScaleX = BlueCursorTriangleScale.ScaleY = scale;
        BlueCursorTriangleGlow.BlurRadius = 2 * (8 + (scale - 1) * 20);
    }

    private static double EaseOut(double progress)
    {
        double clampedProgress = Math.Clamp(progress, 0, 1);
        return 1 - (1 - clampedProgress) * (1 - clampedProgress);
    }
}

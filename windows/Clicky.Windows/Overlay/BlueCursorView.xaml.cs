using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Clicky.Windows.Companion;

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

    private static readonly double[] ListeningBarProfile = [0.4, 0.7, 1.0, 0.7, 0.4];
    private const double ResponseBubbleHoldSeconds = 6.0;
    private const double ResponseBubbleFadeSeconds = 0.4;
    private CompanionVoiceState voiceState = CompanionVoiceState.Idle;
    private double viewClockSeconds;
    private double targetAudioPowerLevel;
    private double displayedAudioPowerLevel;
    private string? responseText;
    private double? responseBubbleFadeStartsAtSeconds;

    public BlueCursorView()
    {
        InitializeComponent();
        BuildProcessingSpinnerArc();
    }

    /// <summary>This monitor's work area in overlay DIPs; the response bubble flips and clamps inside it like the Mac panel.</summary>
    internal Rect WorkAreaInOverlayDips { get; set; } = Rect.Empty;

    /// <summary>
    /// The Mac cross-fades in place instead of swapping elements so the buddy never "pops": triangle while idle or
    /// speaking, waveform while listening, spinner while processing.
    /// </summary>
    internal void SetVoiceState(CompanionVoiceState newVoiceState)
    {
        voiceState = newVoiceState;
        bool isTriangleState = newVoiceState is CompanionVoiceState.Idle or CompanionVoiceState.Responding;
        FadeTo(BlueCursorTriangle, isTriangleState ? 1 : 0, 0.25);
        FadeTo(ListeningWaveform, newVoiceState == CompanionVoiceState.Listening ? 1 : 0, 0.15);
        FadeTo(ProcessingSpinner, newVoiceState == CompanionVoiceState.Processing ? 1 : 0, 0.15);
    }

    /// <summary>Microphone power in 0..1 (the Mac's normalised level); bars ease toward it over ~80 ms.</summary>
    internal void SetAudioPowerLevel(double audioPowerLevel) => targetAudioPowerLevel = audioPowerLevel;

    /// <summary>Shows (or updates) the streamed reply beside the mouse; "..." until the first text arrives.</summary>
    internal void ShowResponseText(string accumulatedResponseText)
    {
        responseText = accumulatedResponseText;
        responseBubbleFadeStartsAtSeconds = null;
        ResponseBubbleText.Text = accumulatedResponseText.Length == 0 ? "..." : accumulatedResponseText;
        ResponseBubble.Opacity = 1;
    }

    /// <summary>Keeps the finished reply up long enough to read the last chunk, then fades it out.</summary>
    internal void FinishResponse()
    {
        if (responseText is not null) responseBubbleFadeStartsAtSeconds = viewClockSeconds + ResponseBubbleHoldSeconds;
    }

    internal void HideResponse()
    {
        responseText = null;
        responseBubbleFadeStartsAtSeconds = null;
        ResponseBubble.Visibility = Visibility.Hidden;
    }

    /// <summary>True from the first reply text until the bubble has faded out.</summary>
    internal bool IsShowingResponse => responseText is not null;

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
        viewClockSeconds += elapsedSeconds;
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

        Visibility buddyVisibility = IsPointing || (isMouseOnThisMonitor && !isAnotherOverlayPointing)
            ? Visibility.Visible
            : Visibility.Hidden;
        Canvas.SetLeft(BlueCursorTriangle, cursorPosition.X);
        Canvas.SetTop(BlueCursorTriangle, cursorPosition.Y);
        BlueCursorTriangle.Visibility = buddyVisibility;
        Canvas.SetLeft(ListeningWaveform, cursorPosition.X - ListeningWaveform.ActualWidth / 2);
        Canvas.SetTop(ListeningWaveform, cursorPosition.Y - ListeningWaveform.Height / 2);
        ListeningWaveform.Visibility = buddyVisibility;
        Canvas.SetLeft(ProcessingSpinner, cursorPosition.X);
        Canvas.SetTop(ProcessingSpinner, cursorPosition.Y);
        ProcessingSpinner.Visibility = buddyVisibility;

        // Animate only the state on screen so an idle overlay produces no per-frame changes.
        if (voiceState == CompanionVoiceState.Listening) UpdateListeningWaveformBars(elapsedSeconds);
        if (voiceState == CompanionVoiceState.Processing)
            ProcessingSpinnerRotation.Angle = viewClockSeconds / 0.8 * 360 % 360;
        UpdateResponseBubble(mouseInOverlayDips, isMouseOnThisMonitor);
    }

    /// <summary>
    /// The Mac panel sits 22 right / 6 below the mouse, flips left at the right edge, goes above the mouse at the
    /// bottom edge, then clamps inside the work area.
    /// </summary>
    internal static Point ResponseBubbleTopLeft(Point mouseInOverlayDips, Size bubbleSize, Rect workAreaInOverlayDips)
    {
        const double cursorOffsetX = 22;
        const double cursorOffsetY = 6;
        double left = mouseInOverlayDips.X + cursorOffsetX;
        double top = mouseInOverlayDips.Y + cursorOffsetY;
        if (workAreaInOverlayDips.IsEmpty) return new Point(left, top);
        if (left + bubbleSize.Width > workAreaInOverlayDips.Right)
            left = mouseInOverlayDips.X - cursorOffsetX - bubbleSize.Width;
        if (top + bubbleSize.Height > workAreaInOverlayDips.Bottom)
            top = mouseInOverlayDips.Y - cursorOffsetY - bubbleSize.Height;
        left = Math.Max(workAreaInOverlayDips.Left, Math.Min(left, workAreaInOverlayDips.Right - bubbleSize.Width));
        top = Math.Max(workAreaInOverlayDips.Top, Math.Min(top, workAreaInOverlayDips.Bottom - bubbleSize.Height));
        return new Point(left, top);
    }

    private void UpdateResponseBubble(Point mouseInOverlayDips, bool isMouseOnThisMonitor)
    {
        if (responseText is null) return;
        if (responseBubbleFadeStartsAtSeconds is double fadeStartsAt && viewClockSeconds >= fadeStartsAt)
        {
            if (viewClockSeconds >= fadeStartsAt + ResponseBubbleFadeSeconds)
            {
                HideResponse();
                return;
            }
            ResponseBubble.Opacity = 1 - (viewClockSeconds - fadeStartsAt) / ResponseBubbleFadeSeconds;
        }
        // The Mac keeps one panel glued to the mouse, so only the mouse's monitor draws it.
        ResponseBubble.Visibility = isMouseOnThisMonitor ? Visibility.Visible : Visibility.Hidden;
        Point bubbleTopLeft = ResponseBubbleTopLeft(mouseInOverlayDips,
            new Size(ResponseBubble.ActualWidth, ResponseBubble.ActualHeight), WorkAreaInOverlayDips);
        Canvas.SetLeft(ResponseBubble, bubbleTopLeft.X);
        Canvas.SetTop(ResponseBubble, bubbleTopLeft.Y);
    }

    /// <summary>Mac <c>BlueCursorWaveformView.barHeight</c>: 3 DIPs base + voice-reactive height + a gentle idle pulse.</summary>
    internal static double ListeningBarHeight(int barIndex, double audioPowerLevel, double clockSeconds)
    {
        double animationPhase = clockSeconds * 3.6 + barIndex * 0.35;
        double normalizedAudioPowerLevel = Math.Max(audioPowerLevel - 0.008, 0);
        double easedAudioPowerLevel = Math.Pow(Math.Min(normalizedAudioPowerLevel * 2.85, 1), 0.76);
        double reactiveHeight = easedAudioPowerLevel * 10 * ListeningBarProfile[barIndex];
        double idlePulse = (Math.Sin(animationPhase) + 1) / 2 * 1.5;
        return 3 + reactiveHeight + idlePulse;
    }

    private void UpdateListeningWaveformBars(double elapsedSeconds)
    {
        displayedAudioPowerLevel += (targetAudioPowerLevel - displayedAudioPowerLevel) * Math.Min(1, elapsedSeconds / 0.08);
        for (int barIndex = 0; barIndex < ListeningWaveform.Children.Count; barIndex++)
            ((Rectangle)ListeningWaveform.Children[barIndex]).Height =
                ListeningBarHeight(barIndex, displayedAudioPowerLevel, viewClockSeconds);
    }

    /// <summary>
    /// WPF has no angular gradient, so the Mac's trimmed circle (0.15..0.85, clear to blue) is drawn as short arcs
    /// whose opacity follows the gradient. Flat inner caps keep the joints from doubling up.
    /// </summary>
    private void BuildProcessingSpinnerArc()
    {
        const int segmentCount = 24;
        const double radius = (14 - 2.5) / 2;
        const double trimStart = 0.15;
        const double trimEnd = 0.85;
        Brush spinnerBrush = (Brush)FindResource("DS.OverlayCursorBlue");
        static Point PointOnCircle(double fraction) =>
            new(radius * Math.Cos(fraction * 2 * Math.PI), radius * Math.Sin(fraction * 2 * Math.PI));

        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            double startFraction = trimStart + (trimEnd - trimStart) * segmentIndex / segmentCount;
            double endFraction = trimStart + (trimEnd - trimStart) * (segmentIndex + 1) / segmentCount;
            PathFigure arcFigure = new() { StartPoint = PointOnCircle(startFraction) };
            arcFigure.Segments.Add(new ArcSegment(PointOnCircle(endFraction), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true));
            ProcessingSpinner.Children.Add(new Path
            {
                Data = new PathGeometry([arcFigure]),
                Stroke = spinnerBrush,
                StrokeThickness = 2.5,
                Opacity = (startFraction + endFraction) / 2,
                StrokeStartLineCap = segmentIndex == 0 ? PenLineCap.Round : PenLineCap.Flat,
                StrokeEndLineCap = segmentIndex == segmentCount - 1 ? PenLineCap.Round : PenLineCap.Flat,
            });
        }
    }

    private static void FadeTo(UIElement element, double targetOpacity, double seconds) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        });

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

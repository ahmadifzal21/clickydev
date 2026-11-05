using System.Windows;

namespace Clicky.Windows.Overlay;

/// <summary>
/// The Mac cursor trails the mouse with SwiftUI's <c>.spring(response: 0.2, dampingFraction: 0.6)</c>, which gives
/// the slight lag and overshoot that makes it feel alive. WPF has no spring animation, so this integrates the same
/// unit-mass spring once per rendered frame.
/// </summary>
internal sealed class CursorFollowSpring
{
    private const double ResponseSeconds = 0.2;
    private const double DampingFraction = 0.6;
    private const double MaximumSubstepSeconds = 1.0 / 240.0;
    // A stalled frame (debugger, sleep) must not fling the cursor across the screen.
    private const double MaximumElapsedSeconds = 0.1;
    private const double RestDistance = 0.01;

    private static readonly double Stiffness = Math.Pow(2 * Math.PI / ResponseSeconds, 2);
    private static readonly double Damping = 4 * Math.PI * DampingFraction / ResponseSeconds;

    private Vector velocity;
    private bool hasPosition;

    public Point Position { get; private set; }

    /// <summary>Advances toward <paramref name="target"/>; the first call places the cursor there directly.</summary>
    public Point Step(Point target, double elapsedSeconds)
    {
        if (!hasPosition)
        {
            hasPosition = true;
            Position = target;
            return Position;
        }

        double remainingSeconds = Math.Clamp(elapsedSeconds, 0, MaximumElapsedSeconds);
        while (remainingSeconds > 0)
        {
            double substepSeconds = Math.Min(remainingSeconds, MaximumSubstepSeconds);
            velocity += ((target - Position) * Stiffness - velocity * Damping) * substepSeconds;
            Position += velocity * substepSeconds;
            remainingSeconds -= substepSeconds;
        }

        // Snap once settled so an idle mouse stops producing new layout values (and repaints).
        if ((target - Position).Length < RestDistance && velocity.Length < RestDistance)
        {
            Position = target;
            velocity = default;
        }
        return Position;
    }
}

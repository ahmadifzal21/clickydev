using System.Windows;

namespace Clicky.Windows.Overlay;

/// <summary>Where the triangle is, which way it faces, and how big it is at one moment of a flight.</summary>
internal readonly record struct PointingFlightFrame(Point Position, double RotationDegrees, double Scale);

/// <summary>
/// The Mac's <c>animateBezierFlightArc</c>: a quadratic bezier that bows upward, eased with smoothstep, the
/// triangle turned along the curve's tangent and swelling to 1.3x mid-flight. All values are overlay DIPs.
/// </summary>
internal sealed class PointingFlightArc
{
    /// <summary>The Mac's resting pointer angle; the triangle returns to it whenever it is not flying.</summary>
    public const double RestingRotationDegrees = -35;

    private readonly Point controlPoint;

    public PointingFlightArc(Point start, Point end)
    {
        Start = start;
        End = end;
        double distance = (end - start).Length;
        // Short hops are quick, long flights more dramatic.
        DurationSeconds = Math.Clamp(distance / 800.0, 0.6, 1.4);
        double arcHeight = Math.Min(distance * 0.2, 80.0);
        controlPoint = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2 - arcHeight);
    }

    public Point Start { get; }
    public Point End { get; }
    public double DurationSeconds { get; }

    public bool IsFinished(double elapsedSeconds) => elapsedSeconds >= DurationSeconds;

    public PointingFlightFrame FrameAt(double elapsedSeconds)
    {
        if (IsFinished(elapsedSeconds)) return new PointingFlightFrame(End, RestingRotationDegrees, 1);

        double linearProgress = Math.Max(elapsedSeconds, 0) / DurationSeconds;
        double t = linearProgress * linearProgress * (3 - 2 * linearProgress);
        double oneMinusT = 1 - t;
        Point position = new(
            oneMinusT * oneMinusT * Start.X + 2 * oneMinusT * t * controlPoint.X + t * t * End.X,
            oneMinusT * oneMinusT * Start.Y + 2 * oneMinusT * t * controlPoint.Y + t * t * End.Y);
        Vector tangent = 2 * oneMinusT * (controlPoint - Start) + 2 * t * (End - controlPoint);
        // +90 because the triangle's tip points up at 0 degrees while atan2 measures from the +X axis.
        double rotationDegrees = Math.Atan2(tangent.Y, tangent.X) * 180 / Math.PI + 90;
        double scale = 1 + Math.Sin(linearProgress * Math.PI) * 0.3;
        return new PointingFlightFrame(position, rotationDegrees, scale);
    }

    /// <summary>The Mac lands 8 right / 12 below the target so the buddy sits beside it, kept 20 DIPs inside the overlay.</summary>
    public static Point LandingSpotBesideTarget(Point targetInOverlayDips, Size overlaySizeDips) =>
        new(Math.Clamp(targetInOverlayDips.X + 8, 20, Math.Max(20, overlaySizeDips.Width - 20)),
            Math.Clamp(targetInOverlayDips.Y + 12, 20, Math.Max(20, overlaySizeDips.Height - 20)));
}

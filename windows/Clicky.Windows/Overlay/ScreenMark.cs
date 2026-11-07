using System.Windows;

namespace Clicky.Windows.Overlay;

internal enum ScreenMarkKind
{
    Ring,
    Box,
    Underline,
    Highlight,
    Arrow,
    Trace,
    Label,
    ScrollHint,
    Card,
    TargetRing,
    Figure3D
}

/// <summary>
/// One mark to draw on the overlay (E11 T11.9), in physical virtual-screen pixels; the overlay of the monitor holding
/// <see cref="Anchor"/> draws it. Points: Ring/Label/ScrollHint/Card/Figure3D one, Box/Highlight/Underline/Arrow two
/// (start and end, or top-left and bottom-right), Trace any number.
/// </summary>
internal sealed record ScreenMark(ScreenMarkKind Kind, IReadOnlyList<Point> Points)
{
    /// <summary>Ring and target-ring radius, in physical pixels.</summary>
    public double Radius { get; init; }

    /// <summary>Label text, a card's body, or an arrow's or ring's label.</summary>
    public string? Text { get; init; }

    /// <summary>A card's or 3D figure's title.</summary>
    public string? Title { get; init; }

    /// <summary>Scroll hint direction: up, down, left or right.</summary>
    public string? Direction { get; init; }

    /// <summary>A 3D figure's card size, in physical pixels.</summary>
    public Size Size { get; init; }

    /// <summary>A 3D figure's parts and options, as the reply program gave them.</summary>
    public object? Payload { get; init; }

    public Point Anchor => Points[0];
}

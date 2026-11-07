using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Clicky.Windows.Overlay;

/// <summary>
/// The marks Clicky draws over one monitor (E11 T11.8-T11.10): hand-drawn rings, boxes, underlines, arrows and traces that
/// draw themselves on, a highlighter wash, labels, scroll hints and cards. Coordinates are this overlay's DIPs. Lives under
/// the blue cursor in the click-through, capture-excluded overlay window, so it never blocks input or shows in screenshots.
/// </summary>
internal sealed partial class ScreenDrawingLayer : Canvas
{
    // The design system's cursor blue, surface and text (Tray/ClickyDesignSystem.xaml), so marks match the buddy.
    internal static readonly Color InkColor = Color.FromRgb(0x33, 0x80, 0xFF);
    private static readonly Color SurfaceColor = Color.FromRgb(0x17, 0x19, 0x18);
    private static readonly Color TextColor = Color.FromRgb(0xEC, 0xEE, 0xED);
    private const double StrokeThickness = 4;
    private const double DrawOnDipsPerSecond = 1400;

    private readonly Dictionary<UIElement, ScreenMarkKind> markKinds = [];
    private Polyline? highlightTrail;

    public ScreenDrawingLayer()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    public bool HasMarks => markKinds.Count > 0;

    /// <summary>How many marks of each kind are on screen; tests read it.</summary>
    public IReadOnlyList<ScreenMarkKind> MarkKinds => Children.OfType<UIElement>().Where(markKinds.ContainsKey).Select(element => markKinds[element]).ToArray();

    public bool HasHighlightTrail => highlightTrail is not null;

    /// <summary>The user's highlighter trail while they talk (E11 T11.23), in DIPs; not a mark, so it doesn't hold the overlay up.</summary>
    public void SetHighlightTrail(IReadOnlyList<Point> points)
    {
        if (highlightTrail is null)
        {
            highlightTrail = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x8C, InkColor.R, InkColor.G, InkColor.B)),
                StrokeThickness = 8,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            Children.Add(highlightTrail);
        }
        highlightTrail.Points = new PointCollection(points);
    }

    public void ClearHighlightTrail()
    {
        if (highlightTrail is null) return;
        Children.Remove(highlightTrail);
        highlightTrail = null;
    }

    /// <summary>Draws <paramref name="mark"/>, whose points are already in this overlay's DIPs; the radius too.</summary>
    public void Add(ScreenMark mark)
    {
        UIElement? element = mark.Kind switch
        {
            ScreenMarkKind.Ring => DrawnStroke(RingPoints(mark.Anchor, Math.Max(10, mark.Radius), Seed(mark)), closedOvershoot: true),
            ScreenMarkKind.TargetRing => TargetRing(mark.Anchor, Math.Max(14, mark.Radius)),
            ScreenMarkKind.Box when mark.Points.Count >= 2 => DrawnStroke(BoxPoints(mark.Points[0], mark.Points[1], Seed(mark)), closedOvershoot: true),
            ScreenMarkKind.Underline when mark.Points.Count >= 2 => DrawnStroke(UnderlinePoints(mark.Points[0], mark.Points[1], Seed(mark)), closedOvershoot: false),
            ScreenMarkKind.Highlight when mark.Points.Count >= 2 => HighlighterWash(mark.Points[0], mark.Points[1]),
            ScreenMarkKind.Arrow when mark.Points.Count >= 2 => Arrow(mark.Points[0], mark.Points[1]),
            ScreenMarkKind.Trace when mark.Points.Count >= 2 => DrawnStroke(SmoothPoints(mark.Points), closedOvershoot: false),
            ScreenMarkKind.Label => Label(mark.Anchor, mark.Text ?? ""),
            ScreenMarkKind.ScrollHint => ScrollHint(mark.Anchor, mark.Direction ?? "down"),
            ScreenMarkKind.Card => Card(mark.Anchor, mark.Title ?? "", mark.Text ?? ""),
            ScreenMarkKind.Figure3D => Figure3DCard.Create(mark),
            _ => null
        };
        if (element is null) return;
        markKinds[element] = mark.Kind;
        Children.Add(element);
        // A ring, box or arrow can carry a short name too, drawn just outside it.
        if (mark.Kind is ScreenMarkKind.Ring or ScreenMarkKind.Box or ScreenMarkKind.Arrow && !string.IsNullOrWhiteSpace(mark.Text))
        {
            Point labelSpot = mark.Kind switch
            {
                ScreenMarkKind.Ring => new Point(mark.Anchor.X + mark.Radius * 0.8, mark.Anchor.Y + mark.Radius * 0.8),
                ScreenMarkKind.Arrow => new Point(mark.Points[0].X + 6, mark.Points[0].Y + 8), // At the tail: the head points elsewhere.
                _ => new Point(Math.Max(mark.Points[0].X, mark.Points[1].X) + 6, Math.Max(mark.Points[0].Y, mark.Points[1].Y) + 4)
            };
            UIElement nameTag = Label(labelSpot, mark.Text!);
            markKinds[nameTag] = ScreenMarkKind.Label;
            Children.Add(nameTag);
        }
    }

    /// <summary>Removes every mark of <paramref name="kind"/> (the target ring when its step is done).</summary>
    public void Remove(ScreenMarkKind kind)
    {
        foreach (UIElement element in markKinds.Where(mark => mark.Value == kind).Select(mark => mark.Key).ToArray())
        {
            Children.Remove(element);
            markKinds.Remove(element);
        }
    }

    public void Clear()
    {
        foreach (UIElement mark in markKinds.Keys) Children.Remove(mark);
        markKinds.Clear();
    }

    private static int Seed(ScreenMark mark) => HashCode.Combine(mark.Kind, (int)mark.Anchor.X, (int)mark.Anchor.Y);

    /// <summary>A pen stroke through <paramref name="points"/> that draws itself on, like someone drawing it right now.</summary>
    private static Path DrawnStroke(IReadOnlyList<Point> points, bool closedOvershoot)
    {
        PathFigure figure = new() { StartPoint = points[0], IsClosed = false, IsFilled = false };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), isStroked: true));
        Path path = InkPath(new PathGeometry([figure]));
        AnimateDrawOn(path, PolylineLength(points));
        return path;
    }

    private static Path InkPath(Geometry geometry) => new()
    {
        Data = geometry,
        Stroke = new SolidColorBrush(InkColor),
        StrokeThickness = StrokeThickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Effect = new DropShadowEffect { Color = InkColor, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.55 }
    };

    private static void AnimateDrawOn(Shape shape, double lengthDips)
    {
        double dashLength = lengthDips / StrokeThickness + 1; // Dash units are multiples of the stroke thickness.
        shape.StrokeDashArray = [dashLength, dashLength];
        shape.StrokeDashOffset = dashLength;
        DoubleAnimation drawOn = new(dashLength, 0, TimeSpan.FromSeconds(Math.Clamp(lengthDips / DrawOnDipsPerSecond, 0.2, 0.7)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        drawOn.Completed += (_, _) =>
        {
            shape.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            shape.StrokeDashArray = null; // A finished stroke is a plain line; dashes would show seams when it moves.
        };
        shape.BeginAnimation(Shape.StrokeDashOffsetProperty, drawOn);
    }

    internal static List<Point> RingPoints(Point center, double radius, int seed)
    {
        Random wobble = new(seed);
        double start = wobble.NextDouble() * Math.PI * 2;
        double phase = wobble.NextDouble() * Math.PI * 2;
        const int segments = 64;
        double sweep = Math.PI * 2 + 0.35; // A hand-drawn circle overshoots where it started.
        List<Point> points = [];
        for (int index = 0; index <= segments; index++)
        {
            double angle = start + sweep * index / segments;
            double progress = (double)index / segments;
            double wobbleRadius = radius * (1 + 0.035 * Math.Sin(3 * angle + phase)) + progress * radius * 0.06;
            points.Add(new Point(center.X + wobbleRadius * Math.Cos(angle), center.Y + wobbleRadius * Math.Sin(angle)));
        }
        return points;
    }

    internal static List<Point> BoxPoints(Point first, Point second, int seed)
    {
        Random wobble = new(seed);
        const double padding = 5;
        double left = Math.Min(first.X, second.X) - padding, right = Math.Max(first.X, second.X) + padding;
        double top = Math.Min(first.Y, second.Y) - padding, bottom = Math.Max(first.Y, second.Y) + padding;
        double corner = Math.Min(10, Math.Min(right - left, bottom - top) / 3);
        List<Point> points = [];
        void Arc(double centerX, double centerY, double fromAngle)
        {
            for (int step = 0; step <= 6; step++)
            {
                double angle = fromAngle + Math.PI / 2 * step / 6;
                points.Add(new Point(centerX + corner * Math.Cos(angle), centerY + corner * Math.Sin(angle)));
            }
        }
        Arc(left + corner, top + corner, Math.PI);
        Arc(right - corner, top + corner, -Math.PI / 2);
        Arc(right - corner, bottom - corner, 0);
        Arc(left + corner, bottom - corner, Math.PI / 2);
        points.Add(new Point(left + wobble.NextDouble() * 2, top + corner + 2)); // Closes a little past the start, by hand.
        points.Add(new Point(left + corner + 6, top - 1 + wobble.NextDouble() * 2));
        return points;
    }

    internal static List<Point> UnderlinePoints(Point start, Point end, int seed)
    {
        Random wobble = new(seed);
        double phase = wobble.NextDouble() * Math.PI;
        List<Point> points = [];
        for (int index = 0; index <= 24; index++)
        {
            double progress = index / 24.0;
            points.Add(new Point(start.X + (end.X - start.X) * progress,
                start.Y + (end.Y - start.Y) * progress + 1.4 * Math.Sin(progress * Math.PI * 3 + phase)));
        }
        return points;
    }

    /// <summary>A Catmull-Rom curve through the points, so a trace looks drawn rather than plotted.</summary>
    internal static List<Point> SmoothPoints(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return [.. points];
        List<Point> smooth = [points[0]];
        for (int index = 0; index < points.Count - 1; index++)
        {
            Point before = points[Math.Max(0, index - 1)], from = points[index], to = points[index + 1], after = points[Math.Min(points.Count - 1, index + 2)];
            for (int step = 1; step <= 8; step++)
            {
                double t = step / 8.0, t2 = t * t, t3 = t2 * t;
                smooth.Add(new Point(
                    0.5 * (2 * from.X + (-before.X + to.X) * t + (2 * before.X - 5 * from.X + 4 * to.X - after.X) * t2 + (-before.X + 3 * from.X - 3 * to.X + after.X) * t3),
                    0.5 * (2 * from.Y + (-before.Y + to.Y) * t + (2 * before.Y - 5 * from.Y + 4 * to.Y - after.Y) * t2 + (-before.Y + 3 * from.Y - 3 * to.Y + after.Y) * t3)));
            }
        }
        return smooth;
    }

    private static UIElement HighlighterWash(Point first, Point second)
    {
        Rectangle wash = new()
        {
            Width = Math.Max(4, Math.Abs(second.X - first.X) + 6),
            Height = Math.Max(4, Math.Abs(second.Y - first.Y) + 4),
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x55, InkColor.R, InkColor.G, InkColor.B)),
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new ScaleTransform(0, 1)
        };
        SetLeft(wash, Math.Min(first.X, second.X) - 3);
        SetTop(wash, Math.Min(first.Y, second.Y) - 2);
        ((ScaleTransform)wash.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        return wash;
    }

    private static UIElement Arrow(Point start, Point end)
    {
        double length = (end - start).Length;
        if (length < 1) return DrawnStroke([start, end], closedOvershoot: false);
        Vector direction = (end - start) / length;
        Vector normal = new(-direction.Y, direction.X);
        Point control = start + (end - start) / 2 + normal * length * 0.08; // A slight curve, as a hand would draw it.
        List<Point> shaft = [];
        for (int step = 0; step <= 20; step++)
        {
            double t = step / 20.0;
            shaft.Add(new Point((1 - t) * (1 - t) * start.X + 2 * (1 - t) * t * control.X + t * t * end.X,
                (1 - t) * (1 - t) * start.Y + 2 * (1 - t) * t * control.Y + t * t * end.Y));
        }
        Vector tip = end - shaft[^3];
        tip.Normalize();
        double headLength = Math.Min(18, length * 0.4);
        Point leftBarb = end - Rotate(tip, 28) * headLength;
        Point rightBarb = end - Rotate(tip, -28) * headLength;
        List<Point> stroke = [.. shaft, leftBarb, end, rightBarb];
        return DrawnStroke(stroke, closedOvershoot: false);
    }

    private static Vector Rotate(Vector vector, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new Vector(vector.X * Math.Cos(radians) - vector.Y * Math.Sin(radians), vector.X * Math.Sin(radians) + vector.Y * Math.Cos(radians));
    }

    private static UIElement Label(Point at, string text)
    {
        Border label = new()
        {
            Background = new SolidColorBrush(InkColor),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 4),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold }
        };
        SetLeft(label, at.X);
        SetTop(label, at.Y);
        FadeIn(label, slideDips: 4);
        return label;
    }

    private static UIElement ScrollHint(Point at, string direction)
    {
        double angle = direction.ToLowerInvariant() switch { "up" => 180, "left" => 90, "right" => -90, _ => 0 };
        GeometryGroup chevrons = new();
        for (int index = 0; index < 3; index++)
        {
            double y = index * 12;
            chevrons.Children.Add(new PathGeometry([new PathFigure(new Point(-12, y), [new PolyLineSegment([new Point(0, y + 10), new Point(12, y)], true)], false)]));
        }
        Path path = InkPath(chevrons);
        TranslateTransform bounce = new();
        path.RenderTransform = new TransformGroup { Children = [bounce, new RotateTransform(angle), new TranslateTransform(at.X, at.Y - 16)] };
        bounce.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 8, TimeSpan.FromSeconds(0.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        return path;
    }

    private static UIElement Card(Point at, string title, string body)
    {
        StackPanel content = new();
        if (title.Length > 0)
            content.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextColor), Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        if (body.Length > 0) content.Children.Add(RichText(body, 12.5));
        Border card = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, SurfaceColor.R, SurfaceColor.G, SurfaceColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, InkColor.R, InkColor.G, InkColor.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 12),
            MaxWidth = 340,
            Child = content,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 24, ShadowDepth = 6, Direction = 270 }
        };
        SetLeft(card, at.X);
        SetTop(card, at.Y);
        FadeIn(card, slideDips: 8);
        return card;
    }

    /// <summary>Card text: lines from \n, "- " lines as bullets, **bold** spans. Nothing else is interpreted.</summary>
    internal static TextBlock RichText(string text, double fontSize)
    {
        TextBlock block = new() { FontSize = fontSize, Foreground = new SolidColorBrush(TextColor), TextWrapping = TextWrapping.Wrap, LineHeight = fontSize * 1.45 };
        string[] lines = text.Replace("\\n", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd();
            if (index > 0) block.Inlines.Add(new LineBreak());
            if (line.TrimStart().StartsWith("- ")) line = "•  " + line.TrimStart()[2..];
            string[] parts = BoldRegex().Split(line); // Odd parts were inside ** **.
            for (int part = 0; part < parts.Length; part++)
                if (parts[part].Length > 0) block.Inlines.Add(new Run(parts[part]) { FontWeight = part % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        }
        return block;
    }

    /// <summary>The ring a guided step waits on: drawn on, then breathing gently until the user clicks inside it.</summary>
    private static UIElement TargetRing(Point center, double radius)
    {
        Ellipse ring = new()
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = new SolidColorBrush(InkColor),
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x1A, InkColor.R, InkColor.G, InkColor.B)),
            Effect = new DropShadowEffect { Color = InkColor, ShadowDepth = 0, BlurRadius = 14, Opacity = 0.7 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };
        SetLeft(ring, center.X - radius);
        SetTop(ring, center.Y - radius);
        AnimateDrawOn(ring, 2 * Math.PI * radius);
        DoubleAnimation breathe = new(1, 1.08, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        return ring;
    }

    private static void FadeIn(FrameworkElement element, double slideDips)
    {
        TranslateTransform slide = new(0, slideDips);
        element.RenderTransform = slide;
        element.Opacity = 0;
        TimeSpan duration = TimeSpan.FromSeconds(0.25);
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slideDips, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static double PolylineLength(IReadOnlyList<Point> points)
    {
        double length = 0;
        for (int index = 1; index < points.Count; index++) length += (points[index] - points[index - 1]).Length;
        return length;
    }

    [GeneratedRegex(@"\*\*")]
    private static partial Regex BoldRegex();
}

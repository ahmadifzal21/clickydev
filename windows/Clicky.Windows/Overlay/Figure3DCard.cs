using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace Clicky.Windows.Overlay;

/// <summary>
/// A small 3D figure on the overlay (E11 T11.30): <c>figure3d(x, y, width, height, "title", [parts], {spin: true})</c>. Parts are
/// boxes, spheres, cylinders, cones and arrows with a position, size and colour; the camera frames them all, two lights shade
/// them, and with spin they turn slowly. Labels are listed under the view with each part's colour (text in 3D reads badly).
/// Built from data only; at most 40 parts.
/// </summary>
internal static class Figure3DCard
{
    private const int MaximumParts = 40;
    private static readonly Color DefaultColor = Color.FromRgb(0x4F, 0x8C, 0xFF);

    internal sealed record Part(string Shape, Point3D At, double[] Size, Point3D? From, Point3D? To, Color Color, string? Label);

    public static UIElement Create(ScreenMark mark)
    {
        object?[] payload = mark.Payload as object?[] ?? [];
        object?[] partValues = payload.OfType<object?[]>().FirstOrDefault() ?? [];
        Dictionary<string, object?> options = payload.OfType<Dictionary<string, object?>>().FirstOrDefault() ?? [];
        Part[] parts = partValues.OfType<Dictionary<string, object?>>().Take(MaximumParts).Select(ReadPart).OfType<Part>().ToArray();
        bool spin = options.TryGetValue("spin", out object? spinValue) && spinValue is true;
        double width = Math.Max(160, mark.Size.Width), height = Math.Max(120, mark.Size.Height);

        Viewport3D viewport = new() { Width = width, Height = height, ClipToBounds = true };
        Model3DGroup models = new();
        models.Children.Add(new AmbientLight(Color.FromRgb(0x5A, 0x5A, 0x5A)));
        models.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.6, -1, -0.8)));
        Model3DGroup shapes = new();
        foreach (Part part in parts) shapes.Children.Add(BuildModel(part));
        models.Children.Add(shapes);
        ModelVisual3D visual = new() { Content = models };
        viewport.Children.Add(visual);

        Rect3D bounds = shapes.Children.Count > 0 ? shapes.Bounds : new Rect3D(-1, -1, -1, 2, 2, 2);
        Point3D center = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double radius = Math.Max(0.5, Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2);
        Vector3D viewDirection = new(-0.55, -0.45, -1);
        viewDirection.Normalize();
        double distance = radius / Math.Tan(Math.PI * 40 / 360) * 1.15;
        viewport.Camera = new PerspectiveCamera(center - viewDirection * distance, viewDirection, new Vector3D(0, 1, 0), 40);
        if (spin)
        {
            AxisAngleRotation3D rotation = new(new Vector3D(0, 1, 0), 0);
            shapes.Transform = new RotateTransform3D(rotation, center);
            rotation.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        StackPanel content = new();
        if (mark.Title is { Length: > 0 } title)
            content.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xED)), Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(viewport);
        WrapPanel legend = new() { Margin = new Thickness(0, 6, 0, 0), MaxWidth = width };
        foreach (Part part in parts.Where(part => !string.IsNullOrWhiteSpace(part.Label)))
        {
            StackPanel entry = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4) };
            entry.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(part.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            entry.Children.Add(new TextBlock { Text = part.Label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xED)) });
            legend.Children.Add(entry);
        }
        if (legend.Children.Count > 0) content.Children.Add(legend);

        Border card = new()
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x17, 0x19, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, ScreenDrawingLayer.InkColor.R, ScreenDrawingLayer.InkColor.G, ScreenDrawingLayer.InkColor.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Child = content,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 24, ShadowDepth = 6, Direction = 270 }
        };
        Canvas.SetLeft(card, mark.Anchor.X);
        Canvas.SetTop(card, mark.Anchor.Y);
        card.Opacity = 0;
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
        return card;
    }

    internal static Part? ReadPart(Dictionary<string, object?> values)
    {
        string shape = (values.GetValueOrDefault("shape") as string ?? "box").ToLowerInvariant();
        if (shape is not ("box" or "sphere" or "cylinder" or "cone" or "arrow")) return null;
        double[] Numbers(string key) => values.GetValueOrDefault(key) is object?[] array ? array.OfType<double>().Select(value => Math.Clamp(value, -50, 50)).ToArray() : [];
        Point3D? ReadPoint(string key) => Numbers(key) is [var x, var y, var z, ..] ? new Point3D(x, y, z) : null;
        Color color = DefaultColor;
        if (values.GetValueOrDefault("color") is string colorText)
        {
            try { color = (Color)ColorConverter.ConvertFromString(colorText); }
            catch (FormatException) { }
        }
        double[] size = Numbers("size").Select(value => Math.Abs(value)).ToArray();
        Part part = new(shape, ReadPoint("at") ?? new Point3D(0, 0, 0), size, ReadPoint("from"), ReadPoint("to"), color, values.GetValueOrDefault("label") as string);
        return shape == "arrow" && (part.From is null || part.To is null) ? null : part;
    }

    internal static GeometryModel3D BuildModel(Part part)
    {
        MeshGeometry3D mesh = part.Shape switch
        {
            "sphere" => Sphere(part.At, Size(part, 0, 1) / 2),
            "cylinder" => Cylinder(part.At, Size(part, 0, 1) / 2, Size(part, 1, 1), coneTop: false),
            "cone" => Cylinder(part.At, Size(part, 0, 1) / 2, Size(part, 1, 1), coneTop: true),
            "arrow" => Arrow(part.From!.Value, part.To!.Value),
            _ => Box(part.At, Size(part, 0, 1), Size(part, 1, Size(part, 0, 1)), Size(part, 2, Size(part, 0, 1)))
        };
        MaterialGroup material = new();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(part.Color)));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 30));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static double Size(Part part, int index, double fallback) =>
        index < part.Size.Length && part.Size[index] > 0.01 ? part.Size[index] : fallback;

    private static MeshGeometry3D Box(Point3D center, double width, double height, double depth)
    {
        MeshGeometry3D mesh = new();
        double x = width / 2, y = height / 2, z = depth / 2;
        Point3D[] corners =
        [
            new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z),
            new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z)
        ];
        int[][] faces = [[0, 1, 2, 3], [5, 4, 7, 6], [4, 0, 3, 7], [1, 5, 6, 2], [3, 2, 6, 7], [4, 5, 1, 0]];
        foreach (int[] face in faces)
        {
            int start = mesh.Positions.Count;
            foreach (int corner in face) mesh.Positions.Add(corners[corner] + (Vector3D)center);
            mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 2);
            mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 2); mesh.TriangleIndices.Add(start + 3);
        }
        return mesh;
    }

    private static MeshGeometry3D Sphere(Point3D center, double radius, int slices = 24, int stacks = 16)
    {
        MeshGeometry3D mesh = new();
        for (int stack = 0; stack <= stacks; stack++)
        {
            double phi = Math.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                double theta = 2 * Math.PI * slice / slices;
                Vector3D normal = new(Math.Sin(phi) * Math.Cos(theta), Math.Cos(phi), Math.Sin(phi) * Math.Sin(theta));
                mesh.Positions.Add(center + normal * radius);
                mesh.Normals.Add(normal);
            }
        }
        for (int stack = 0; stack < stacks; stack++)
            for (int slice = 0; slice < slices; slice++)
            {
                int a = stack * (slices + 1) + slice, b = a + slices + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
            }
        return mesh;
    }

    /// <summary>A cylinder (or cone) standing on the y axis, centred on <paramref name="center"/>, with closed ends.</summary>
    private static MeshGeometry3D Cylinder(Point3D center, double radius, double height, bool coneTop, int slices = 28)
    {
        MeshGeometry3D mesh = new();
        double bottom = center.Y - height / 2, top = center.Y + height / 2;
        double topRadius = coneTop ? 0 : radius;
        int bottomCenter = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(center.X, bottom, center.Z));
        int topCenter = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(center.X, top, center.Z));
        for (int slice = 0; slice < slices; slice++)
        {
            double angle = 2 * Math.PI * slice / slices, next = 2 * Math.PI * (slice + 1) / slices;
            Point3D b0 = new(center.X + radius * Math.Cos(angle), bottom, center.Z + radius * Math.Sin(angle));
            Point3D b1 = new(center.X + radius * Math.Cos(next), bottom, center.Z + radius * Math.Sin(next));
            Point3D t0 = new(center.X + topRadius * Math.Cos(angle), top, center.Z + topRadius * Math.Sin(angle));
            Point3D t1 = new(center.X + topRadius * Math.Cos(next), top, center.Z + topRadius * Math.Sin(next));
            int start = mesh.Positions.Count;
            mesh.Positions.Add(b0); mesh.Positions.Add(b1); mesh.Positions.Add(t1); mesh.Positions.Add(t0);
            mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 3); mesh.TriangleIndices.Add(start + 1);
            mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 3); mesh.TriangleIndices.Add(start + 2);
            mesh.TriangleIndices.Add(bottomCenter); mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1);
            if (!coneTop) { mesh.TriangleIndices.Add(topCenter); mesh.TriangleIndices.Add(start + 2); mesh.TriangleIndices.Add(start + 3); }
        }
        return mesh;
    }

    /// <summary>A shaft and a cone head from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static MeshGeometry3D Arrow(Point3D from, Point3D to)
    {
        Vector3D direction = to - from;
        double length = Math.Max(0.05, direction.Length);
        double headLength = Math.Min(length * 0.35, 0.8), radius = Math.Max(0.03, length * 0.04);
        MeshGeometry3D shaft = Cylinder(new Point3D(0, (length - headLength) / 2, 0), radius, length - headLength, coneTop: false);
        MeshGeometry3D head = Cylinder(new Point3D(0, length - headLength / 2, 0), radius * 3, headLength, coneTop: true);
        MeshGeometry3D mesh = new();
        foreach (MeshGeometry3D piece in new[] { shaft, head })
        {
            int offset = mesh.Positions.Count;
            foreach (Point3D position in piece.Positions) mesh.Positions.Add(position);
            foreach (int index in piece.TriangleIndices) mesh.TriangleIndices.Add(index + offset);
        }
        // Built along +y from the origin: turn y onto the arrow's direction, then move it to its start.
        Vector3D yAxis = new(0, 1, 0), unit = direction / length;
        Vector3D axis = Vector3D.CrossProduct(yAxis, unit);
        double angle = Vector3D.AngleBetween(yAxis, unit);
        Transform3DGroup transform = new();
        if (axis.Length > 1e-6) transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angle)));
        else if (angle > 90) transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 180)));
        transform.Children.Add(new TranslateTransform3D((Vector3D)from));
        for (int index = 0; index < mesh.Positions.Count; index++) mesh.Positions[index] = transform.Transform(mesh.Positions[index]);
        return mesh;
    }
}

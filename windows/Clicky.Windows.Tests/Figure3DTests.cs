using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.30: figure3d(...) parts, read from data only and turned into shaded meshes.</summary>
public sealed class Figure3DTests
{
    [Fact]
    public void Parts_AreReadFromData_UnknownShapesAndHalfArrowsAreDropped()
    {
        Assert.Equal("cone", Figure3DCard.ReadPart(new() { ["shape"] = "Cone", ["at"] = new object?[] { 1.0, 2.0, 3.0 } })!.Shape);
        Assert.Null(Figure3DCard.ReadPart(new() { ["shape"] = "teapot" }));
        Assert.Null(Figure3DCard.ReadPart(new() { ["shape"] = "arrow", ["from"] = new object?[] { 0.0, 0.0, 0.0 } }));

        Figure3DCard.Part part = Figure3DCard.ReadPart(new()
        {
            ["shape"] = "box", ["at"] = new object?[] { 999.0, 0.0, 0.0 }, ["size"] = new object?[] { -2.0, 1.0, 1.0 }, ["color"] = "not a colour"
        })!;
        Assert.Equal(50, part.At.X); // Clamped into a sane scene.
        Assert.Equal(2, part.Size[0]);
        Assert.Equal(Color.FromRgb(0x4F, 0x8C, 0xFF), part.Color);
    }

    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("cone")]
    public void EachShape_BuildsAClosedMeshAroundItsPosition(string shape) => StaTestThread.Run(() =>
    {
        GeometryModel3D model = Figure3DCard.BuildModel(Figure3DCard.ReadPart(new()
        {
            ["shape"] = shape, ["at"] = new object?[] { 2.0, 1.0, 0.0 }, ["size"] = new object?[] { 1.0, 1.0, 1.0 }
        })!);
        MeshGeometry3D mesh = (MeshGeometry3D)model.Geometry;
        Assert.True(mesh.TriangleIndices.Count >= 12);
        Assert.Equal(0, mesh.TriangleIndices.Count % 3);
        Rect3D bounds = model.Bounds;
        Assert.InRange(bounds.X + bounds.SizeX / 2, 1.9, 2.1);
        Assert.InRange(bounds.Y + bounds.SizeY / 2, 0.9, 1.1);
    });

    [Fact]
    public void Arrow_RunsFromItsStartToItsEnd() => StaTestThread.Run(() =>
    {
        GeometryModel3D model = Figure3DCard.BuildModel(Figure3DCard.ReadPart(new()
        {
            ["shape"] = "arrow", ["from"] = new object?[] { 0.0, 0.0, 0.0 }, ["to"] = new object?[] { 4.0, 0.0, 0.0 }
        })!);
        Rect3D bounds = model.Bounds;
        Assert.InRange(bounds.X, -0.5, 0.1);
        Assert.InRange(bounds.X + bounds.SizeX, 3.9, 4.1);
        Assert.True(bounds.SizeY < 1.5);
    });

    [Fact]
    public void Figure3dCall_BecomesAMark_WithItsSizeAndParts()
    {
        CompanionScreenCapture capture = new([0xFF, 0xD8], "screen", true,
            new DisplayMonitor("D", new PixelRect(0, 0, 2560, 1600), new PixelRect(0, 0, 2560, 1600), 96, 96, true), 1280, 800,
            ScreenCaptureMethod.WindowsGraphicsCapture, TimeSpan.Zero);
        ProgramCall call = ReplyProgramReader.ReadAll(
            "figure3d(100, 50, 400, 300, \"gears\", [{shape: \"cylinder\", at: [0, 0, 0], size: [1, 0.3]}], {spin: true})\n").Calls.Single();

        ScreenMark mark = ScreenMarkBuilder.FromCall(call, [capture])!;

        Assert.Equal(ScreenMarkKind.Figure3D, mark.Kind);
        Assert.Equal(new Size(800, 600), mark.Size);
        Assert.Equal("gears", mark.Title);
        Assert.Contains(((object?[])mark.Payload!), value => value is object?[]);
    }
}

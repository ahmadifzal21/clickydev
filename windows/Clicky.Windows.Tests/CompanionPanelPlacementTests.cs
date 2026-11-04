using Clicky.Windows.Tray;

namespace Clicky.Windows.Tests;

public sealed class CompanionPanelPlacementTests
{
    private static readonly PixelRect FullHdMonitor = new(0, 0, 1920, 1080);

    [Fact]
    public void BottomTaskbar_DetectedFromWorkAreaInset()
    {
        TaskbarEdge taskbarEdge = CompanionPanelPlacement.DetectTaskbarEdge(FullHdMonitor, new PixelRect(0, 0, 1920, 1032), 1800, 1060);

        Assert.Equal(TaskbarEdge.Bottom, taskbarEdge);
    }

    [Theory]
    [InlineData(0, 48, 1920, 1080, TaskbarEdge.Top)]
    [InlineData(62, 0, 1920, 1080, TaskbarEdge.Left)]
    [InlineData(0, 0, 1858, 1080, TaskbarEdge.Right)]
    public void OtherTaskbarEdges_DetectedFromWorkAreaInset(int left, int top, int right, int bottom, TaskbarEdge expectedTaskbarEdge)
    {
        Assert.Equal(expectedTaskbarEdge, CompanionPanelPlacement.DetectTaskbarEdge(FullHdMonitor, new PixelRect(left, top, right, bottom), 960, 540));
    }

    [Fact]
    public void AutoHideTaskbar_UsesEdgeNearestTheClick()
    {
        Assert.Equal(TaskbarEdge.Right, CompanionPanelPlacement.DetectTaskbarEdge(FullHdMonitor, FullHdMonitor, 1915, 700));
        Assert.Equal(TaskbarEdge.Bottom, CompanionPanelPlacement.DetectTaskbarEdge(FullHdMonitor, FullHdMonitor, 1700, 1078));
    }

    [Fact]
    public void BottomTaskbar_PanelSitsAboveTaskbarCenteredOnClick()
    {
        (int left, int top) = CompanionPanelPlacement.CalculatePanelTopLeft(
            new PixelRect(0, 0, 1920, 1032), TaskbarEdge.Bottom, anchorX: 1000, anchorY: 1060, panelWidth: 320, panelHeight: 400, gapFromTaskbar: 8);

        Assert.Equal(840, left);
        Assert.Equal(1032 - 400 - 8, top);
    }

    [Fact]
    public void BottomTaskbar_ClickNearRightCorner_ClampedInsideWorkArea()
    {
        (int left, _) = CompanionPanelPlacement.CalculatePanelTopLeft(
            new PixelRect(0, 0, 1920, 1032), TaskbarEdge.Bottom, anchorX: 1900, anchorY: 1060, panelWidth: 320, panelHeight: 400, gapFromTaskbar: 8);

        Assert.Equal(1920 - 320 - 8, left);
    }

    [Fact]
    public void LeftTaskbarOnSecondMonitor_PanelBesideTaskbar()
    {
        // Second monitor to the left of the primary, so coordinates are negative.
        PixelRect secondMonitorWorkArea = new(-2560 + 62, 0, 0, 1440);

        (int left, int top) = CompanionPanelPlacement.CalculatePanelTopLeft(
            secondMonitorWorkArea, TaskbarEdge.Left, anchorX: -2530, anchorY: 1400, panelWidth: 320, panelHeight: 400, gapFromTaskbar: 8);

        Assert.Equal(-2560 + 62 + 8, left);
        Assert.Equal(1440 - 400 - 8, top);
    }

    [Fact]
    public void RightAndTopTaskbars_PanelHugsThatEdge()
    {
        (int rightEdgeLeft, _) = CompanionPanelPlacement.CalculatePanelTopLeft(
            new PixelRect(0, 0, 1858, 1080), TaskbarEdge.Right, 1880, 540, 320, 400, 8);
        (_, int topEdgeTop) = CompanionPanelPlacement.CalculatePanelTopLeft(
            new PixelRect(0, 48, 1920, 1080), TaskbarEdge.Top, 1800, 20, 320, 400, 8);

        Assert.Equal(1858 - 320 - 8, rightEdgeLeft);
        Assert.Equal(48 + 8, topEdgeTop);
    }
}

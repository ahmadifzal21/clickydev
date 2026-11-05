using System.Windows;
using System.Windows.Controls;

namespace Clicky.Windows.Overlay;

/// <summary>Draws the companion in one overlay's DIP space; the window decides which monitor that is.</summary>
public partial class BlueCursorView : UserControl
{
    /// <summary>The Mac draws the buddy 35 pt right of and 25 pt below the mouse so it never hides the real pointer.</summary>
    internal static readonly Vector MouseFollowOffset = new(35, 25);

    private readonly CursorFollowSpring cursorFollowSpring = new();

    public BlueCursorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Called every rendered frame. The spring keeps tracking while the mouse is on another monitor so the
    /// triangle appears in the right place, not from a stale spot, the moment the mouse arrives here.
    /// </summary>
    internal void FollowMouse(Point mouseInOverlayDips, bool isMouseOnThisMonitor, double elapsedSeconds)
    {
        Point cursorPosition = cursorFollowSpring.Step(mouseInOverlayDips + MouseFollowOffset, elapsedSeconds);
        Canvas.SetLeft(BlueCursorTriangle, cursorPosition.X);
        Canvas.SetTop(BlueCursorTriangle, cursorPosition.Y);
        BlueCursorTriangle.Visibility = isMouseOnThisMonitor ? Visibility.Visible : Visibility.Hidden;
    }
}

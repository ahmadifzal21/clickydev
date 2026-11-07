using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

namespace Clicky.Windows.Automation;

/// <summary>
/// Moves a guided step's target onto the real control under it (E11 T11.14): the model's box is often a few pixels off, so
/// UI Automation looks at the element under the box's center and walks up (at most 8 parents) to the first clickable control.
/// That control's bounds replace the box only when they plausibly are the same thing (<see cref="IsPlausibleSnap"/>);
/// otherwise the model's box stays. Runs on its own MTA thread with a 300 ms budget, and never inside Clicky's own windows.
/// </summary>
internal static class UiaControlSnapper
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);
    private const int MaximumParentSteps = 8;

    private static readonly HashSet<int> ClickableControlTypeIds =
    [
        ControlType.Button.Id, ControlType.Hyperlink.Id, ControlType.MenuItem.Id, ControlType.CheckBox.Id, ControlType.RadioButton.Id,
        ControlType.Edit.Id, ControlType.ListItem.Id, ControlType.TreeItem.Id, ControlType.TabItem.Id, ControlType.ComboBox.Id,
        ControlType.SplitButton.Id, ControlType.Slider.Id, ControlType.DataItem.Id, ControlType.MenuBar.Id, ControlType.Spinner.Id
    ];

    /// <summary>The control's bounds (physical pixels) to use instead of <paramref name="modelBox"/>, or null to keep the box.</summary>
    public static async Task<Rect?> SnapAsync(Rect modelBox, Rect monitorBounds)
    {
        TaskCompletionSource<Rect?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread automationThread = new(() =>
        {
            try { result.TrySetResult(FindControl(modelBox, monitorBounds)); }
            catch (Exception exception)
            {
                Trace.WriteLine($"Target snap skipped: {exception.Message}");
                result.TrySetResult(null);
            }
        }) { IsBackground = true, Name = "Clicky target snap" };
        automationThread.SetApartmentState(ApartmentState.MTA);
        Stopwatch stopwatch = Stopwatch.StartNew();
        automationThread.Start();
        Task finished = await Task.WhenAny(result.Task, Task.Delay(Budget));
        if (finished != result.Task)
        {
            Trace.WriteLine("Target snap took longer than 300 ms; keeping the model's box.");
            return null;
        }
        Rect? snapped = await result.Task;
        if (snapped is { } control)
            Trace.WriteLine($"Target snapped to the control {control.Width:0}x{control.Height:0} from the model's {modelBox.Width:0}x{modelBox.Height:0} ({stopwatch.ElapsedMilliseconds} ms).");
        return snapped;
    }

    private static Rect? FindControl(Rect modelBox, Rect monitorBounds)
    {
        Point center = new(modelBox.X + modelBox.Width / 2, modelBox.Y + modelBox.Height / 2);
        AutomationElement? element = AutomationElement.FromPoint(center);
        int ownProcessId = Environment.ProcessId;
        for (int step = 0; element is not null && step <= MaximumParentSteps; step++)
        {
            AutomationElement.AutomationElementInformation current = element.Current;
            if (current.ProcessId == ownProcessId) return null;
            if (ControlTypeIdIsWindowOrPane(current.ControlType.Id)) return null; // Walked out of any control: containers don't count.
            bool isClickable = ClickableControlTypeIds.Contains(current.ControlType.Id)
                || (bool)element.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty);
            if (isClickable)
            {
                Rect bounds = current.BoundingRectangle;
                return IsPlausibleSnap(bounds, modelBox, monitorBounds) ? bounds : null;
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }

    private static bool ControlTypeIdIsWindowOrPane(int controlTypeId) =>
        controlTypeId == ControlType.Window.Id || controlTypeId == ControlType.Pane.Id;

    /// <summary>
    /// A control replaces the model's box only if it is at least 8x8, on the monitor, no more than a quarter of the monitor,
    /// between 0.1x and 8x the box's area, and contains the box's center (with 12 px of slack).
    /// </summary>
    internal static bool IsPlausibleSnap(Rect control, Rect modelBox, Rect monitorBounds)
    {
        if (control.IsEmpty || control.Width < 8 || control.Height < 8) return false;
        if (!monitorBounds.IntersectsWith(control)) return false;
        double controlArea = control.Width * control.Height;
        if (controlArea > monitorBounds.Width * monitorBounds.Height * 0.25) return false;
        double boxArea = Math.Max(1, modelBox.Width * modelBox.Height);
        double areaRatio = controlArea / boxArea;
        if (areaRatio < 0.1 || areaRatio > 8) return false;
        Rect slack = control;
        slack.Inflate(12, 12);
        return slack.Contains(new Point(modelBox.X + modelBox.Width / 2, modelBox.Y + modelBox.Height / 2));
    }
}

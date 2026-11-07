using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Clicky.Windows.Companion;

namespace Clicky.Windows.Automation;

/// <summary>
/// The page the user is looking at (E11 T11.26): when a Chromium browser is in front, UI Automation gives its address bar and
/// the text of its web document, so questions about the page don't have to be answered from pixels alone. 400 ms at most, on
/// its own MTA thread; nothing when the browser doesn't answer in time. The text is only sent to the model, never logged.
/// </summary>
internal static class BrowserPageReader
{
    internal const int MaximumPageCharacters = 4000;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "brave", "vivaldi", "opera" };

    public static bool IsBrowser(ForegroundAppInfo? frontApp) => frontApp is not null && BrowserProcessNames.Contains(frontApp.ProcessName);

    /// <summary>The [page] context line for the browser in front, or null.</summary>
    public static async Task<string?> ReadContextLineAsync(ForegroundAppInfo? frontApp)
    {
        if (!IsBrowser(frontApp)) return null;
        IntPtr windowHandle = GetForegroundWindow();
        TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread reader = new(() =>
        {
            try { result.TrySetResult(Read(windowHandle, frontApp!.WindowTitle)); }
            catch (Exception exception)
            {
                Trace.WriteLine($"Browser page not read: {exception.Message}");
                result.TrySetResult(null);
            }
        }) { IsBackground = true, Name = "Clicky page read" };
        reader.SetApartmentState(ApartmentState.MTA);
        Stopwatch stopwatch = Stopwatch.StartNew();
        reader.Start();
        if (await Task.WhenAny(result.Task, Task.Delay(Budget)) != result.Task)
        {
            Trace.WriteLine("Browser page read took longer than 400 ms; skipped.");
            return null;
        }
        string? line = await result.Task;
        if (line is not null) Trace.WriteLine($"Browser page read in {stopwatch.ElapsedMilliseconds} ms ({line.Length} characters).");
        return line;
    }

    private static string? Read(IntPtr windowHandle, string windowTitle)
    {
        if (windowHandle == IntPtr.Zero) return null;
        AutomationElement window = AutomationElement.FromHandle(windowHandle);
        AutomationElement? addressBar = window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true)));
        string address = addressBar?.TryGetCurrentPattern(ValuePattern.Pattern, out object value) == true ? ((ValuePattern)value).Current.Value : "";
        AutomationElement? document = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        string text = "";
        if (document?.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern) == true)
        {
            TextPatternRange range = ((TextPattern)textPattern).DocumentRange;
            text = range.GetText(MaximumPageCharacters + 1);
        }
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaximumPageCharacters) text = text[..MaximumPageCharacters] + "…";
        if (address.Length == 0 && text.Length == 0) return null;
        string title = windowTitle.Length > 120 ? windowTitle[..120] + "…" : windowTitle;
        return $"[page] {address} \"{title}\" — {text}";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}

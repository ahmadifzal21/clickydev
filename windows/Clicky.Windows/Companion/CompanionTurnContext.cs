using System.Globalization;

namespace Clicky.Windows.Companion;

/// <summary>
/// The app's lines at the start of every turn (E11 T11.19), sent apart from the user's words: when it is, which app is in front
/// and its window title (often the document or settings page's name), whether they spoke or typed, the walkthrough step, then
/// any other app lines (notes from the last step, memory, the browser page, a highlight, jobs). The prompt explains each one.
/// </summary>
internal static class CompanionTurnContext
{
    public static string Build(DateTime localTime, ForegroundAppInfo? frontApp, bool isVoice, int? walkthroughStep,
        IEnumerable<string> appLines)
    {
        List<string> parts = [localTime.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)];
        if (frontApp is not null)
        {
            string title = frontApp.WindowTitle.Trim();
            if (title.Length > 160) title = title[..160] + "…";
            parts.Add(title.Length > 0 ? $"app: {frontApp.ProcessName} \"{title}\"" : $"app: {frontApp.ProcessName}");
        }
        parts.Add(isVoice ? "voice" : "typed");
        if (walkthroughStep is { } step and > 0) parts.Add($"walkthrough step {step}");
        return string.Join("\n", [$"[context] {string.Join(" · ", parts)}", .. appLines.Where(line => !string.IsNullOrWhiteSpace(line))]);
    }
}

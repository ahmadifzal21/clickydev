using System.Diagnostics;
using System.IO;
using System.Text;
using Clicky.Windows.AI;

namespace Clicky.Windows.Companion;

/// <summary>
/// debug.saveTurns (E11 T11.28): each turn as the model saw it — the screenshots actually sent (highlight painted in), the
/// context, the prompt, the raw reply and the timing line — in %LOCALAPPDATA%\Clicky\turns\&lt;time&gt;-&lt;turn&gt;\. A turn worth
/// keeping becomes a bench case by adding an expect.json beside it. Off by default: screenshots are private.
/// </summary>
internal static class TurnRecorder
{
    public static string TurnsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky", "turns");

    public static void Save(int turnId, CompanionChatRequest request, string replyText, string backend, string timingLine)
    {
        try
        {
            string folder = Path.Combine(TurnsFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{turnId}");
            Directory.CreateDirectory(folder);
            StringBuilder requestText = new();
            requestText.AppendLine(request.TurnContext ?? "");
            for (int index = 0; index < request.LabeledScreenshotImages.Count; index++)
            {
                LabeledScreenshotImage screenshot = request.LabeledScreenshotImages[index];
                File.WriteAllBytes(Path.Combine(folder, $"screen-{index + 1}.jpg"), screenshot.JpegImageBytes);
                requestText.AppendLine($"screen-{index + 1}.jpg: {screenshot.ImageLabel}");
            }
            requestText.AppendLine().AppendLine(request.UserPrompt);
            File.WriteAllText(Path.Combine(folder, "request.txt"), requestText.ToString());
            File.WriteAllText(Path.Combine(folder, "reply.txt"), $"{backend}\n\n{replyText}");
            File.WriteAllText(Path.Combine(folder, "timing.txt"), timingLine);
            Trace.WriteLine($"Turn saved to {folder}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Couldn't save the turn: {exception.Message}");
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Windows.AI;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

/// <summary>
/// E11 T11.29, opt-in: how well and how fast each backend points. Set CLICKY_BENCH=1 to run (it calls the real CLIs and costs
/// tokens). Cases: a rendered app window with known control positions (always), plus every saved turn (debug.saveTurns) that
/// has an expect.json beside it: {"question": "...", "boxes": [[x1, y1, x2, y2], ...]} in the screenshot's pixels. A reply
/// hits when its first point/click/hover/ring/box lands in an expected box (6 px slack). Writes windows/bench/results/*.md.
/// CLICKY_BENCH_CONFIGS picks configurations, e.g. "claude-sonnet-low,codex-low".
/// </summary>
public sealed class PointingBench
{
    private sealed record BenchCase(string Name, byte[] Jpeg, int Width, int Height, string Question, Rect[] Boxes);

    private sealed record BenchConfig(string Name, Func<ICompanionChatBackend> CreateBackend);

    private static readonly BenchConfig[] AllConfigs =
    [
        new("claude-sonnet-low", () => new ClaudeCodeCliChatBackend(new ClaudeCodeCliBackendSettings { Model = "sonnet", Effort = "low", KeepSessionAlive = false, WebTools = false })),
        new("claude-sonnet-medium", () => new ClaudeCodeCliChatBackend(new ClaudeCodeCliBackendSettings { Model = "sonnet", Effort = "medium", KeepSessionAlive = false, WebTools = false })),
        new("claude-haiku-low", () => new ClaudeCodeCliChatBackend(new ClaudeCodeCliBackendSettings { Model = "haiku", Effort = "low", KeepSessionAlive = false, WebTools = false })),
        new("codex-low", () => new CodexCliChatBackend(new CodexCliBackendSettings { ReasoningEffort = "low", WebSearch = false }))
    ];

    [Fact]
    public void MeasurePointingAccuracyAndSpeed()
    {
        if (Environment.GetEnvironmentVariable("CLICKY_BENCH") is not { Length: > 0 }) return;
        List<BenchCase> cases = [];
        StaTestThread.Run(() => cases.AddRange(RenderedAppCases()));
        cases.AddRange(SavedTurnCases());
        string? configFilter = Environment.GetEnvironmentVariable("CLICKY_BENCH_CONFIGS");
        BenchConfig[] configs = configFilter is { Length: > 0 }
            ? AllConfigs.Where(config => configFilter.Split(',').Contains(config.Name)).ToArray()
            : AllConfigs;

        StringBuilder report = new();
        report.AppendLine($"# Pointing bench {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        report.AppendLine($"{cases.Count} cases. Hit = the reply's first mark lands in an expected box (6 px slack).").AppendLine();
        report.AppendLine("| Config | Hits | First text (median) | Total (median) | Failures |");
        report.AppendLine("|---|---|---|---|---|");
        StringBuilder details = new();
        foreach (BenchConfig config in configs)
        {
            ICompanionChatBackend backend = config.CreateBackend();
            List<double> firstTexts = [], totals = [];
            int hits = 0, failures = 0;
            foreach (BenchCase benchCase in cases)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                double? firstText = null;
                string reply;
                try
                {
                    CompanionChatRequest request = new(CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt,
                        [new LabeledScreenshotImage(benchCase.Jpeg, $"user's screen (cursor is here) (image dimensions: {benchCase.Width}x{benchCase.Height} pixels)")],
                        [], benchCase.Question) { TurnContext = "[context] Sat 7 Nov 14:32 · app: BenchApp \"Report.docx - BenchApp\" · voice" };
                    reply = backend.GetResponseTextAsync(request, CancellationToken.None, _ => firstText ??= stopwatch.Elapsed.TotalSeconds)
                        .GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    failures++;
                    details.AppendLine($"- {config.Name} / {benchCase.Name}: failed: {exception.Message}");
                    continue;
                }
                totals.Add(stopwatch.Elapsed.TotalSeconds);
                firstTexts.Add(firstText ?? stopwatch.Elapsed.TotalSeconds);
                Point? markCenter = FirstMarkCenter(reply);
                bool hit = markCenter is { } center && benchCase.Boxes.Any(box => Inflated(box, 6).Contains(center));
                if (hit) hits++;
                details.AppendLine($"- {config.Name} / {benchCase.Name}: {(hit ? "hit" : "miss")} at {markCenter?.ToString() ?? "no mark"}, " +
                    $"{firstText ?? 0:0.00} s / {stopwatch.Elapsed.TotalSeconds:0.00} s");
            }
            backend.Shutdown();
            report.AppendLine($"| {config.Name} | {hits}/{cases.Count} | {Median(firstTexts):0.00} s | {Median(totals):0.00} s | {failures} |");
        }
        report.AppendLine().AppendLine("## Each case").AppendLine().Append(details);

        string resultsFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bench", "results"));
        Directory.CreateDirectory(resultsFolder);
        string resultsFile = Path.Combine(resultsFolder, $"{DateTime.Now:yyyyMMdd-HHmm}.md");
        File.WriteAllText(resultsFile, report.ToString());
        Console.WriteLine(report);
        Console.WriteLine($"Bench results: {resultsFile}");
    }

    private static Point? FirstMarkCenter(string reply)
    {
        foreach (ProgramCall call in ReplyProgramReader.ReadAll(reply).Calls)
        {
            double[] numbers = call.LeadingNumbers();
            switch (call.Name)
            {
                case "point" or "ring" when numbers.Length >= 2: return new Point(numbers[0], numbers[1]);
                case "click" or "hover" or "box" when numbers.Length >= 4: return new Point((numbers[0] + numbers[2]) / 2, (numbers[1] + numbers[3]) / 2);
            }
        }
        return null;
    }

    private static Rect Inflated(Rect box, double slack)
    {
        box.Inflate(slack, slack);
        return box;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        List<double> sorted = [.. values.Order()];
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>A plain document app at 1280x800 with a menu bar, toolbar, sidebar and a dialog, every control at a known spot.</summary>
    private static IEnumerable<BenchCase> RenderedAppCases()
    {
        Canvas canvas = new() { Width = 1280, Height = 800, Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)) };
        Dictionary<string, Rect> controls = [];
        void Add(string name, Rect rect, string text, Brush background, double fontSize = 13)
        {
            Border control = new()
            {
                Width = rect.Width, Height = rect.Height, Background = background, CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = text, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            };
            Canvas.SetLeft(control, rect.X);
            Canvas.SetTop(control, rect.Y);
            canvas.Children.Add(control);
            controls[name] = rect;
        }
        Brush plain = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), button = new SolidColorBrush(Color.FromRgb(0xE1, 0xE1, 0xE1));
        Brush blue = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xE0));
        Add("title", new Rect(0, 0, 1280, 32), "Report.docx - BenchApp", new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
        string[] menus = ["File", "Edit", "View", "Insert", "Help"];
        for (int index = 0; index < menus.Length; index++) Add(menus[index], new Rect(8 + index * 64, 36, 56, 24), menus[index], plain);
        string[] tools = ["Save", "Undo", "Print", "Export PDF", "Share"];
        for (int index = 0; index < tools.Length; index++) Add(tools[index], new Rect(12 + index * 104, 68, 96, 30), tools[index], button);
        string[] places = ["Recent", "Drafts", "Shared with me", "Trash", "Settings"];
        for (int index = 0; index < places.Length; index++) Add(places[index], new Rect(12, 120 + index * 40, 200, 32), places[index], plain);
        Add("page", new Rect(240, 120, 620, 640), "Quarterly report\n\nRevenue grew 12% ...", Brushes.White, 15);
        Add("dialog", new Rect(900, 300, 340, 160), "Save changes to Report.docx?", Brushes.White);
        Add("Don't save", new Rect(916, 410, 96, 32), "Don't save", button);
        Add("Cancel", new Rect(1024, 410, 96, 32), "Cancel", button);
        Add("Save dialog", new Rect(1132, 410, 96, 32), "Save", blue);
        canvas.Measure(new Size(1280, 800));
        canvas.Arrange(new Rect(0, 0, 1280, 800));
        RenderTargetBitmap bitmap = new(1280, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        JpegBitmapEncoder encoder = new() { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream jpeg = new();
        encoder.Save(jpeg);
        byte[] image = jpeg.ToArray();
        (string Name, string Question, string[] Targets)[] questions =
        [
            ("export", "Where's the button to export a PDF?", ["Export PDF"]),
            ("help", "Where is the Help menu?", ["Help"]),
            ("settings", "How do I get to the settings?", ["Settings"]),
            ("drafts", "Show me my drafts.", ["Drafts"]),
            ("confirm-save", "I want to keep my changes in this dialog. What do I click?", ["Save dialog"]),
            ("cancel", "How do I close this dialog without doing anything?", ["Cancel"]),
            ("print-walkthrough", "Walk me through printing this.", ["Print", "File"]),
            ("undo", "I made a mistake, how do I undo it?", ["Undo", "Edit"])
        ];
        foreach ((string name, string question, string[] targets) in questions)
            yield return new BenchCase($"app/{name}", image, 1280, 800, question, targets.Select(target => controls[target]).ToArray());
    }

    private static IEnumerable<BenchCase> SavedTurnCases()
    {
        if (!Directory.Exists(TurnRecorder.TurnsFolder)) yield break;
        foreach (string expectFile in Directory.EnumerateFiles(TurnRecorder.TurnsFolder, "expect.json", SearchOption.AllDirectories))
        {
            string folder = Path.GetDirectoryName(expectFile)!;
            string screenshot = Path.Combine(folder, "screen-1.jpg");
            if (!File.Exists(screenshot)) continue;
            using JsonDocument expect = JsonDocument.Parse(File.ReadAllText(expectFile));
            Rect[] boxes = expect.RootElement.GetProperty("boxes").EnumerateArray()
                .Select(box => box.EnumerateArray().Select(value => value.GetDouble()).ToArray())
                .Select(values => new Rect(new Point(values[0], values[1]), new Point(values[2], values[3]))).ToArray();
            byte[] jpeg = File.ReadAllBytes(screenshot);
            BitmapFrame frame = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            yield return new BenchCase($"turn/{Path.GetFileName(folder)}", jpeg, frame.PixelWidth, frame.PixelHeight,
                expect.RootElement.GetProperty("question").GetString() ?? "", boxes);
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clicky.Windows.Overlay;

namespace Clicky.Windows.Tests;

/// <summary>
/// Opt-in: renders every mark kind to a PNG so a person can look at them (the real overlay is excluded from screenshots).
/// Set CLICKY_DRAWING_PREVIEW to an output file path to run it.
/// </summary>
public sealed class ScreenDrawingPreview
{
    [Fact]
    public void RenderEveryMarkToAPng()
    {
        string? outputPath = Environment.GetEnvironmentVariable("CLICKY_DRAWING_PREVIEW");
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        StaTestThread.Run(() =>
        {
            ScreenDrawingLayer layer = new() { Width = 1000, Height = 560 };
            Grid root = new() { Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF2)), Children = { layer } };
            Window window = new() { Content = root, Width = 1000, Height = 560, WindowStyle = WindowStyle.None, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            layer.Add(new ScreenMark(ScreenMarkKind.Ring, [new Point(110, 90)]) { Radius = 40, Text = "ring" });
            layer.Add(new ScreenMark(ScreenMarkKind.Box, [new Point(230, 50), new Point(420, 130)]) { Text = "box" });
            layer.Add(new ScreenMark(ScreenMarkKind.Underline, [new Point(480, 120), new Point(680, 120)]));
            layer.Add(new ScreenMark(ScreenMarkKind.Highlight, [new Point(720, 80), new Point(940, 110)]));
            layer.Add(new ScreenMark(ScreenMarkKind.Arrow, [new Point(80, 300), new Point(360, 220)]) { Text = "arrow" });
            layer.Add(new ScreenMark(ScreenMarkKind.Trace, [new Point(420, 300), new Point(480, 220), new Point(560, 320), new Point(640, 230)]));
            layer.Add(new ScreenMark(ScreenMarkKind.Label, [new Point(700, 220)]) { Text = "a label" });
            layer.Add(new ScreenMark(ScreenMarkKind.ScrollHint, [new Point(880, 250)]) { Direction = "down" });
            layer.Add(new ScreenMark(ScreenMarkKind.Card, [new Point(60, 380)]) { Title = "Shortcut", Text = "Press **Ctrl+Shift+P**\n- opens the command palette\n- type to search" });
            layer.Add(new ScreenMark(ScreenMarkKind.TargetRing, [new Point(560, 450)]) { Radius = 36 });
            layer.Add(new ScreenMark(ScreenMarkKind.Figure3D, [new Point(660, 300)])
            {
                Size = new Size(300, 200), Title = "Two gears",
                Payload = new object?[] { "Two gears", new object?[]
                {
                    new Dictionary<string, object?> { ["shape"] = "cylinder", ["at"] = new object?[] { -1.6, 0.0, 0.0 }, ["size"] = new object?[] { 1.6, 0.4 }, ["color"] = "#4f8cff", ["label"] = "small gear" },
                    new Dictionary<string, object?> { ["shape"] = "cylinder", ["at"] = new object?[] { 1.2, 0.0, 0.0 }, ["size"] = new object?[] { 3.2, 0.4 }, ["color"] = "#ff9f43", ["label"] = "big gear" },
                    new Dictionary<string, object?> { ["shape"] = "arrow", ["from"] = new object?[] { -1.6, 1.2, 0.0 }, ["to"] = new object?[] { -0.4, 1.2, 0.0 }, ["color"] = "#ffffff", ["label"] = "fast" },
                    new Dictionary<string, object?> { ["shape"] = "sphere", ["at"] = new object?[] { 0.0, 1.5, 1.0 }, ["size"] = new object?[] { 0.6 }, ["color"] = "#43d692" }
                } }
            });

            DispatcherFrame frame = new();
            DispatcherTimer timer = new(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame); // Let the draw-on animations finish.
            timer.Stop();

            RenderTargetBitmap bitmap = new(1000, 560, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(outputPath);
            encoder.Save(file);
            window.Close();
        });
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Windows.AI;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;
using Xunit.Abstractions;

namespace Clicky.Windows.Tests;

/// <summary>
/// T5.8 end to end against the real backends, using the user's clicky.config.json and CLI logins: show an orange target,
/// capture every screen, ask the backend to point at it, check the answer, the [POINT] tag lands on the target, and the
/// overlay starts pointing. Off by default (real CLIs, network, quota); run with
/// <c>$env:CLICKY_LIVE_BACKENDS=1; dotnet test --filter Category=LiveBackend --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "LiveBackend")]
public sealed class LiveBackendVerificationTests(ITestOutputHelper output)
{
    private const int TargetHitMarginPixels = 60;

    // shortcut: xunit 2.5 has no runtime skip, so with the variable unset these pass without doing anything.
    private static bool IsEnabled => Environment.GetEnvironmentVariable("CLICKY_LIVE_BACKENDS") == "1";

    [Theory]
    [InlineData(ChatBackendKind.ClaudeCodeCli)]
    [InlineData(ChatBackendKind.CodexCli)]
    [InlineData(ChatBackendKind.CloudflareWorker)]
    public void Backend_AnswersAndPointsAtAVisibleTarget(ChatBackendKind backendKind)
    {
        if (!IsEnabled) return;
        ClickyConfiguration configuration = ClickyConfigurationStore.LoadOrCreateDefault().CurrentConfiguration;
        ICompanionChatBackend backend = backendKind switch
        {
            ChatBackendKind.ClaudeCodeCli => new ClaudeCodeCliChatBackend(configuration.ClaudeCodeCli),
            ChatBackendKind.CodexCli => new CodexCliChatBackend(configuration.CodexCli),
            _ => new CloudflareWorkerChatBackend(configuration.CloudflareWorker)
        };

        RunOnStaThread(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            Window targetWindow = ShowTargetWindow();
            try
            {
                Assert.True(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(targetWindow).Handle, out NativeRect targetRect));
                // Off the dispatcher thread so the WinRT capture and CLI awaits can't deadlock on its context.
                IReadOnlyList<CompanionScreenCapture> screenCaptures = Task.Run(() =>
                    CompanionScreenCaptureUtility.CaptureAllScreensAsJpegAsync(displayMonitorCatalog.Monitors, CancellationToken.None))
                    .GetAwaiter().GetResult();

                Stopwatch answerStopwatch = Stopwatch.StartNew();
                string responseText = Task.Run(() => backend.GetResponseTextAsync(CompanionScreenQuestion.BuildChatRequest(
                    "Point at the orange box that says CLICKY TARGET.", screenCaptures, []), CancellationToken.None))
                    .GetAwaiter().GetResult();
                output.WriteLine($"{backend.DisplayName} answered in {answerStopwatch.ElapsedMilliseconds} ms: {responseText}");

                PointingParseResult pointingParseResult = PointTagParser.ParsePointingCoordinates(responseText);
                Assert.False(string.IsNullOrWhiteSpace(pointingParseResult.SpokenText));
                Point? pointTarget = CompanionScreenQuestion.ResolvePointTarget(pointingParseResult, screenCaptures);
                Assert.True(pointTarget.HasValue, "The answer had no usable [POINT] tag.");
                output.WriteLine($"Target {pointTarget}, orange box ({targetRect.Left},{targetRect.Top})-({targetRect.Right},{targetRect.Bottom}).");
                Assert.InRange(pointTarget.Value.X, targetRect.Left - TargetHitMarginPixels, targetRect.Right + TargetHitMarginPixels);
                Assert.InRange(pointTarget.Value.Y, targetRect.Top - TargetHitMarginPixels, targetRect.Bottom + TargetHitMarginPixels);

                using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
                cursorOverlayManager.ShowTransiently();
                cursorOverlayManager.PointAt(pointTarget.Value.X, pointTarget.Value.Y, pointingParseResult.ElementLabel);
                Assert.True(cursorOverlayManager.IsPointing);
            }
            finally
            {
                targetWindow.Close();
            }
        });
    }

    /// <summary>A big topmost orange box in the middle of the primary monitor, rendered before returning.</summary>
    private static Window ShowTargetWindow()
    {
        Window targetWindow = new()
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
            Width = 360, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            Content = new TextBlock
            {
                Text = "CLICKY TARGET", FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        targetWindow.Show();
        targetWindow.Activate();
        // Let it render and reach the compositor before the screenshot.
        Stopwatch renderWait = Stopwatch.StartNew();
        while (renderWait.ElapsedMilliseconds < 500)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(20);
        }
        return targetWindow;
    }

    private static void RunOnStaThread(Action testAction)
    {
        Exception? testException = null;
        Thread testThread = new(() =>
        {
            IntPtr previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { testAction(); }
            catch (Exception exception) { testException = exception; }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                SetThreadDpiAwarenessContext(previousDpiContext);
            }
        });
        testThread.SetApartmentState(ApartmentState.STA);
        testThread.Start();
        testThread.Join();
        if (testException is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(testException).Throw();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect windowRect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}

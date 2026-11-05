using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Windows.AI;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Xunit.Abstractions;

namespace Clicky.Windows.Tests;

/// <summary>
/// T5.8 end to end against the real backends, using the user's clicky.config.json and CLI logins. Drives the same
/// <see cref="TypedScreenQuestionRunner"/> the tray item uses (minus the input box): an orange target on screen, real capture,
/// real backend, [POINT] must land on the target, overlay points and keeps the bubble, history carries to a second turn.
/// Off by default (real CLIs, network, quota); run with
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
    public void TypedQuestion_AnswersPointsAtTargetKeepsBubbleAndRemembersHistory(ChatBackendKind backendKind)
    {
        if (!IsEnabled) return;
        ChatBackendRouter router = CreateRouter(new JobBackendRouting { Answer = backendKind, Fallback = null });

        RunOnStaThreadWithDispatcher(() =>
        {
            using DisplayMonitorCatalog displayMonitorCatalog = new();
            using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
            List<CompanionVoiceState> voiceStates = [];
            cursorOverlayManager.CompanionStatusChanged += (_, voiceState) => voiceStates.Add(voiceState);
            CompanionConversationHistory history = new();
            TypedScreenQuestionRunner runner = new(cursorOverlayManager, displayMonitorCatalog, router, history);
            PixelRect targetRect;
            Window targetWindow = ShowTargetWindow();
            try
            {
                Assert.True(GetWindowRect(new WindowInteropHelper(targetWindow).Handle, out NativeRect nativeRect));
                targetRect = new PixelRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);

                // Turn 1: answer + point.
                Stopwatch stopwatch = Stopwatch.StartNew();
                string firstBubble = WaitFor(runner.AnswerAsync(
                    "Remember the code word PELICAN for later. Now point at the orange box that says CLICKY TARGET."));
                output.WriteLine($"Turn 1 ({stopwatch.ElapsedMilliseconds} ms): {firstBubble}");
                Assert.DoesNotContain("couldn't answer", firstBubble);
                Assert.DoesNotContain("[POINT", firstBubble);
                Assert.Contains(CompanionVoiceState.Processing, voiceStates);
                Assert.Equal(CompanionVoiceState.Idle, voiceStates[^1]);
                Assert.Single(history.GetExchanges());
                Assert.True(cursorOverlayManager.IsPointing, "The answer did not start a pointing flight.");
                Assert.True(cursorOverlayManager.IsShowingResponse);
                Point pointTarget = ResolveRecordedPointTarget(displayMonitorCatalog);
                output.WriteLine($"Pointed at {pointTarget}; box {targetRect}.");
                Assert.InRange(pointTarget.X, targetRect.Left - TargetHitMarginPixels, targetRect.Right + TargetHitMarginPixels);
                Assert.InRange(pointTarget.Y, targetRect.Top - TargetHitMarginPixels, targetRect.Bottom + TargetHitMarginPixels);
            }
            finally
            {
                targetWindow.Close();
            }

            // Show Clicky off: the overlay stays for the pointing flight and the 6 s bubble, then hides.
            DisplayMonitor primaryMonitor = displayMonitorCatalog.Monitors[0];
            double mouseX = primaryMonitor.Bounds.Left + 30, mouseY = primaryMonitor.Bounds.Top + 30;
            for (int frame = 0; frame < 100; frame++) cursorOverlayManager.AdvanceFrame(mouseX, mouseY, 0.05);
            Assert.True(cursorOverlayManager.IsOverlayVisible, "Overlay hid before the reply bubble finished.");
            for (int frame = 0; frame < 300 && cursorOverlayManager.IsOverlayVisible; frame++)
                cursorOverlayManager.AdvanceFrame(mouseX, mouseY, 0.05);
            Assert.False(cursorOverlayManager.IsOverlayVisible, "Overlay never hid after the interaction.");

            // Turn 2: only the history can answer this.
            string secondBubble = WaitFor(runner.AnswerAsync("What code word did I ask you to remember? Don't point at anything."));
            output.WriteLine($"Turn 2: {secondBubble}");
            Assert.Contains("pelican", secondBubble, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, history.GetExchanges().Count);
            Assert.DoesNotContain("[POINT", history.GetExchanges()[0].AssistantResponse);
        });
    }

    [Fact]
    public void Router_FallsBackWhenTheRoutedBackendFails()
    {
        if (!IsEnabled) return;
        // A Worker URL nothing listens on fails fast, whatever the user's Worker setup is.
        ClickyConfiguration configuration = ClickyConfigurationStore.LoadOrCreateDefault().CurrentConfiguration;
        ChatBackendRouter router = new(() => new JobBackendRouting
            { Answer = ChatBackendKind.CloudflareWorker, Fallback = ChatBackendKind.ClaudeCodeCli },
        [
            new ClaudeCodeCliChatBackend(configuration.ClaudeCodeCli),
            new CloudflareWorkerChatBackend(new CloudflareWorkerBackendSettings { WorkerBaseUrl = "http://127.0.0.1:9" })
        ]);

        RoutedChatResponse response = router.GetResponseAsync(CompanionJobKind.Answer,
            new CompanionChatRequest(CompanionSystemPrompts.CompanionVoiceResponseSystemPrompt, [], [], "Say hello in three words."),
            CancellationToken.None).GetAwaiter().GetResult();

        output.WriteLine($"{response.AnsweringBackendKind}: {response.ResponseText}");
        Assert.Equal(ChatBackendKind.ClaudeCodeCli, response.AnsweringBackendKind);
        Assert.False(string.IsNullOrWhiteSpace(response.ResponseText));
    }

    private ChatBackendRouter CreateRouter(JobBackendRouting routing)
    {
        ClickyConfiguration configuration = ClickyConfigurationStore.LoadOrCreateDefault().CurrentConfiguration;
        return new ChatBackendRouter(() => routing,
        [
            new RecordingBackend(new ClaudeCodeCliChatBackend(configuration.ClaudeCodeCli), this),
            new RecordingBackend(new CodexCliChatBackend(configuration.CodexCli), this),
            new RecordingBackend(new CloudflareWorkerChatBackend(configuration.CloudflareWorker), this)
        ]);
    }

    /// <summary>Keeps the backend's raw reply (with its [POINT] tag), which the runner only uses internally.</summary>
    private sealed class RecordingBackend(ICompanionChatBackend innerBackend, LiveBackendVerificationTests owner) : ICompanionChatBackend
    {
        public ChatBackendKind BackendKind => innerBackend.BackendKind;
        public string DisplayName => innerBackend.DisplayName;

        public async Task<string> GetResponseTextAsync(CompanionChatRequest companionChatRequest, CancellationToken cancellationToken)
        {
            string responseText = await innerBackend.GetResponseTextAsync(companionChatRequest, cancellationToken);
            owner.lastRawResponse = responseText;
            return responseText;
        }
    }

    /// <summary>Re-resolves the turn's raw [POINT] tag the way the runner did (same monitors, same screenshot sizes).</summary>
    private Point ResolveRecordedPointTarget(DisplayMonitorCatalog displayMonitorCatalog)
    {
        Assert.NotNull(lastRawResponse);
        PointingParseResult pointingParseResult = PointTagParser.ParsePointingCoordinates(lastRawResponse!);
        var screenCaptures = Task.Run(() => Clicky.Windows.ScreenCapture.CompanionScreenCaptureUtility
            .CaptureAllScreensAsJpegAsync(displayMonitorCatalog.Monitors, CancellationToken.None)).GetAwaiter().GetResult();
        Point? pointTarget = CompanionScreenQuestion.ResolvePointTarget(pointingParseResult, screenCaptures);
        Assert.True(pointTarget.HasValue, "The answer had no usable [POINT] tag.");
        return pointTarget!.Value;
    }

    private string? lastRawResponse;

    /// <summary>Pumps the dispatcher (the runner awaits on it, like in the app) until <paramref name="task"/> ends.</summary>
    private static T WaitFor<T>(Task<T> task)
    {
        DispatcherFrame frame = new();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
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
        DispatcherFrame frame = new();
        new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            frame.Continue = false;
        }, Dispatcher.CurrentDispatcher).Start();
        Dispatcher.PushFrame(frame);
        return targetWindow;
    }

    private static void RunOnStaThreadWithDispatcher(Action testAction)
    {
        Exception? testException = null;
        Thread testThread = new(() =>
        {
            IntPtr previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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

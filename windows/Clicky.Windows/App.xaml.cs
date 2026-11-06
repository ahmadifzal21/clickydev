using System.Windows;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
using Clicky.Windows.Logging;
using Clicky.Windows.Overlay;
using Clicky.Windows.ScreenCapture;
using Clicky.Windows.Tray;
using Clicky.Windows.Voice;

namespace Clicky.Windows;

/// <summary>
/// Tray-only app entry point (the Windows counterpart of leanring_buddyApp.swift +
/// CompanionAppDelegate). There is no main window; Clicky lives in the system tray, and a
/// left-click on the tray icon toggles the floating companion panel.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Named mutex that keeps a second copy of Clicky from starting. Two copies
    /// would install two keyboard hooks and draw two cursors.
    /// </summary>
    private const string SingleInstanceMutexName = "Clicky.Windows.SingleInstance";

    private Mutex? singleInstanceMutex;
    private ClickyConfigurationStore? clickyConfigurationStore;
    private CompanionPanelWindow? companionPanelWindow;
    private TrayContextMenu? trayContextMenu;
    private TrayIconManager? trayIconManager;
    private DisplayMonitorCatalog? displayMonitorCatalog;
    private CursorOverlayManager? cursorOverlayManager;
    private GlobalPushToTalkKeyboardHook? pushToTalkKeyboardHook;
    private ISpeechToTextTranscriber? speechToTextTranscriber;
    private PushToTalkDictation? pushToTalkDictation;
    private ITextToSpeechSpeaker? textToSpeechSpeaker;
    private readonly CancellationTokenSource appExitCancellation = new();
    private TypedScreenQuestionRunner? typedScreenQuestionRunner;

    protected override void OnStartup(StartupEventArgs startupEventArgs)
    {
        base.OnStartup(startupEventArgs);

        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstRunningInstance);
        if (!isFirstRunningInstance)
        {
            Shutdown();
            return;
        }

        ClickyLogFile.Start();
        clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreateDefault();
        displayMonitorCatalog = new DisplayMonitorCatalog();
        Console.WriteLine($"Clicky config loaded from {clickyConfigurationStore.ConfigurationFilePath}");

        companionPanelWindow = new CompanionPanelWindow(clickyConfigurationStore);
        companionPanelWindow.QuitRequested += Shutdown;
        cursorOverlayManager = new CursorOverlayManager(displayMonitorCatalog,
            clickyConfigurationStore.CurrentConfiguration.IsClickyCursorAlwaysVisible);
        cursorOverlayManager.CompanionStatusChanged += companionPanelWindow.SetCompanionStatus;
        clickyConfigurationStore.ConfigurationChanged += ApplyShowClickySetting;
        SyncStartWithWindowsFromRegistry();
        clickyConfigurationStore.ConfigurationChanged += ApplyStartWithWindowsSetting;

        speechToTextTranscriber = SpeechToTextTranscriberFactory.Create(clickyConfigurationStore.CurrentConfiguration.Voice);
        speechToTextTranscriber.StatusChanged += status => Dispatcher.BeginInvoke(() => companionPanelWindow.SetVoiceNote(status));
        speechToTextTranscriber.Prepare(clickyConfigurationStore.CurrentConfiguration.Voice);
        textToSpeechSpeaker = TextToSpeechSpeakerFactory.Create(clickyConfigurationStore.CurrentConfiguration.Voice);
        textToSpeechSpeaker.StatusChanged += status => Dispatcher.BeginInvoke(() => companionPanelWindow.SetVoiceNote(status));
        textToSpeechSpeaker.Prepare(clickyConfigurationStore.CurrentConfiguration.Voice);
        clickyConfigurationStore.ConfigurationChanged += ApplyVoiceSettings;
        // One owner for the voice state and one answer path, shared by push-to-talk and the typed question.
        CompanionInteractionStateMachine companionInteractionStateMachine = new(cursorOverlayManager.SetVoiceState);
        TypedScreenQuestionRunner typedScreenQuestionRunner = new(cursorOverlayManager, displayMonitorCatalog,
            TypedScreenQuestionRunner.CreateRouter(clickyConfigurationStore), new CompanionConversationHistory(),
            companionInteractionStateMachine, textToSpeechSpeaker);
        _ = ShowAnswersInPanelAsync(typedScreenQuestionRunner.TurnEventLog.Follow(0, appExitCancellation.Token));
        pushToTalkDictation = new PushToTalkDictation(cursorOverlayManager, speechToTextTranscriber,
            companionPanelWindow.SetVoiceNote, Dispatcher, interactionStateMachine: companionInteractionStateMachine,
            answerTranscriptAsync: (transcript, turnId, cancellationToken) =>
                typedScreenQuestionRunner.AnswerAsync(transcript, callerCancellationToken: cancellationToken, turnId: turnId,
                    screenCapturesTask: typedScreenQuestionRunner.TakeCaptureFromPress()),
            turnEventLog: typedScreenQuestionRunner.TurnEventLog,
            onReleaseWithoutQuestion: typedScreenQuestionRunner.DiscardPrestartedAnswer);
        this.typedScreenQuestionRunner = typedScreenQuestionRunner;

        // An invalid shortcut falls back to ctrl+alt (TryParse's out value); the panel explains why.
        PushToTalkShortcut.TryParse(clickyConfigurationStore.CurrentConfiguration.PushToTalkShortcut, out PushToTalkShortcut pushToTalkShortcut);
        pushToTalkKeyboardHook = new GlobalPushToTalkKeyboardHook(pushToTalkShortcut);
        // Hold to talk, or double-tap to listen hands-free until the next tap (E10 T10.9).
        PushToTalkGestureRecognizer pushToTalkGestureRecognizer = new();
        System.Diagnostics.Stopwatch gestureClock = System.Diagnostics.Stopwatch.StartNew();
        System.Windows.Threading.DispatcherTimer handsFreeCapTimer = new();
        void ApplyGesture(PushToTalkGestureAction? gestureAction)
        {
            switch (gestureAction)
            {
                case PushToTalkGestureAction.StartListening:
                    // Like the Mac: talking over Clicky cancels its answer and cuts its speech off.
                    typedScreenQuestionRunner.InterruptCurrentAnswer();
                    pushToTalkDictation.HandleShortcutTransition(PushToTalkShortcutTransition.Pressed);
                    // After the microphone has started; the screen is captured while the user talks (E10 T10.2).
                    typedScreenQuestionRunner.CaptureOnPress();
                    break;
                case PushToTalkGestureAction.StopAndAnswer:
                    pushToTalkDictation.HandleShortcutTransition(PushToTalkShortcutTransition.Released);
                    break;
                case PushToTalkGestureAction.StopAndDiscard:
                    pushToTalkDictation.DiscardRecording();
                    break;
            }
            handsFreeCapTimer.Stop();
            if (pushToTalkGestureRecognizer.HandsFreeDeadline is { } handsFreeDeadline)
            {
                if (gestureAction is null) companionPanelWindow.SetVoiceNote("Listening hands-free. Tap the shortcut again to send.");
                handsFreeCapTimer.Interval = TimeSpan.FromTicks(Math.Max(1, (handsFreeDeadline - gestureClock.Elapsed).Ticks));
                handsFreeCapTimer.Start();
            }
        }
        handsFreeCapTimer.Tick += (_, _) => ApplyGesture(pushToTalkGestureRecognizer.HandleTimeElapsed(gestureClock.Elapsed));
        // Raised on the hook thread, which must return fast; the overlay lives on the UI thread.
        // The time is taken here, when the key moved: the UI thread can be busy for a while starting to listen, and a tap
        // timed on arrival there would look like a hold.
        pushToTalkKeyboardHook.ShortcutTransitioned += shortcutTransition =>
        {
            TimeSpan transitionTime = gestureClock.Elapsed;
            Dispatcher.BeginInvoke(() =>
            {
                PushToTalkGestureAction? gestureAction = pushToTalkGestureRecognizer.Handle(shortcutTransition, transitionTime);
                // Tap and gap timings vary by person and shortcut; this line is how they get tuned.
                Console.WriteLine($"Push-to-talk {shortcutTransition} at {transitionTime.TotalMilliseconds:F0} ms -> " +
                    $"{gestureAction?.ToString() ?? (pushToTalkGestureRecognizer.IsHandsFree ? "hands-free" : "nothing")}");
                ApplyGesture(gestureAction);
            });
        };
        if (!pushToTalkKeyboardHook.Start())
        {
            Console.WriteLine("Clicky couldn't install its push-to-talk keyboard hook.");
        }

        clickyConfigurationStore.ConfigurationChanged += ApplyPushToTalkShortcutSetting;

        PointingTestTour pointingTestTour = new(cursorOverlayManager, displayMonitorCatalog);
        trayContextMenu = new TrayContextMenu(clickyConfigurationStore, askAboutScreenRequested: typedScreenQuestionRunner.Run,
            testPointingRequested: pointingTestTour.Run, speakTestLineRequested: SpeakTestLine,
            saveDebugScreenshotsRequested: SaveDebugScreenshots, quitRequested: Shutdown);
        trayIconManager = new TrayIconManager(trayContextMenu.ContextMenuStrip);
        trayIconManager.TrayIconLeftClicked += companionPanelWindow.TogglePanel;
        clickyConfigurationStore.WatchForHandEdits(Dispatcher);
    }

    private void ApplyShowClickySetting(ClickyConfiguration clickyConfiguration)
    {
        cursorOverlayManager?.SetCursorAlwaysVisible(clickyConfiguration.IsClickyCursorAlwaysVisible);
    }

    /// <summary>The registry is the truth on start (the user may have removed the value, or moved the exe).</summary>
    private void SyncStartWithWindowsFromRegistry()
    {
        bool isRegistered;
        try
        {
            isRegistered = WindowsStartupRegistration.IsEnabled();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Couldn't read the Start with Windows setting: {exception.Message}");
            return;
        }

        if (clickyConfigurationStore!.CurrentConfiguration.StartWithWindows != isRegistered)
            clickyConfigurationStore.UpdateAndSave(configuration => configuration.StartWithWindows = isRegistered);
    }

    private void ApplyStartWithWindowsSetting(ClickyConfiguration clickyConfiguration)
    {
        try
        {
            if (WindowsStartupRegistration.IsEnabled() == clickyConfiguration.StartWithWindows) return;
            WindowsStartupRegistration.SetEnabled(clickyConfiguration.StartWithWindows);
            Console.WriteLine($"Start with Windows turned {(clickyConfiguration.StartWithWindows ? "on" : "off")}.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Couldn't change Start with Windows: {exception.Message}");
            // Put the toggle back to what the registry really says; that save finds them equal and stops.
            Dispatcher.BeginInvoke(SyncStartWithWindowsFromRegistry);
        }
    }

    private void ApplyVoiceSettings(ClickyConfiguration clickyConfiguration)
    {
        speechToTextTranscriber?.Prepare(clickyConfiguration.Voice);
        textToSpeechSpeaker?.Prepare(clickyConfiguration.Voice);
    }

    /// <summary>Tray debug item (E7 checkpoint): speaks a reply-length line; push-to-talk or a second click interrupts it.</summary>
    private async void SpeakTestLine()
    {
        if (textToSpeechSpeaker is null) return;
        System.Diagnostics.Stopwatch speechStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await textToSpeechSpeaker.SpeakAsync("Hi, I'm Clicky. Hold control and alt, ask me about anything on your screen, " +
            "and I'll point you right to it.", CancellationToken.None);
        Console.WriteLine($"Speak test line finished after {speechStopwatch.ElapsedMilliseconds} ms");
    }

    private async void SaveDebugScreenshots()
    {
        if (displayMonitorCatalog is null) return;
        try
        {
            await DebugScreenshotWriter.SaveAndOpenFolderAsync(displayMonitorCatalog.Monitors);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Clicky couldn't save debug screenshots: {exception.Message}", "Clicky");
        }
    }

    private void ApplyPushToTalkShortcutSetting(ClickyConfiguration clickyConfiguration)
    {
        PushToTalkShortcut.TryParse(clickyConfiguration.PushToTalkShortcut, out PushToTalkShortcut pushToTalkShortcut);
        pushToTalkKeyboardHook?.ChangeShortcut(pushToTalkShortcut);
    }

    /// <summary>The panel follows the turn event log (from the start, so nothing is missed) and shows how each turn ended.</summary>
    private async Task ShowAnswersInPanelAsync(System.Threading.Channels.ChannelReader<CompanionTurnEvent> turnEvents)
    {
        try
        {
            await foreach (CompanionTurnEvent turnEvent in turnEvents.ReadAllAsync())
            {
                if (CompanionPanelText.GetTurnNote(turnEvent) is { } turnNote) companionPanelWindow?.SetVoiceNote(turnNote);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs exitEventArgs)
    {
        appExitCancellation.Cancel();
        // Tray icon first: Dispose hides it, so no ghost icon is left behind.
        trayIconManager?.Dispose();
        trayContextMenu?.Dispose();

        if (clickyConfigurationStore is not null)
        {
            clickyConfigurationStore.ConfigurationChanged -= ApplyShowClickySetting;
            clickyConfigurationStore.ConfigurationChanged -= ApplyStartWithWindowsSetting;
            clickyConfigurationStore.ConfigurationChanged -= ApplyPushToTalkShortcutSetting;
            clickyConfigurationStore.ConfigurationChanged -= ApplyVoiceSettings;
            clickyConfigurationStore.Dispose();
        }

        pushToTalkKeyboardHook?.Dispose();
        typedScreenQuestionRunner?.DiscardPrestartedAnswer(); // Its CLI process would outlive Clicky otherwise.
        speechToTextTranscriber?.Dispose();
        textToSpeechSpeaker?.Dispose();

        companionPanelWindow?.Close();
        cursorOverlayManager?.Dispose();
        displayMonitorCatalog?.Dispose();
        singleInstanceMutex?.Dispose();
        base.OnExit(exitEventArgs);
    }
}

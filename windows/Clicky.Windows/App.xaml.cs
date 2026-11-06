using System.Windows;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
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

    protected override void OnStartup(StartupEventArgs startupEventArgs)
    {
        base.OnStartup(startupEventArgs);

        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstRunningInstance);
        if (!isFirstRunningInstance)
        {
            Shutdown();
            return;
        }

        clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreateDefault();
        displayMonitorCatalog = new DisplayMonitorCatalog();
        Console.WriteLine($"Clicky config loaded from {clickyConfigurationStore.ConfigurationFilePath}");

        companionPanelWindow = new CompanionPanelWindow(clickyConfigurationStore);
        companionPanelWindow.QuitRequested += Shutdown;
        cursorOverlayManager = new CursorOverlayManager(displayMonitorCatalog,
            clickyConfigurationStore.CurrentConfiguration.IsClickyCursorAlwaysVisible);
        cursorOverlayManager.CompanionStatusChanged += companionPanelWindow.SetCompanionStatus;
        clickyConfigurationStore.ConfigurationChanged += ApplyShowClickySetting;

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
        pushToTalkDictation = new PushToTalkDictation(cursorOverlayManager, speechToTextTranscriber,
            companionPanelWindow.SetVoiceNote, Dispatcher, interactionStateMachine: companionInteractionStateMachine,
            answerTranscriptAsync: (transcript, cancellationToken) =>
                typedScreenQuestionRunner.AnswerAsync(transcript, callerCancellationToken: cancellationToken));

        // An invalid shortcut falls back to ctrl+alt (TryParse's out value); the panel explains why.
        PushToTalkShortcut.TryParse(clickyConfigurationStore.CurrentConfiguration.PushToTalkShortcut, out PushToTalkShortcut pushToTalkShortcut);
        pushToTalkKeyboardHook = new GlobalPushToTalkKeyboardHook(pushToTalkShortcut);
        // Raised on the hook thread, which must return fast; the overlay lives on the UI thread.
        pushToTalkKeyboardHook.ShortcutTransitioned += shortcutTransition =>
            Dispatcher.BeginInvoke(() =>
            {
                // Like the Mac: talking over Clicky cancels its answer and cuts its speech off.
                if (shortcutTransition == PushToTalkShortcutTransition.Pressed) typedScreenQuestionRunner.InterruptCurrentAnswer();
                pushToTalkDictation.HandleShortcutTransition(shortcutTransition);
            });
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

    protected override void OnExit(ExitEventArgs exitEventArgs)
    {
        // Tray icon first: Dispose hides it, so no ghost icon is left behind.
        trayIconManager?.Dispose();
        trayContextMenu?.Dispose();

        if (clickyConfigurationStore is not null)
        {
            clickyConfigurationStore.ConfigurationChanged -= ApplyShowClickySetting;
            clickyConfigurationStore.ConfigurationChanged -= ApplyPushToTalkShortcutSetting;
            clickyConfigurationStore.ConfigurationChanged -= ApplyVoiceSettings;
            clickyConfigurationStore.Dispose();
        }

        pushToTalkKeyboardHook?.Dispose();
        speechToTextTranscriber?.Dispose();
        textToSpeechSpeaker?.Dispose();

        companionPanelWindow?.Close();
        cursorOverlayManager?.Dispose();
        displayMonitorCatalog?.Dispose();
        singleInstanceMutex?.Dispose();
        base.OnExit(exitEventArgs);
    }
}

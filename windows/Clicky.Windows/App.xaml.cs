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
        clickyConfigurationStore.ConfigurationChanged += ApplyVoiceSettings;
        pushToTalkDictation = new PushToTalkDictation(cursorOverlayManager, speechToTextTranscriber,
            companionPanelWindow.SetVoiceNote, Dispatcher);

        // An invalid shortcut falls back to ctrl+alt (TryParse's out value); the panel explains why.
        PushToTalkShortcut.TryParse(clickyConfigurationStore.CurrentConfiguration.PushToTalkShortcut, out PushToTalkShortcut pushToTalkShortcut);
        pushToTalkKeyboardHook = new GlobalPushToTalkKeyboardHook(pushToTalkShortcut);
        // Raised on the hook thread, which must return fast; the overlay lives on the UI thread.
        pushToTalkKeyboardHook.ShortcutTransitioned += shortcutTransition =>
            Dispatcher.BeginInvoke(() => pushToTalkDictation.HandleShortcutTransition(shortcutTransition));
        if (!pushToTalkKeyboardHook.Start())
        {
            Console.WriteLine("Clicky couldn't install its push-to-talk keyboard hook.");
        }

        clickyConfigurationStore.ConfigurationChanged += ApplyPushToTalkShortcutSetting;

        PointingTestTour pointingTestTour = new(cursorOverlayManager, displayMonitorCatalog);
        TypedScreenQuestionRunner typedScreenQuestionRunner = new(cursorOverlayManager, displayMonitorCatalog,
            TypedScreenQuestionRunner.CreateRouter(clickyConfigurationStore), new CompanionConversationHistory());
        trayContextMenu = new TrayContextMenu(clickyConfigurationStore, askAboutScreenRequested: typedScreenQuestionRunner.Run,
            testPointingRequested: pointingTestTour.Run,
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

        companionPanelWindow?.Close();
        cursorOverlayManager?.Dispose();
        displayMonitorCatalog?.Dispose();
        singleInstanceMutex?.Dispose();
        base.OnExit(exitEventArgs);
    }
}

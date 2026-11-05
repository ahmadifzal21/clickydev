using System.Windows;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;
using Clicky.Windows.Overlay;
using Clicky.Windows.Tray;

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

        trayContextMenu = new TrayContextMenu(clickyConfigurationStore, quitRequested: Shutdown);
        trayIconManager = new TrayIconManager(trayContextMenu.ContextMenuStrip);
        trayIconManager.TrayIconLeftClicked += companionPanelWindow.TogglePanel;
    }

    private void ApplyShowClickySetting(ClickyConfiguration clickyConfiguration)
    {
        cursorOverlayManager?.SetCursorAlwaysVisible(clickyConfiguration.IsClickyCursorAlwaysVisible);
    }

    protected override void OnExit(ExitEventArgs exitEventArgs)
    {
        // Tray icon first: Dispose hides it, so no ghost icon is left behind.
        trayIconManager?.Dispose();
        trayContextMenu?.Dispose();

        if (clickyConfigurationStore is not null)
        {
            clickyConfigurationStore.ConfigurationChanged -= ApplyShowClickySetting;
        }

        companionPanelWindow?.Close();
        cursorOverlayManager?.Dispose();
        displayMonitorCatalog?.Dispose();
        singleInstanceMutex?.Dispose();
        base.OnExit(exitEventArgs);
    }
}

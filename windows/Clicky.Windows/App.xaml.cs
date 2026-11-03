using System.Windows;
using Clicky.Windows.Configuration;

namespace Clicky.Windows;

/// <summary>
/// Tray-only app entry point (the Windows counterpart of leanring_buddyApp.swift +
/// CompanionAppDelegate). There is no main window; Clicky lives in the system tray.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Named mutex that keeps a second copy of Clicky from starting. Two copies
    /// would install two keyboard hooks and draw two cursors.
    /// </summary>
    private const string SingleInstanceMutexName = "Clicky.Windows.SingleInstance";

    private Mutex? singleInstanceMutex;
    private System.Windows.Forms.NotifyIcon? temporaryTrayIcon;

    protected override void OnStartup(StartupEventArgs startupEventArgs)
    {
        base.OnStartup(startupEventArgs);

        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstRunningInstance);
        if (!isFirstRunningInstance)
        {
            Shutdown();
            return;
        }

        ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreateDefault();
        Console.WriteLine($"Clicky config loaded from {clickyConfigurationStore.ConfigurationFilePath}");

        // Skeleton placeholder: a plain tray icon with Quit so the app is visible
        // and can be closed. Step 1 replaces this with TrayIconManager + the floating panel.
        temporaryTrayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Clicky",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        temporaryTrayIcon.ContextMenuStrip.Items.Add("Quit Clicky", image: null, onClick: (_, _) => Shutdown());
    }

    protected override void OnExit(ExitEventArgs exitEventArgs)
    {
        if (temporaryTrayIcon is not null)
        {
            // Hide before disposing, otherwise a ghost icon stays in the tray until hovered.
            temporaryTrayIcon.Visible = false;
            temporaryTrayIcon.Dispose();
        }

        singleInstanceMutex?.Dispose();
        base.OnExit(exitEventArgs);
    }
}

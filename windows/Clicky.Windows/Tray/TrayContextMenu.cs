using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tray;

/// <summary>
/// Builds the tray icon's right-click menu: one submenu per backend picker (radio-style checks
/// kept in sync with the config), Open config file, a config error line when the file couldn't be
/// parsed, and Quit. Mirrors the pickers in the companion panel.
/// </summary>
internal sealed class TrayContextMenu : IDisposable
{
    private readonly ClickyConfigurationStore clickyConfigurationStore;
    private readonly List<(BackendRoutingPickerRow PickerRow, System.Windows.Forms.ToolStripMenuItem OptionItem, ChatBackendKind? Option)> backendOptionMenuItems = [];

    public System.Windows.Forms.ContextMenuStrip ContextMenuStrip { get; } = new();

    public TrayContextMenu(ClickyConfigurationStore clickyConfigurationStore, Action testPointingRequested,
        Action saveDebugScreenshotsRequested, Action quitRequested)
    {
        this.clickyConfigurationStore = clickyConfigurationStore;

        foreach (BackendRoutingPickerRow pickerRow in BackendRoutingPickerRows.All)
        {
            System.Windows.Forms.ToolStripMenuItem pickerSubmenu = new($"{pickerRow.Title} with");
            foreach (ChatBackendKind? option in pickerRow.Options)
            {
                System.Windows.Forms.ToolStripMenuItem optionItem = new(BackendRoutingPickerRows.GetOptionDisplayName(option));
                optionItem.Click += (_, _) => clickyConfigurationStore.UpdateAndSave(configuration => pickerRow.SetSelectedBackend(configuration, option));
                pickerSubmenu.DropDownItems.Add(optionItem);
                backendOptionMenuItems.Add((pickerRow, optionItem, option));
            }

            ContextMenuStrip.Items.Add(pickerSubmenu);
        }

        ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        if (clickyConfigurationStore.ConfigurationLoadErrorMessage is not null)
        {
            // Disabled, informational: the panel shows the full message.
            ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Config file has errors — using defaults") { Enabled = false });
        }

        ContextMenuStrip.Items.Add("Open config file", image: null,
            onClick: (_, _) => BackendRoutingPickerRows.OpenConfigurationFileInEditor(clickyConfigurationStore.ConfigurationFilePath));
        // Debug: flies to known spots on every monitor to check pointing at the user's scaling settings.
        ContextMenuStrip.Items.Add("Test pointing", image: null, onClick: (_, _) => testPointingRequested());
        // Debug: writes the screenshots the model would see, to check capture, ordering and overlay exclusion.
        ContextMenuStrip.Items.Add("Save debug screenshots", image: null, onClick: (_, _) => saveDebugScreenshotsRequested());
        ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        ContextMenuStrip.Items.Add("Quit Clicky", image: null, onClick: (_, _) => quitRequested());

        clickyConfigurationStore.ConfigurationChanged += RefreshCheckMarks;
        RefreshCheckMarks(clickyConfigurationStore.CurrentConfiguration);
    }

    private void RefreshCheckMarks(ClickyConfiguration clickyConfiguration)
    {
        foreach ((BackendRoutingPickerRow pickerRow, System.Windows.Forms.ToolStripMenuItem optionItem, ChatBackendKind? option) in backendOptionMenuItems)
        {
            optionItem.Checked = pickerRow.GetSelectedBackend(clickyConfiguration) == option;
        }
    }

    public void Dispose()
    {
        clickyConfigurationStore.ConfigurationChanged -= RefreshCheckMarks;
        ContextMenuStrip.Dispose();
    }
}

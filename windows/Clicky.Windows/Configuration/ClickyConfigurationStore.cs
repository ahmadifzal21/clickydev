using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Clicky.Windows.Configuration;

/// <summary>
/// Loads and saves clicky.config.json. The file is looked up next to Clicky.exe first
/// (so a portable copy carries its own config), otherwise in %APPDATA%\Clicky\.
/// Changes made from the tray menu go through <see cref="UpdateAndSave"/>; hand edits to the file are picked up by
/// <see cref="WatchForHandEdits"/>.
/// </summary>
public sealed class ClickyConfigurationStore : IDisposable
{
    public const string ConfigurationFileName = "clicky.config.json";

    internal static readonly JsonSerializerOptions ConfigurationJsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // The default encoder writes "+" as "+" (HTML-safety). This file is only read
        // by Clicky and edited by hand, so keep "ctrl+alt" readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
    };

    public string ConfigurationFilePath { get; }

    public ClickyConfiguration CurrentConfiguration { get; private set; }

    /// <summary>
    /// Set when the file existed but couldn't be parsed. Clicky then runs on defaults
    /// and does NOT overwrite the broken file, so the user's edits aren't lost.
    /// </summary>
    public string? ConfigurationLoadErrorMessage { get; private set; }

    /// <summary>The file text Clicky last read or wrote, so its own saves don't count as hand edits.</summary>
    private string lastKnownConfigurationJson;
    private FileSystemWatcher? configurationFileWatcher;

    /// <summary>Raised after the configuration changes and has been saved.</summary>
    public event Action<ClickyConfiguration>? ConfigurationChanged;

    private ClickyConfigurationStore(string configurationFilePath, ClickyConfiguration loadedConfiguration, string? configurationLoadErrorMessage,
        string lastKnownConfigurationJson)
    {
        ConfigurationFilePath = configurationFilePath;
        CurrentConfiguration = loadedConfiguration;
        ConfigurationLoadErrorMessage = configurationLoadErrorMessage;
        this.lastKnownConfigurationJson = lastKnownConfigurationJson;
    }

    /// <summary>Loads from the default location, writing a defaults file if none exists yet.</summary>
    public static ClickyConfigurationStore LoadOrCreateDefault()
    {
        return LoadOrCreate(ResolveDefaultConfigurationFilePath());
    }

    public static ClickyConfigurationStore LoadOrCreate(string configurationFilePath)
    {
        if (!File.Exists(configurationFilePath))
        {
            ClickyConfiguration defaultConfiguration = new();
            string defaultConfigurationJson = WriteConfigurationFile(configurationFilePath, defaultConfiguration);
            return new ClickyConfigurationStore(configurationFilePath, defaultConfiguration, configurationLoadErrorMessage: null, defaultConfigurationJson);
        }

        string configurationJson = "";
        try
        {
            configurationJson = File.ReadAllText(configurationFilePath);
            ClickyConfiguration loadedConfiguration = ParseConfigurationJson(configurationJson);
            return new ClickyConfigurationStore(configurationFilePath, loadedConfiguration, configurationLoadErrorMessage: null, configurationJson);
        }
        catch (Exception configurationLoadException) when (configurationLoadException is JsonException or IOException)
        {
            return new ClickyConfigurationStore(configurationFilePath, new ClickyConfiguration(), configurationLoadException.Message, configurationJson);
        }
    }

    /// <summary>Applies a change, saves the file, and notifies listeners.</summary>
    public void UpdateAndSave(Action<ClickyConfiguration> applyConfigurationChange)
    {
        applyConfigurationChange(CurrentConfiguration);

        // If the file on disk is broken, saving would silently replace the user's
        // hand edits with defaults. Keep the change in memory only.
        if (ConfigurationLoadErrorMessage is null)
        {
            lastKnownConfigurationJson = WriteConfigurationFile(ConfigurationFilePath, CurrentConfiguration);
        }

        ConfigurationChanged?.Invoke(CurrentConfiguration);
    }

    /// <summary>
    /// Re-reads the file after a hand edit and notifies listeners if it changed. A file that doesn't parse (say, one
    /// saved mid-edit) keeps the current settings and blocks tray saves until it's fixed, as at start-up.
    /// </summary>
    /// <returns>True when new settings were applied.</returns>
    public bool ReloadFromDisk()
    {
        string configurationJson;
        ClickyConfiguration reloadedConfiguration;
        try
        {
            configurationJson = File.ReadAllText(ConfigurationFilePath);
            if (configurationJson == lastKnownConfigurationJson)
            {
                return false;
            }

            reloadedConfiguration = ParseConfigurationJson(configurationJson);
        }
        catch (Exception configurationReloadException) when (configurationReloadException is JsonException or IOException)
        {
            ConfigurationLoadErrorMessage = configurationReloadException.Message;
            Console.WriteLine($"Clicky config edit not applied: {configurationReloadException.Message}");
            return false;
        }

        lastKnownConfigurationJson = configurationJson;
        ConfigurationLoadErrorMessage = null;
        CurrentConfiguration = reloadedConfiguration;
        Console.WriteLine("Clicky config reloaded after an edit.");
        ConfigurationChanged?.Invoke(CurrentConfiguration);
        return true;
    }

    /// <summary>
    /// Reloads, on <paramref name="dispatcher"/>'s thread, whenever the file is saved from an editor. Editors save in
    /// bursts of events, so a reload waits until the file has been quiet for a moment.
    /// </summary>
    public void WatchForHandEdits(Dispatcher dispatcher)
    {
        DispatcherTimer reloadDebounceTimer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            ReloadFromDisk();
        }, dispatcher);
        reloadDebounceTimer.Stop();

        void RestartReloadDebounce() => dispatcher.BeginInvoke(() =>
        {
            reloadDebounceTimer.Stop();
            reloadDebounceTimer.Start();
        });

        configurationFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(ConfigurationFilePath))!,
            Path.GetFileName(ConfigurationFilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };
        configurationFileWatcher.Changed += (_, _) => RestartReloadDebounce();
        configurationFileWatcher.Created += (_, _) => RestartReloadDebounce();
        configurationFileWatcher.Renamed += (_, _) => RestartReloadDebounce();
        configurationFileWatcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        configurationFileWatcher?.Dispose();
    }

    internal static ClickyConfiguration ParseConfigurationJson(string configurationJson)
    {
        // An empty file or a literal "null" falls back to defaults rather than failing.
        return JsonSerializer.Deserialize<ClickyConfiguration>(configurationJson, ConfigurationJsonSerializerOptions)
            ?? new ClickyConfiguration();
    }

    internal static string SerializeConfigurationJson(ClickyConfiguration clickyConfiguration)
    {
        return JsonSerializer.Serialize(clickyConfiguration, ConfigurationJsonSerializerOptions);
    }

    /// <returns>The JSON written.</returns>
    private static string WriteConfigurationFile(string configurationFilePath, ClickyConfiguration clickyConfiguration)
    {
        string? configurationDirectoryPath = Path.GetDirectoryName(configurationFilePath);
        if (!string.IsNullOrEmpty(configurationDirectoryPath))
        {
            Directory.CreateDirectory(configurationDirectoryPath);
        }

        // Write to a temp file then swap, so a crash mid-write can't leave a truncated config.
        string temporaryConfigurationFilePath = configurationFilePath + ".tmp";
        string configurationJson = SerializeConfigurationJson(clickyConfiguration);
        File.WriteAllText(temporaryConfigurationFilePath, configurationJson);
        File.Move(temporaryConfigurationFilePath, configurationFilePath, overwrite: true);
        return configurationJson;
    }

    private static string ResolveDefaultConfigurationFilePath()
    {
        string configurationFilePathNextToExecutable = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);
        if (File.Exists(configurationFilePathNextToExecutable))
        {
            return configurationFilePathNextToExecutable;
        }

        string applicationDataDirectoryPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(applicationDataDirectoryPath, "Clicky", ConfigurationFileName);
    }
}

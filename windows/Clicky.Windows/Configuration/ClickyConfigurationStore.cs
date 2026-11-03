using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky.Windows.Configuration;

/// <summary>
/// Loads and saves clicky.config.json. The file is looked up next to Clicky.exe first
/// (so a portable copy carries its own config), otherwise in %APPDATA%\Clicky\.
/// Changes made from the tray menu go through <see cref="UpdateAndSave"/>.
/// </summary>
public sealed class ClickyConfigurationStore
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
    public string? ConfigurationLoadErrorMessage { get; }

    /// <summary>Raised after the configuration changes and has been saved.</summary>
    public event Action<ClickyConfiguration>? ConfigurationChanged;

    private ClickyConfigurationStore(string configurationFilePath, ClickyConfiguration loadedConfiguration, string? configurationLoadErrorMessage)
    {
        ConfigurationFilePath = configurationFilePath;
        CurrentConfiguration = loadedConfiguration;
        ConfigurationLoadErrorMessage = configurationLoadErrorMessage;
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
            WriteConfigurationFile(configurationFilePath, defaultConfiguration);
            return new ClickyConfigurationStore(configurationFilePath, defaultConfiguration, configurationLoadErrorMessage: null);
        }

        try
        {
            string configurationJson = File.ReadAllText(configurationFilePath);
            ClickyConfiguration loadedConfiguration = ParseConfigurationJson(configurationJson);
            return new ClickyConfigurationStore(configurationFilePath, loadedConfiguration, configurationLoadErrorMessage: null);
        }
        catch (Exception configurationLoadException) when (configurationLoadException is JsonException or IOException)
        {
            return new ClickyConfigurationStore(configurationFilePath, new ClickyConfiguration(), configurationLoadException.Message);
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
            WriteConfigurationFile(ConfigurationFilePath, CurrentConfiguration);
        }

        ConfigurationChanged?.Invoke(CurrentConfiguration);
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

    private static void WriteConfigurationFile(string configurationFilePath, ClickyConfiguration clickyConfiguration)
    {
        string? configurationDirectoryPath = Path.GetDirectoryName(configurationFilePath);
        if (!string.IsNullOrEmpty(configurationDirectoryPath))
        {
            Directory.CreateDirectory(configurationDirectoryPath);
        }

        // Write to a temp file then swap, so a crash mid-write can't leave a truncated config.
        string temporaryConfigurationFilePath = configurationFilePath + ".tmp";
        File.WriteAllText(temporaryConfigurationFilePath, SerializeConfigurationJson(clickyConfiguration));
        File.Move(temporaryConfigurationFilePath, configurationFilePath, overwrite: true);
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

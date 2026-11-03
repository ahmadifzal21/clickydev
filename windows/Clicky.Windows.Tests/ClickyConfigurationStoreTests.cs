using System.IO;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

public sealed class ClickyConfigurationStoreTests : IDisposable
{
    private readonly string temporaryTestDirectoryPath =
        Path.Combine(Path.GetTempPath(), "ClickyConfigurationStoreTests-" + Guid.NewGuid().ToString("N"));

    private string TemporaryConfigurationFilePath => Path.Combine(temporaryTestDirectoryPath, ClickyConfigurationStore.ConfigurationFileName);

    public void Dispose()
    {
        if (Directory.Exists(temporaryTestDirectoryPath))
        {
            Directory.Delete(temporaryTestDirectoryPath, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_IsCreatedWithDefaults()
    {
        ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);

        Assert.True(File.Exists(TemporaryConfigurationFilePath));
        Assert.Equal(ChatBackendKind.ClaudeCodeCli, clickyConfigurationStore.CurrentConfiguration.JobRouting.Answer);
        Assert.Equal(ChatBackendKind.CodexCli, clickyConfigurationStore.CurrentConfiguration.JobRouting.Fallback);

        string writtenConfigurationJson = File.ReadAllText(TemporaryConfigurationFilePath);
        Assert.Contains("\"answer\": \"claude-code-cli\"", writtenConfigurationJson);
        Assert.Contains("\"speechToText\": \"whisper-local\"", writtenConfigurationJson);
        Assert.Contains("\"pushToTalkShortcut\": \"ctrl+alt\"", writtenConfigurationJson);
    }

    [Fact]
    public void PartialJson_FillsInDefaults()
    {
        ClickyConfiguration parsedConfiguration = ClickyConfigurationStore.ParseConfigurationJson(
            """
            {
              // comments and trailing commas are allowed for hand editing
              "jobRouting": { "answer": "codex-cli", "fallback": null, },
            }
            """);

        Assert.Equal(ChatBackendKind.CodexCli, parsedConfiguration.JobRouting.Answer);
        Assert.Equal(ChatBackendKind.ClaudeCodeCli, parsedConfiguration.JobRouting.Pointing);
        Assert.Null(parsedConfiguration.JobRouting.Fallback);
        Assert.Equal("ctrl+alt", parsedConfiguration.PushToTalkShortcut);
    }

    [Fact]
    public void UpdateAndSave_PersistsAndNotifies()
    {
        ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        ClickyConfiguration? configurationReceivedByListener = null;
        clickyConfigurationStore.ConfigurationChanged += changedConfiguration => configurationReceivedByListener = changedConfiguration;

        clickyConfigurationStore.UpdateAndSave(configuration =>
            configuration.JobRouting.SetBackendForJob(CompanionJobKind.Pointing, ChatBackendKind.CloudflareWorker));

        Assert.NotNull(configurationReceivedByListener);
        ClickyConfigurationStore reloadedConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        Assert.Equal(ChatBackendKind.CloudflareWorker, reloadedConfigurationStore.CurrentConfiguration.JobRouting.Pointing);
    }

    [Fact]
    public void BrokenFile_FallsBackToDefaults_AndIsNotOverwritten()
    {
        Directory.CreateDirectory(temporaryTestDirectoryPath);
        const string brokenConfigurationJson = "{ \"jobRouting\": { \"answer\": \"not-a-backend\" } }";
        File.WriteAllText(TemporaryConfigurationFilePath, brokenConfigurationJson);

        ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        clickyConfigurationStore.UpdateAndSave(configuration => configuration.PushToTalkShortcut = "ctrl+shift");

        Assert.NotNull(clickyConfigurationStore.ConfigurationLoadErrorMessage);
        Assert.Equal(brokenConfigurationJson, File.ReadAllText(TemporaryConfigurationFilePath));
    }
}

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
        Assert.Contains("\"speechToText\": \"foundry-local\"", writtenConfigurationJson);
        Assert.Contains("\"pushToTalkShortcut\": \"ctrl+alt\"", writtenConfigurationJson);
        Assert.Contains("\"textToSpeech\": \"kokoro-local\"", writtenConfigurationJson);
        Assert.Contains("\"kokoroVoice\": \"af_heart\"", writtenConfigurationJson);
    }

    [Fact]
    public void FileFromBeforeKokoro_WithEdgeTts_IsMigratedToKokoro_AndSaved_KeepingTheUsersOtherSettings()
    {
        Directory.CreateDirectory(temporaryTestDirectoryPath);
        File.WriteAllText(TemporaryConfigurationFilePath, """
            { "jobRouting": { "answer": "codex-cli" },
              "voice": { "textToSpeech": "edge-tts", "edgeTtsVoice": "en-US-AvaMultilingualNeural" } }
            """);

        ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);

        Assert.Null(clickyConfigurationStore.ConfigurationLoadErrorMessage);
        Assert.Equal(ChatBackendKind.CodexCli, clickyConfigurationStore.CurrentConfiguration.JobRouting.Answer);
        Assert.Equal(TextToSpeechProviderKind.KokoroLocal, clickyConfigurationStore.CurrentConfiguration.Voice.TextToSpeech);
        Assert.Equal("af_heart", clickyConfigurationStore.CurrentConfiguration.Voice.KokoroVoice);
        string savedConfigurationJson = File.ReadAllText(TemporaryConfigurationFilePath);
        Assert.Contains("\"textToSpeech\": \"kokoro-local\"", savedConfigurationJson);
        Assert.Contains("\"answer\": \"codex-cli\"", savedConfigurationJson);
        Assert.DoesNotContain("edge", savedConfigurationJson);
    }

    [Fact]
    public void UnknownTextToSpeechValue_IsStillAnError_AndTheFileIsLeftAlone()
    {
        Directory.CreateDirectory(temporaryTestDirectoryPath);
        const string unknownEngineJson = "{ \"voice\": { \"textToSpeech\": \"not-an-engine\" } }";
        File.WriteAllText(TemporaryConfigurationFilePath, unknownEngineJson);

        ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);

        Assert.NotNull(clickyConfigurationStore.ConfigurationLoadErrorMessage);
        Assert.Equal(unknownEngineJson, File.ReadAllText(TemporaryConfigurationFilePath));
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
    private void EditFileByHand(string oldText, string newText) =>
        File.WriteAllText(TemporaryConfigurationFilePath, File.ReadAllText(TemporaryConfigurationFilePath).Replace(oldText, newText));

    [Fact]
    public void HandEdit_IsReloadedAndNotified_Once()
    {
        using ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        List<string> whisperModelsReceivedByListener = [];
        clickyConfigurationStore.ConfigurationChanged += changedConfiguration => whisperModelsReceivedByListener.Add(changedConfiguration.Voice.WhisperModel);

        EditFileByHand("\"base.en\"", "\"small.en\"");

        Assert.True(clickyConfigurationStore.ReloadFromDisk());
        Assert.False(clickyConfigurationStore.ReloadFromDisk()); // Nothing new on disk.
        Assert.Equal(["small.en"], whisperModelsReceivedByListener);
        Assert.Equal("small.en", clickyConfigurationStore.CurrentConfiguration.Voice.WhisperModel);
    }

    [Fact]
    public void OwnSave_IsNotMistakenForAHandEdit()
    {
        using ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        clickyConfigurationStore.UpdateAndSave(configuration => configuration.PushToTalkShortcut = "ctrl+shift");

        Assert.False(clickyConfigurationStore.ReloadFromDisk());
    }

    [Fact]
    public void TraySave_AfterAHandEdit_KeepsTheHandEdit()
    {
        using ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        EditFileByHand("\"base.en\"", "\"small.en\"");
        clickyConfigurationStore.ReloadFromDisk();

        clickyConfigurationStore.UpdateAndSave(configuration =>
            configuration.JobRouting.SetBackendForJob(CompanionJobKind.Pointing, ChatBackendKind.CloudflareWorker));

        ClickyConfiguration savedConfiguration = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath).CurrentConfiguration;
        Assert.Equal("small.en", savedConfiguration.Voice.WhisperModel);
        Assert.Equal(ChatBackendKind.CloudflareWorker, savedConfiguration.JobRouting.Pointing);
    }

    [Fact]
    public void BrokenHandEdit_KeepsSettingsAndTheFile_UntilItIsFixed()
    {
        using ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
        int notificationCount = 0;
        clickyConfigurationStore.ConfigurationChanged += _ => notificationCount++;
        EditFileByHand("\"base.en\"", "\"small.en"); // Saved mid-edit: unterminated string.
        string brokenConfigurationJson = File.ReadAllText(TemporaryConfigurationFilePath);

        Assert.False(clickyConfigurationStore.ReloadFromDisk());
        Assert.NotNull(clickyConfigurationStore.ConfigurationLoadErrorMessage);
        Assert.Equal("base.en", clickyConfigurationStore.CurrentConfiguration.Voice.WhisperModel);
        clickyConfigurationStore.UpdateAndSave(configuration => configuration.PushToTalkShortcut = "ctrl+shift");
        Assert.Equal(brokenConfigurationJson, File.ReadAllText(TemporaryConfigurationFilePath)); // Tray save didn't clobber it.

        EditFileByHand("\"small.en", "\"small.en\"");
        Assert.True(clickyConfigurationStore.ReloadFromDisk());
        Assert.Null(clickyConfigurationStore.ConfigurationLoadErrorMessage);
        Assert.Equal("small.en", clickyConfigurationStore.CurrentConfiguration.Voice.WhisperModel);
        Assert.Equal(2, notificationCount); // The tray change and the fixed edit.
    }

    [Fact]
    public void WatchForHandEdits_AppliesAnEditorSave_OnTheDispatcherThread()
    {
        StaTestThread.Run(() =>
        {
            using ClickyConfigurationStore clickyConfigurationStore = ClickyConfigurationStore.LoadOrCreate(TemporaryConfigurationFilePath);
            int dispatcherThreadId = Environment.CurrentManagedThreadId;
            TaskCompletionSource<(string WhisperModel, int ThreadId)> reloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clickyConfigurationStore.ConfigurationChanged += changedConfiguration =>
                reloaded.TrySetResult((changedConfiguration.Voice.WhisperModel, Environment.CurrentManagedThreadId));
            clickyConfigurationStore.WatchForHandEdits(System.Windows.Threading.Dispatcher.CurrentDispatcher);

            EditFileByHand("\"base.en\"", "\"small.en\"");
            StaTestThread.WaitFor(reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(("small.en", dispatcherThreadId), reloaded.Task.Result);
        });
    }
}

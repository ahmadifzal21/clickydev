using Clicky.Windows.Configuration;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

/// <summary>
/// Runs real Whisper.net with the base.en model already cached in %LOCALAPPDATA%\Clicky\models, handed over
/// through the download seam, so nothing touches the network.
/// </summary>
public class WhisperLocalTranscriberTests
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Records statuses and completes when one matching the predicate arrives.</summary>
    private sealed class StatusRecorder
    {
        private readonly object statusesLock = new();
        private readonly List<string> statuses = [];
        private readonly List<(Func<string, bool> Predicate, TaskCompletionSource Completion)> waiters = [];

        public StatusRecorder(ISpeechToTextTranscriber speechToTextTranscriber) => speechToTextTranscriber.StatusChanged += Record;

        public IReadOnlyList<string> Statuses { get { lock (statusesLock) return statuses.ToList(); } }

        public Task WaitForAsync(Func<string, bool> predicate)
        {
            lock (statusesLock)
            {
                if (statuses.Any(predicate)) return Task.CompletedTask;
                TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((predicate, completion));
                return completion.Task.WaitAsync(LoadTimeout);
            }
        }

        private void Record(string status)
        {
            lock (statusesLock)
            {
                statuses.Add(status);
                foreach ((Func<string, bool> predicate, TaskCompletionSource completion) in waiters.Where(waiter => waiter.Predicate(status)))
                {
                    completion.TrySetResult();
                }
            }
        }
    }

    private static Task<string> ServeCachedBaseEnModel(string whisperModelName, Action<int> reportDownloadPercent, CancellationToken cancellationToken)
    {
        reportDownloadPercent(50);
        reportDownloadPercent(100);
        return Task.FromResult(VoiceTestAudio.CachedBaseEnModelPath);
    }

    [Fact]
    public async Task BeforeAnyModelIsLoaded_TranscribingExplainsWhy()
    {
        using WhisperLocalTranscriber whisperLocalTranscriber = new(ServeCachedBaseEnModel);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            whisperLocalTranscriber.TranscribeAsync(new float[16000], CancellationToken.None));

        Assert.Equal("Speech model not loaded yet.", exception.Message);
    }

    [Fact]
    public async Task Prepare_ReportsDownloadLoadReady_ThenTranscribesSpeech()
    {
        int downloadCallCount = 0;
        using WhisperLocalTranscriber whisperLocalTranscriber = new((whisperModelName, reportDownloadPercent, cancellationToken) =>
        {
            Interlocked.Increment(ref downloadCallCount);
            return ServeCachedBaseEnModel(whisperModelName, reportDownloadPercent, cancellationToken);
        });
        StatusRecorder statusRecorder = new(whisperLocalTranscriber);

        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" }); // Same model: no second load.
        await statusRecorder.WaitForAsync(status => status == "Speech model base.en ready.");

        Assert.Equal(1, downloadCallCount);
        Assert.Equal(
            ["Downloading speech model base.en: 50%", "Downloading speech model base.en: 100%", "Loading speech model base.en...", "Speech model base.en ready."],
            statusRecorder.Statuses);
        string transcript = await whisperLocalTranscriber.TranscribeAsync(VoiceTestAudio.SynthesizedSpeech, CancellationToken.None);
        Assert.Contains("settings menu", transcript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedModelSetting_SwapsInTheNewModel()
    {
        using WhisperLocalTranscriber whisperLocalTranscriber = new(ServeCachedBaseEnModel);
        StatusRecorder statusRecorder = new(whisperLocalTranscriber);

        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await statusRecorder.WaitForAsync(status => status == "Speech model base.en ready.");
        // A second name served from the same file still goes through a full load and replaces the first model.
        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en-copy" });
        await statusRecorder.WaitForAsync(status => status == "Speech model base.en-copy ready.");

        Assert.Contains("settings", await whisperLocalTranscriber.TranscribeAsync(VoiceTestAudio.SynthesizedSpeech, CancellationToken.None),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledTranscription_StopsAndReleasesTheModelForTheNextOne()
    {
        using WhisperLocalTranscriber whisperLocalTranscriber = new(ServeCachedBaseEnModel);
        StatusRecorder statusRecorder = new(whisperLocalTranscriber);
        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await statusRecorder.WaitForAsync(status => status == "Speech model base.en ready.");
        float[] longSpeech = Enumerable.Repeat(VoiceTestAudio.SynthesizedSpeech, 10).SelectMany(samples => samples).ToArray();

        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => whisperLocalTranscriber.TranscribeAsync(longSpeech, cancellation.Token));

        Assert.Contains("settings", await whisperLocalTranscriber.TranscribeAsync(VoiceTestAudio.SynthesizedSpeech, CancellationToken.None),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingChangedAgainDuringDownload_OnlyTheNewestModelLoads()
    {
        TaskCompletionSource<string> slowDownloadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using WhisperLocalTranscriber whisperLocalTranscriber = new((whisperModelName, reportDownloadPercent, cancellationToken) =>
            whisperModelName == "slow.en" ? slowDownloadCompletion.Task : ServeCachedBaseEnModel(whisperModelName, reportDownloadPercent, cancellationToken));
        StatusRecorder statusRecorder = new(whisperLocalTranscriber);

        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "slow.en" });
        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await statusRecorder.WaitForAsync(status => status == "Speech model base.en ready.");
        slowDownloadCompletion.SetResult(VoiceTestAudio.CachedBaseEnModelPath);
        await Task.Delay(300);

        Assert.DoesNotContain(statusRecorder.Statuses, status => status.Contains("slow.en"));
    }

    [Fact]
    public async Task FailedLoad_IsReported_AndTheNextPrepareRetries()
    {
        int downloadCallCount = 0;
        using WhisperLocalTranscriber whisperLocalTranscriber = new((_, _, _) =>
        {
            Interlocked.Increment(ref downloadCallCount);
            return Task.FromException<string>(new InvalidOperationException("HTTP 503"));
        });
        StatusRecorder statusRecorder = new(whisperLocalTranscriber);

        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await statusRecorder.WaitForAsync(status => status.Contains("failed"));
        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await Task.Delay(100);

        Assert.Equal("Speech model base.en failed to load: HTTP 503", statusRecorder.Statuses[0]);
        Assert.Equal(2, downloadCallCount);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            whisperLocalTranscriber.TranscribeAsync(new float[16000], CancellationToken.None));
        Assert.Contains("failed to load", exception.Message);
    }

    [Fact]
    public async Task DisposingDuringDownload_CancelsQuietly()
    {
        TaskCompletionSource downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource downloadCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WhisperLocalTranscriber whisperLocalTranscriber = new(async (_, _, cancellationToken) =>
        {
            downloadStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                downloadCancelled.SetResult();
            }

            return "";
        });
        StatusRecorder statusRecorder = new(whisperLocalTranscriber);

        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await downloadStarted.Task.WaitAsync(LoadTimeout);
        whisperLocalTranscriber.Dispose();
        await downloadCancelled.Task.WaitAsync(LoadTimeout);
        await Task.Delay(100);

        Assert.Empty(statusRecorder.Statuses);
    }

    [Fact]
    public async Task StatusWithNoListener_IsStillRecordedForTheNextError()
    {
        TaskCompletionSource progressReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using WhisperLocalTranscriber whisperLocalTranscriber = new((_, reportDownloadPercent, _) =>
        {
            reportDownloadPercent(42); // Nobody subscribed to StatusChanged.
            progressReported.SetResult();
            return new TaskCompletionSource<string>().Task; // Stays "downloading".
        });

        whisperLocalTranscriber.Prepare(new VoiceSettings { WhisperModel = "base.en" });
        await progressReported.Task.WaitAsync(LoadTimeout);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            whisperLocalTranscriber.TranscribeAsync(new float[16000], CancellationToken.None));
        Assert.Equal("Downloading speech model base.en: 42%", exception.Message);
    }

    [Theory]
    [InlineData(SpeechToTextProviderKind.WhisperLocal)]
    [InlineData(SpeechToTextProviderKind.AssemblyAi)] // Not built yet: still gets Whisper.
    public void Factory_AlwaysCreatesLocalWhisper(SpeechToTextProviderKind speechToTextProviderKind)
    {
        using ISpeechToTextTranscriber speechToTextTranscriber =
            SpeechToTextTranscriberFactory.Create(new VoiceSettings { SpeechToText = speechToTextProviderKind });

        Assert.IsType<WhisperLocalTranscriber>(speechToTextTranscriber);
    }
}

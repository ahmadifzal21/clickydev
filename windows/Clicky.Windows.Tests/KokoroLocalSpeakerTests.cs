using System.IO;
using System.Net;
using System.Net.Http;
using Clicky.Windows.Configuration;
using Clicky.Windows.Voice;
using NAudio.Wave;

namespace Clicky.Windows.Tests;

/// <summary>The Kokoro speaker's own logic, with the model download faked and the Windows fallback voice recorded, not played.</summary>
public sealed class KokoroLocalSpeakerTests : IDisposable
{
    private readonly string scratchModelsFolderPath = Path.Combine(Path.GetTempPath(), "ClickyTests", Guid.NewGuid().ToString("N"), "models");

    /// <summary>Records what it was asked to say; "speaking" lasts until stopped.</summary>
    internal sealed class RecordingFallbackSpeaker : ITextToSpeechSpeaker
    {
        private TaskCompletionSource? currentSpeech;
        public List<string> SpokenTexts { get; } = [];
        public int StopCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action? SoundStarted { add { } remove { } }
        public bool IsSpeaking => currentSpeech is { Task.IsCompleted: false };
        public void Prepare(VoiceSettings voiceSettings) { }

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            SpokenTexts.Add(text);
            currentSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return currentSpeech.Task;
        }

        public async Task SpeakLinesAsync(System.Threading.Channels.ChannelReader<SpokenLine> spokenLines, CancellationToken cancellationToken)
        {
            currentSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource thisSpeech = currentSpeech;
            try
            {
                await foreach (SpokenLine spokenLine in spokenLines.ReadAllAsync(cancellationToken))
                {
                    SpokenTexts.Add(spokenLine.Text);
                    spokenLine.OnStarted?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await thisSpeech.Task;
        }

        public void StopSpeaking()
        {
            StopCount++;
            currentSpeech?.TrySetResult();
        }

        public void Dispose() => IsDisposed = true;
    }

    private static (KokoroLocalSpeaker Speaker, RecordingFallbackSpeaker Fallback, List<string> Statuses, TaskCompletionSource LoadFailed)
        CreateSpeakerWhoseModelDownloadFails()
    {
        RecordingFallbackSpeaker fallbackSpeaker = new();
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Task.FromException<string>(new InvalidOperationException("offline")), FakeKokoroModel.NeverLoaded,
            () => new FakeWavePlayer(), fallbackSpeaker);
        List<string> statuses = [];
        TaskCompletionSource loadFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kokoroLocalSpeaker.StatusChanged += status =>
        {
            lock (statuses) statuses.Add(status);
            if (status.Contains("failed to load")) loadFailed.TrySetResult();
        };
        return (kokoroLocalSpeaker, fallbackSpeaker, statuses, loadFailed);
    }

    [Fact]
    public async Task ModelThatFailsToLoad_IsReported_AndSpeechUsesTheWindowsVoice()
    {
        var (kokoroLocalSpeaker, fallbackSpeaker, statuses, loadFailed) = CreateSpeakerWhoseModelDownloadFails();

        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        await loadFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task speech = kokoroLocalSpeaker.SpeakAsync("Hello there.", CancellationToken.None);

        Assert.Contains("Voice model failed to load: offline Trying again in 30 s.", statuses);
        Assert.Equal(["Hello there."], fallbackSpeaker.SpokenTexts);
        Assert.True(kokoroLocalSpeaker.IsSpeaking);

        kokoroLocalSpeaker.StopSpeaking();
        await speech.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(kokoroLocalSpeaker.IsSpeaking);
    }

    [Fact]
    public async Task FailedLoad_IsRetriedAutomatically_UntilTheModelLoads()
    {
        int downloadAttemptCount = 0;
        FakeKokoroModel fakeKokoroModel = new();
        List<int> retryDelayRequests = [];
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Interlocked.Increment(ref downloadAttemptCount) < 3
                ? Task.FromException<string>(new InvalidOperationException("offline"))
                : Task.FromResult("fake-model.onnx"),
            fakeKokoroModel.Load, () => new FakeWavePlayer(), new RecordingFallbackSpeaker(),
            failedAttemptCount => { lock (retryDelayRequests) retryDelayRequests.Add(failedAttemptCount); return TimeSpan.FromMilliseconds(20); });
        TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kokoroLocalSpeaker.StatusChanged += status => { if (status == "Voice model ready.") modelReady.TrySetResult(); };

        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        await modelReady.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, downloadAttemptCount);
        Assert.Equal([1, 2], retryDelayRequests);
        Assert.False(kokoroLocalSpeaker.IsSpeaking);
    }

    [Fact]
    public async Task ModelThatLoadsButFailsToWarmUp_IsReleased_AndRetried()
    {
        FakeKokoroModel fakeKokoroModel = new() { FailsOnCall = callNumber => callNumber == 1 }; // First warm-up fails.
        List<string> statuses = [];
        TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Task.FromResult("fake-model.onnx"), fakeKokoroModel.Load,
            () => new FakeWavePlayer(), new RecordingFallbackSpeaker(), _ => TimeSpan.FromMilliseconds(20));
        kokoroLocalSpeaker.StatusChanged += status =>
        {
            lock (statuses) statuses.Add(status);
            if (status == "Voice model ready.") modelReady.TrySetResult();
        };

        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        await modelReady.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, fakeKokoroModel.ReleaseCount);
        lock (statuses) Assert.Contains("Voice model failed to load: ONNX run 1 failed Trying again in 1 s.", statuses);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    [InlineData(50, 600)]
    public void DefaultRetryDelay_BacksOffToTenMinutes(int failedAttemptCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), KokoroLocalSpeaker.GetDefaultRetryDelay(failedAttemptCount));
    }

    [Fact]
    public async Task FailedLoad_IsRetriedAtOnceByTheNextPrepare()
    {
        int downloadAttemptCount = 0;
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) =>
        {
            Interlocked.Increment(ref downloadAttemptCount);
            return Task.FromException<string>(new InvalidOperationException("offline"));
        }, FakeKokoroModel.NeverLoaded, () => new FakeWavePlayer(), new RecordingFallbackSpeaker());
        SemaphoreSlim loadFailures = new(0);
        kokoroLocalSpeaker.StatusChanged += status => { if (status.Contains("failed to load")) loadFailures.Release(); };

        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        Assert.True(await loadFailures.WaitAsync(TimeSpan.FromSeconds(10)));
        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        Assert.True(await loadFailures.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(2, downloadAttemptCount);
    }

    [Fact]
    public void ModelStillDownloading_IsNotRequestedTwice_AndReportsProgress()
    {
        int downloadAttemptCount = 0;
        TaskCompletionSource<string> neverFinishingDownload = new();
        KokoroLocalSpeaker kokoroLocalSpeaker = new((reportDownloadPercent, _) =>
        {
            Interlocked.Increment(ref downloadAttemptCount);
            reportDownloadPercent(42);
            return neverFinishingDownload.Task;
        }, FakeKokoroModel.NeverLoaded, () => new FakeWavePlayer(), new RecordingFallbackSpeaker());
        List<string> statuses = [];
        kokoroLocalSpeaker.StatusChanged += statuses.Add;

        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        kokoroLocalSpeaker.Prepare(new VoiceSettings { KokoroVoice = "am_michael" });

        Assert.Equal(1, downloadAttemptCount);
        Assert.Contains("Downloading voice model: 42%", statuses);
        kokoroLocalSpeaker.Dispose();
    }

    [Fact]
    public void UnknownVoice_IsReportedWithTheDefaultToTry()
    {
        var (kokoroLocalSpeaker, _, statuses, _) = CreateSpeakerWhoseModelDownloadFails();

        kokoroLocalSpeaker.Prepare(new VoiceSettings { KokoroVoice = "zz_nobody" });

        lock (statuses) Assert.Contains(statuses, status => status.StartsWith("Kokoro voice \"zz_nobody\" not found (try \"af_heart\")"));
    }

    [Theory]
    [InlineData("af_heart")]
    [InlineData("BM_George")]
    public void BundledVoice_IsFound(string kokoroVoiceName)
    {
        var (kokoroLocalSpeaker, _, statuses, _) = CreateSpeakerWhoseModelDownloadFails();

        kokoroLocalSpeaker.Prepare(new VoiceSettings { KokoroVoice = kokoroVoiceName });

        lock (statuses) Assert.DoesNotContain(statuses, status => status.Contains("not found"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankText_SaysNothing(string blankText)
    {
        var (kokoroLocalSpeaker, fallbackSpeaker, _, _) = CreateSpeakerWhoseModelDownloadFails();

        await kokoroLocalSpeaker.SpeakAsync(blankText, CancellationToken.None);

        Assert.Empty(fallbackSpeaker.SpokenTexts);
    }

    [Fact]
    public void Dispose_DisposesTheFallbackVoice()
    {
        var (kokoroLocalSpeaker, fallbackSpeaker, _, _) = CreateSpeakerWhoseModelDownloadFails();

        kokoroLocalSpeaker.Dispose();

        Assert.True(fallbackSpeaker.IsDisposed);
    }

    [Fact]
    public async Task MissingModel_IsDownloadedFromKokoroSharpBinaries_IntoTheModelsFolder()
    {
        byte[] modelBytes = Enumerable.Range(0, 100_000).Select(byteIndex => (byte)byteIndex).ToArray();
        List<Uri> requestedUris = [];
        HttpClient fakeModelServer = new(new FakeHttpMessageHandler(request =>
        {
            requestedUris.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(modelBytes) };
        }));
        List<int> reportedPercents = [];

        string modelFilePath = await KokoroLocalSpeaker.EnsureModelDownloadedAsync(reportedPercents.Add, fakeModelServer,
            scratchModelsFolderPath, CancellationToken.None);
        string cachedModelFilePath = await KokoroLocalSpeaker.EnsureModelDownloadedAsync(reportedPercents.Add, fakeModelServer,
            scratchModelsFolderPath, CancellationToken.None);

        Assert.Equal(Path.Combine(scratchModelsFolderPath, "kokoro-v1.0.onnx"), modelFilePath);
        Assert.Equal(modelFilePath, cachedModelFilePath);
        Assert.Equal(modelBytes, File.ReadAllBytes(modelFilePath));
        Assert.Equal("https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx", Assert.Single(requestedUris).ToString());
        Assert.Equal(100, reportedPercents[^1]);
    }

    /// <summary>Stands in for the ONNX model: a short tone per chunk, optionally slow, optionally failing on chosen calls.</summary>
    private sealed class FakeKokoroModel
    {
        public static readonly Func<string, KokoroInference> NeverLoaded = _ => throw new InvalidOperationException("no model in this test");

        private int synthesizeCallCount;
        public Func<int, bool> FailsOnCall { get; init; } = _ => false;
        public TimeSpan TimePerChunk { get; init; } = TimeSpan.Zero;
        public int SynthesizeCallCount => Volatile.Read(ref synthesizeCallCount);
        private int releaseCount;
        public int ReleaseCount => Volatile.Read(ref releaseCount);
        public bool IsReleased => ReleaseCount > 0;

        public KokoroInference Load(string modelFilePath) => new(Synthesize, () => Interlocked.Increment(ref releaseCount));

        private float[] Synthesize(int[] tokens, float[,,] voiceStyle)
        {
            int callNumber = Interlocked.Increment(ref synthesizeCallCount); // Call 1 is the warm-up at load.
            Thread.Sleep(TimePerChunk);
            if (FailsOnCall(callNumber)) throw new InvalidOperationException($"ONNX run {callNumber} failed");
            return VoiceTestAudio.Tone(0.05, 0.5f);
        }
    }

    /// <summary>Stands in for the sound card: on Play it drains the queued audio as fast as it can until stopped.</summary>
    internal sealed class FakeWavePlayer : IWavePlayer
    {
        private IWaveProvider? waveProvider;
        private volatile bool isStopped;
        public bool HasPlayed { get; private set; }
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; } = 1;
        public WaveFormat OutputWaveFormat => waveProvider!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) => this.waveProvider = waveProvider;

        public void Play()
        {
            HasPlayed = true;
            PlaybackState = PlaybackState.Playing;
            byte[] readBuffer = new byte[4800];
            Task.Run(() => { while (!isStopped) { waveProvider!.Read(readBuffer, 0, readBuffer.Length); Thread.Sleep(1); } });
        }

        public void Stop()
        {
            isStopped = true;
            PlaybackState = PlaybackState.Stopped;
        }

        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() => Stop();
    }

    private const string ThreeSentenceReply = "That's the Save button. It's in the top left corner. Click it, and your changes are kept.";

    [Fact]
    public async Task SpeakLines_StartsOnTheFirstLine_AndSpeaksLinesThatArriveLater()
    {
        FakeKokoroModel fakeKokoroModel = new();
        (KokoroLocalSpeaker kokoroLocalSpeaker, RecordingFallbackSpeaker fallbackSpeaker) = await CreateReadySpeakerAsync(fakeKokoroModel);
        System.Threading.Channels.Channel<SpokenLine> spokenLines = System.Threading.Channels.Channel.CreateUnbounded<SpokenLine>();
        TaskCompletionSource firstLineStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondLineStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        spokenLines.Writer.TryWrite(new SpokenLine("That's the Save button.", firstLineStarted.SetResult));
        Task speech = kokoroLocalSpeaker.SpeakLinesAsync(spokenLines.Reader, CancellationToken.None);
        await firstLineStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); // Speaking before the rest of the reply exists.
        spokenLines.Writer.TryWrite(new SpokenLine("Click it to keep your changes.", secondLineStarted.SetResult));
        spokenLines.Writer.TryComplete();
        await speech.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(secondLineStarted.Task.IsCompleted);
        Assert.Empty(fallbackSpeaker.SpokenTexts);
        Assert.True(fakeKokoroModel.SynthesizeCallCount >= 3, "warm-up plus at least one chunk per line");
    }

    [Fact]
    public async Task SpeakLines_StopsWhenCancelled_WhileWaitingForTheNextLine()
    {
        (KokoroLocalSpeaker kokoroLocalSpeaker, _) = await CreateReadySpeakerAsync(new FakeKokoroModel());
        System.Threading.Channels.Channel<SpokenLine> spokenLines = System.Threading.Channels.Channel.CreateUnbounded<SpokenLine>();
        using CancellationTokenSource speechCancellation = new();
        TaskCompletionSource firstLineStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool laterLineStarted = false;

        spokenLines.Writer.TryWrite(new SpokenLine("One moment.", firstLineStarted.SetResult));
        Task speech = kokoroLocalSpeaker.SpeakLinesAsync(spokenLines.Reader, speechCancellation.Token);
        await firstLineStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        speechCancellation.Cancel();
        await speech.WaitAsync(TimeSpan.FromSeconds(5));
        spokenLines.Writer.TryWrite(new SpokenLine("Too late.", () => laterLineStarted = true));

        Assert.False(laterLineStarted);
        Assert.False(kokoroLocalSpeaker.IsSpeaking);
    }

    /// <summary>A speaker whose (fake) model has loaded and warmed up; <paramref name="createdPlayers"/> collects its sound outputs.</summary>
    private static async Task<(KokoroLocalSpeaker Speaker, RecordingFallbackSpeaker Fallback)> CreateReadySpeakerAsync(
        FakeKokoroModel fakeKokoroModel, List<FakeWavePlayer>? createdPlayers = null)
    {
        RecordingFallbackSpeaker fallbackSpeaker = new();
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Task.FromResult("fake-model.onnx"), fakeKokoroModel.Load, () =>
        {
            FakeWavePlayer fakeWavePlayer = new();
            lock (createdPlayers ?? []) createdPlayers?.Add(fakeWavePlayer);
            return fakeWavePlayer;
        }, fallbackSpeaker);
        TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kokoroLocalSpeaker.StatusChanged += status => { if (status == "Voice model ready.") modelReady.TrySetResult(); };
        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        await modelReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return (kokoroLocalSpeaker, fallbackSpeaker);
    }

    [Fact]
    public async Task ReadyModel_SpeaksEveryChunk_AndFinishesWhenTheAudioIsPlayed()
    {
        FakeKokoroModel fakeKokoroModel = new();
        var (kokoroLocalSpeaker, fallbackSpeaker) = await CreateReadySpeakerAsync(fakeKokoroModel);

        Task speech = kokoroLocalSpeaker.SpeakAsync(ThreeSentenceReply, CancellationToken.None);
        Assert.True(kokoroLocalSpeaker.IsSpeaking);
        await speech.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(kokoroLocalSpeaker.IsSpeaking);
        Assert.True(fakeKokoroModel.SynthesizeCallCount >= 3, $"warm-up + at least two chunks, got {fakeKokoroModel.SynthesizeCallCount}");
        Assert.Empty(fallbackSpeaker.SpokenTexts);
    }

    [Fact]
    public async Task ModelErrorBeforeAnySound_DoesNotEscape_AndTheWindowsVoiceSaysTheReply()
    {
        FakeKokoroModel fakeKokoroModel = new() { FailsOnCall = callNumber => callNumber > 1 };
        var (kokoroLocalSpeaker, fallbackSpeaker) = await CreateReadySpeakerAsync(fakeKokoroModel);

        Task speech = kokoroLocalSpeaker.SpeakAsync(ThreeSentenceReply, CancellationToken.None);
        while (fallbackSpeaker.SpokenTexts.Count == 0 && !speech.IsCompleted) await Task.Delay(10);

        Assert.Equal([ThreeSentenceReply], fallbackSpeaker.SpokenTexts);
        Assert.True(kokoroLocalSpeaker.IsSpeaking);
        kokoroLocalSpeaker.StopSpeaking();
        await speech.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(kokoroLocalSpeaker.IsSpeaking);
    }

    [Fact]
    public async Task ModelErrorMidReply_DoesNotEscape_AndTheSpeechFinishes_WithoutRepeatingInAnotherVoice()
    {
        FakeKokoroModel fakeKokoroModel = new() { FailsOnCall = callNumber => callNumber == 3 }; // Warm-up, chunk 1, then chunk 2 fails.
        var (kokoroLocalSpeaker, fallbackSpeaker) = await CreateReadySpeakerAsync(fakeKokoroModel);

        await kokoroLocalSpeaker.SpeakAsync(ThreeSentenceReply, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(kokoroLocalSpeaker.IsSpeaking);
        Assert.Empty(fallbackSpeaker.SpokenTexts);
        Assert.Equal(3, fakeKokoroModel.SynthesizeCallCount);
    }

    [Fact]
    public async Task StopMidReply_FinishesPromptly_AndSynthesizesNoFurtherChunks()
    {
        // Each chunk takes a second, so the stop lands while the second chunk's (uninterruptible) run is in progress.
        FakeKokoroModel fakeKokoroModel = new() { TimePerChunk = TimeSpan.FromSeconds(1) };
        List<FakeWavePlayer> createdPlayers = [];
        var (kokoroLocalSpeaker, _) = await CreateReadySpeakerAsync(fakeKokoroModel, createdPlayers);

        Task speech = kokoroLocalSpeaker.SpeakAsync(ThreeSentenceReply + " " + ThreeSentenceReply, CancellationToken.None);
        while (!createdPlayers.Any(fakeWavePlayer => fakeWavePlayer.HasPlayed)) await Task.Delay(5);
        kokoroLocalSpeaker.StopSpeaking();
        await speech.WaitAsync(TimeSpan.FromMilliseconds(300));
        await Task.Delay(1500); // Let the run that was in progress finish in the background.
        int synthesizeCallsAtStop = fakeKokoroModel.SynthesizeCallCount;
        await Task.Delay(1500);

        Assert.False(kokoroLocalSpeaker.IsSpeaking);
        Assert.Equal(synthesizeCallsAtStop, fakeKokoroModel.SynthesizeCallCount);
        Assert.Equal(PlaybackState.Stopped, Assert.Single(createdPlayers).PlaybackState);
    }

    [Fact]
    public async Task NewReply_StopsTheOneStillSpeaking()
    {
        FakeKokoroModel fakeKokoroModel = new() { TimePerChunk = TimeSpan.FromMilliseconds(200) };
        var (kokoroLocalSpeaker, _) = await CreateReadySpeakerAsync(fakeKokoroModel);

        Task firstSpeech = kokoroLocalSpeaker.SpeakAsync(ThreeSentenceReply + " " + ThreeSentenceReply, CancellationToken.None);
        Task secondSpeech = kokoroLocalSpeaker.SpeakAsync("Never mind.", CancellationToken.None);

        await firstSpeech.WaitAsync(TimeSpan.FromSeconds(2));
        await secondSpeech.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(kokoroLocalSpeaker.IsSpeaking);
    }

    [Fact]
    public async Task ModelFileThatFailsToOpen_IsReported_NotThrown()
    {
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Task.FromResult("corrupt.onnx"), FakeKokoroModel.NeverLoaded,
            () => new FakeWavePlayer(), new RecordingFallbackSpeaker());
        TaskCompletionSource loadFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kokoroLocalSpeaker.StatusChanged += status => { if (status.StartsWith("Voice model failed to load")) loadFailed.TrySetResult(); };

        kokoroLocalSpeaker.Prepare(new VoiceSettings());

        await loadFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Dispose_ReleasesTheLoadedModel()
    {
        FakeKokoroModel fakeKokoroModel = new();
        var (kokoroLocalSpeaker, _) = await CreateReadySpeakerAsync(fakeKokoroModel);

        kokoroLocalSpeaker.Dispose();

        Assert.True(fakeKokoroModel.IsReleased);
    }

    [Theory]
    [InlineData(TextToSpeechProviderKind.KokoroLocal)]
    [InlineData(TextToSpeechProviderKind.ElevenLabs)]
    public void EveryProvider_SpeaksWithKokoroForNow(TextToSpeechProviderKind textToSpeechProviderKind)
    {
        using ITextToSpeechSpeaker textToSpeechSpeaker = TextToSpeechSpeakerFactory.Create(new VoiceSettings { TextToSpeech = textToSpeechProviderKind });

        Assert.IsType<KokoroLocalSpeaker>(textToSpeechSpeaker);
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    public void Dispose()
    {
        string scratchRootPath = Path.GetDirectoryName(scratchModelsFolderPath)!;
        if (Directory.Exists(scratchRootPath)) Directory.Delete(scratchRootPath, recursive: true);
    }
}

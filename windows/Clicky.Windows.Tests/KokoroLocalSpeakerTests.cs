using System.IO;
using System.Net;
using System.Net.Http;
using Clicky.Windows.Configuration;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

/// <summary>The Kokoro speaker's own logic, with the model download faked and the Windows fallback voice recorded, not played.</summary>
public sealed class KokoroLocalSpeakerTests : IDisposable
{
    private readonly string scratchModelsFolderPath = Path.Combine(Path.GetTempPath(), "ClickyTests", Guid.NewGuid().ToString("N"), "models");

    /// <summary>Records what it was asked to say; "speaking" lasts until stopped.</summary>
    private sealed class RecordingFallbackSpeaker : ITextToSpeechSpeaker
    {
        private TaskCompletionSource? currentSpeech;
        public List<string> SpokenTexts { get; } = [];
        public int StopCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public event Action<string>? StatusChanged { add { } remove { } }
        public bool IsSpeaking => currentSpeech is { Task.IsCompleted: false };
        public void Prepare(VoiceSettings voiceSettings) { }

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            SpokenTexts.Add(text);
            currentSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return currentSpeech.Task;
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
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Task.FromException<string>(new InvalidOperationException("offline")), fallbackSpeaker);
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

        Assert.Contains("Voice model failed to load: offline", statuses);
        Assert.Equal(["Hello there."], fallbackSpeaker.SpokenTexts);
        Assert.True(kokoroLocalSpeaker.IsSpeaking);

        kokoroLocalSpeaker.StopSpeaking();
        await speech.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(kokoroLocalSpeaker.IsSpeaking);
    }

    [Fact]
    public async Task FailedLoad_IsRetriedByTheNextPrepare()
    {
        int downloadAttemptCount = 0;
        KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) =>
        {
            Interlocked.Increment(ref downloadAttemptCount);
            return Task.FromException<string>(new InvalidOperationException("offline"));
        }, new RecordingFallbackSpeaker());
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
        }, new RecordingFallbackSpeaker());
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

    [Theory]
    [InlineData(TextToSpeechProviderKind.KokoroLocal)]
    [InlineData(TextToSpeechProviderKind.ElevenLabs)]
    [InlineData(TextToSpeechProviderKind.EdgeTts)]
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

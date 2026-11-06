using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Windows.Voice;

/// <summary>
/// Foundry Local's live speech model (Nemotron Speech Streaming 0.6B, int4, CPU; MIT; in-process, no service), fed while the
/// shortcut is held so the transcript is ready about 0.3 s after the release (E10 T10.7: 284 ms median on the user's voice,
/// 8.8% word errors, against Whisper small.en's 722 ms and 11%). The ~700 MB model downloads into Clicky's models folder on
/// first use. <paramref name="onUnavailable"/> runs (once) when it is downloading or failed to load, so the fallback can get
/// ready meanwhile.
/// </summary>
internal sealed class FoundryLocalTranscriber(Action onUnavailable) : ISpeechToTextTranscriber
{
    public const string ModelAlias = "nemotron-speech-streaming-en-0.6b";

    /// <summary>The SDK's manager is a process-wide singleton; created once, on the first load.</summary>
    private static Task? managerCreation;
    private static readonly object ManagerCreationLock = new();

    private readonly CancellationTokenSource disposalCancellation = new();
    private IModel? loadedModel;
    private int isLoadStartedOrDone;
    private int hasReportedUnavailable;
    private string currentStatus = "Live speech model not loaded yet.";

    public event Action<string>? StatusChanged;

    public void Prepare(VoiceSettings voiceSettings)
    {
        if (Interlocked.Exchange(ref isLoadStartedOrDone, 1) == 0) _ = LoadAsync(disposalCancellation.Token);
    }

    public ILiveTranscription? BeginLiveTranscription() => loadedModel is { } model ? new FoundryLiveTranscription(model) : null;

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (loadedModel is not { } model) throw new InvalidOperationException(currentStatus);
        using FoundryLiveTranscription transcription = new(model);
        for (int offset = 0; offset < samples.Length; offset += SpeechClip.SampleRateHertz / 2)
        {
            transcription.Append(samples[offset..Math.Min(samples.Length, offset + SpeechClip.SampleRateHertz / 2)]);
        }
        return await transcription.FinishAsync(cancellationToken);
    }

    private static Task CreateManagerOnceAsync()
    {
        // The SDK's native Configuration has a finalizer that crashes the whole process when its constructor failed (seen when
        // foundry_local.dll couldn't load its ONNX Runtime DLLs). So load the native library first: if it can't load, Foundry
        // is reported unavailable and Whisper is used, instead of Clicky crashing.
        string nativeLibraryPath = Path.Combine(AppContext.BaseDirectory, "foundry_local.dll");
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(nativeLibraryPath, out _))
        {
            return Task.FromException(new InvalidOperationException($"Foundry Local's native runtime couldn't be loaded from {nativeLibraryPath}."));
        }

        lock (ManagerCreationLock)
        {
            string clickyDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky");
            return managerCreation ??= FoundryLocalManager.CreateAsync(new Microsoft.AI.Foundry.Local.Configuration
            {
                AppName = "Clicky",
                AppDataDir = Path.Combine(clickyDataFolder, "foundry"),
                ModelCacheDir = Path.Combine(ModelFileDownload.ModelsFolderPath, "foundry"),
                LogsDir = Path.Combine(clickyDataFolder, "logs", "foundry"),
                LogLevel = Microsoft.AI.Foundry.Local.LogLevel.Warning,
                // Clicky keeps what it hears on the PC; Foundry may still send one minimal process event.
                DisableNonessentialTelemetry = true
            }, NullLogger.Instance);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Stopwatch loadStopwatch = Stopwatch.StartNew();
            ReportStatus("Loading live speech model...");
            await CreateManagerOnceAsync().WaitAsync(cancellationToken);
            ICatalog catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);
            IModel model = await catalog.GetModelAsync(ModelAlias, cancellationToken)
                ?? throw new InvalidOperationException($"\"{ModelAlias}\" isn't in the Foundry Local catalog.");
            await model.DownloadAsync(downloadPercent =>
            {
                if (downloadPercent >= 100) return;
                ReportUnavailable(); // A first-run download takes a while; the fallback answers meanwhile.
                ReportStatus($"Downloading live speech model: {(int)downloadPercent}%");
            }, cancellationToken);
            ReportStatus("Loading live speech model...");
            await model.LoadAsync(cancellationToken);
            loadedModel = model;
            await TranscribeAsync(new float[SpeechClip.SampleRateHertz], cancellationToken); // Pays first-run setup now.
            Console.WriteLine($"Foundry Local {model.Id} loaded and warmed up in {loadStopwatch.ElapsedMilliseconds} ms");
            ReportStatus("Live speech model ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            loadedModel = null;
            Interlocked.Exchange(ref isLoadStartedOrDone, 0); // Lets the next Prepare (a config save) retry.
            Console.WriteLine($"Foundry Local failed to load: {exception}");
            ReportStatus($"Live speech model failed to load ({exception.Message}); using Whisper.");
            ReportUnavailable();
        }
    }

    private void ReportUnavailable()
    {
        if (Interlocked.Exchange(ref hasReportedUnavailable, 1) == 0) onUnavailable();
    }

    private void ReportStatus(string status)
    {
        currentStatus = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        disposalCancellation.Cancel();
        // The model and the SDK's native runtime are released when Clicky exits.
    }

    /// <summary>
    /// One streaming request: samples go in as 16-bit PCM while the user talks; the SDK decodes as they arrive, so finishing
    /// only has to decode the last moment of audio. Native resources are released once the request has ended.
    /// </summary>
    private sealed class FoundryLiveTranscription : ILiveTranscription
    {
        private readonly AudioSession audioSession;
        private readonly ItemQueue inputQueue = new();
        private readonly Request request = new();
        private readonly Task<string> transcript;
        private readonly object inputLock = new();
        private bool isInputFinished;

        public FoundryLiveTranscription(IModel model)
        {
            audioSession = new AudioSession(model);
            audioSession.SetStreaming(true);
            request.AddItem(AudioItem.CreateFormatDescriptor("pcm", SpeechClip.SampleRateHertz, 1));
            request.AddItem(inputQueue, takeOwnership: false);
            transcript = ReadTranscriptAsync();
            // Once the request has ended (finished or dropped), nothing uses the native objects any more.
            transcript.ContinueWith(_ =>
            {
                inputQueue.Dispose();
                request.Dispose();
                audioSession.Dispose();
            }, TaskScheduler.Default);
        }

        public void Append(float[] samples)
        {
            byte[] pcmBytes = new byte[samples.Length * 2];
            for (int index = 0; index < samples.Length; index++)
            {
                short sample = (short)Math.Clamp(samples[index] * 32767f, short.MinValue, short.MaxValue);
                pcmBytes[index * 2] = (byte)sample;
                pcmBytes[index * 2 + 1] = (byte)(sample >> 8);
            }
            lock (inputLock)
            {
                if (!isInputFinished) inputQueue.Push(BytesItem.CreateOwned(pcmBytes));
            }
        }

        public async Task<string> FinishAsync(CancellationToken cancellationToken)
        {
            FinishInput();
            return await transcript.WaitAsync(cancellationToken);
        }

        public void Dispose() => FinishInput();

        private void FinishInput()
        {
            lock (inputLock)
            {
                if (isInputFinished) return;
                isInputFinished = true;
                inputQueue.MarkFinished();
            }
        }

        private async Task<string> ReadTranscriptAsync()
        {
            await using var stream = audioSession.ProcessStreamingRequestAsync(request);
            List<string> finalSegments = [];
            await foreach (var item in stream)
            {
                using (item)
                {
                    if (item is SpeechSegmentItem { Kind: SpeechSegmentKind.Final } segment && !string.IsNullOrWhiteSpace(segment.Text))
                        finalSegments.Add(segment.Text.Trim());
                }
            }
            using var finalResponse = await stream.FinalResponse;
            string? fullTranscript = finalResponse.OfType<SpeechResultItem>().FirstOrDefault()?.Text;
            return (string.IsNullOrWhiteSpace(fullTranscript) ? string.Join(" ", finalSegments) : fullTranscript).Trim();
        }
    }
}

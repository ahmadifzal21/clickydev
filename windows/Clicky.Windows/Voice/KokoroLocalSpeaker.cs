using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using NAudio.Wave;

namespace Clicky.Windows.Voice;

/// <summary>Speaks Clicky's replies aloud. Port target of the Mac's ElevenLabsTTSClient.</summary>
internal interface ITextToSpeechSpeaker : IDisposable
{
    /// <summary>Short human-readable state ("Downloading voice model: 42%"), shown in the panel.</summary>
    event Action<string>? StatusChanged;

    /// <summary>True from the start of <see cref="SpeakAsync"/> until the speech ends or is stopped.</summary>
    bool IsSpeaking { get; }

    /// <summary>Loads (downloading first if needed) whatever the voice settings ask for. Safe to call repeatedly.</summary>
    void Prepare(VoiceSettings voiceSettings);

    /// <summary>Stops anything already speaking, then speaks <paramref name="text"/>. Finishes when the speech ends or is stopped.</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken);

    void StopSpeaking();
}

internal static class TextToSpeechSpeakerFactory
{
    /// <summary>
    /// Only Kokoro exists for now: ElevenLabs (T7.4) is built only if local speech feels slow or flat after E8, so
    /// "eleven-labs" still gets Kokoro, with a log line saying so.
    /// </summary>
    public static ITextToSpeechSpeaker Create(VoiceSettings voiceSettings)
    {
        if (voiceSettings.TextToSpeech != TextToSpeechProviderKind.KokoroLocal)
        {
            Console.WriteLine($"Text-to-speech \"{voiceSettings.TextToSpeech}\" isn't built yet; using local Kokoro.");
        }

        return new KokoroLocalSpeaker();
    }
}

/// <summary>The loaded model, reduced to what the speaker needs, so tests can stand in a fake that fails or is slow.</summary>
/// <param name="Synthesize">Tokens + voice style -> 24 kHz mono samples. Can't be interrupted once started (one ONNX run).</param>
internal sealed record KokoroInference(Func<int[], float[,,], float[]> Synthesize, Action Release)
{
    public static KokoroInference Load(string modelFilePath)
    {
        KokoroModel kokoroModel = new(modelFilePath);
        return new KokoroInference((tokens, voiceStyle) => kokoroModel.Infer(tokens, voiceStyle), kokoroModel.Dispose);
    }
}

/// <summary>
/// Kokoro-82M in-process (ONNX Runtime, CPU; DirectML fails on the Arc GPU, see the E7 ticket log). The ~320 MB model is
/// downloaded on first use and loaded once; voices are the .npy files in the voices folder beside Clicky.exe. Until the model
/// is ready, or if Kokoro fails before making a sound, replies are spoken with Windows' built-in voice instead.
/// <para>
/// Uses KokoroSharp's model, tokenizer and segmenter but not its KokoroTTS/SpeakFast player: that one runs inference on its own
/// thread with no exception handling (an ONNX error there would end Clicky) and reports the end of speech only through
/// callbacks (a failure mid-reply would leave the speech "running" forever). Here inference runs in our own task, every error is
/// caught, and <see cref="SpeakAsync"/> finishes when that task does. <see cref="SpeakAsync"/> and <see cref="StopSpeaking"/>
/// are called from the UI thread.
/// </para>
/// </summary>
internal sealed class KokoroLocalSpeaker : ITextToSpeechSpeaker
{
    public const string DefaultVoiceName = "af_heart";

    /// <summary>
    /// The first chunk is synthesized before anything plays, so it's kept short: 30 tokens gave first sound in ~0.8 s on the
    /// dev PC's CPU against ~2 s with KokoroSharp's 200, for about a second more in total.
    /// </summary>
    private static readonly DefaultSegmentationConfig QuickFirstSoundSegmentation = new() { MaxFirstSegmentLength = 30 };

    /// <summary>KokoroSharp's natural pauses after a chunk ending in punctuation (0.5 s after a period, 0.1 s after a comma).</summary>
    private static readonly PauseAfterSegmentStrategy PauseAfterPunctuation = new();

    /// <summary>
    /// Played after the last chunk. The speech counts as finished once the queued audio has been handed to the sound card,
    /// which still holds up to WaveOutEvent's 300 ms latency; this silence is what's left there, so stopping then cuts nothing.
    /// </summary>
    private static readonly TimeSpan TrailingSilence = TimeSpan.FromMilliseconds(500);

    private const string KokoroModelDownloadUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";

    private readonly Func<Action<int>, CancellationToken, Task<string>> ensureModelDownloadedAsync;
    private readonly Func<string, KokoroInference> loadKokoroInference;
    private readonly Func<IWavePlayer> createWavePlayer;
    private readonly ITextToSpeechSpeaker fallbackSpeaker;
    private readonly CancellationTokenSource disposalCancellation = new();

    /// <summary>One synthesis at a time: a reply that was just stopped may still be finishing its last ONNX run.</summary>
    private readonly SemaphoreSlim inferenceLock = new(1, 1);
    private KokoroInference? loadedKokoroInference;
    private KokoroVoice? selectedKokoroVoice;
    private bool isModelLoadStarted;
    private string currentStatus = "Voice model not loaded yet.";
    private CancellationTokenSource? currentSpeechCancellation;
    private Task currentKokoroSpeech = Task.CompletedTask;

    public KokoroLocalSpeaker() : this(EnsureModelDownloadedAsync, KokoroInference.Load, () => new WaveOutEvent(), new WindowsBuiltInSpeaker())
    {
    }

    /// <summary>Takes the download, model, sound output and fallback voice so tests run without the network, the model or speakers.</summary>
    internal KokoroLocalSpeaker(Func<Action<int>, CancellationToken, Task<string>> ensureModelDownloadedAsync,
        Func<string, KokoroInference> loadKokoroInference, Func<IWavePlayer> createWavePlayer, ITextToSpeechSpeaker fallbackSpeaker)
    {
        this.ensureModelDownloadedAsync = ensureModelDownloadedAsync;
        this.loadKokoroInference = loadKokoroInference;
        this.createWavePlayer = createWavePlayer;
        this.fallbackSpeaker = fallbackSpeaker;
    }

    public event Action<string>? StatusChanged;

    public bool IsSpeaking => !currentKokoroSpeech.IsCompleted || fallbackSpeaker.IsSpeaking;

    public static Task<string> EnsureModelDownloadedAsync(Action<int> reportDownloadPercent, CancellationToken cancellationToken) =>
        EnsureModelDownloadedAsync(reportDownloadPercent, ModelFileDownload.SharedModelDownloadHttpClient,
            ModelFileDownload.ModelsFolderPath, cancellationToken);

    internal static async Task<string> EnsureModelDownloadedAsync(Action<int> reportDownloadPercent,
        System.Net.Http.HttpClient modelDownloadHttpClient, string modelsFolderPath, CancellationToken cancellationToken)
    {
        string modelFilePath = Path.Combine(modelsFolderPath, "kokoro-v1.0.onnx");
        if (!File.Exists(modelFilePath))
        {
            await ModelFileDownload.DownloadAsync(KokoroModelDownloadUrl, modelFilePath, "the Kokoro voice model",
                reportDownloadPercent, modelDownloadHttpClient, cancellationToken);
        }

        return modelFilePath;
    }

    public void Prepare(VoiceSettings voiceSettings)
    {
        SelectVoice(voiceSettings.KokoroVoice);
        if (!isModelLoadStarted)
        {
            isModelLoadStarted = true;
            _ = LoadModelAsync(disposalCancellation.Token);
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        StopSpeaking();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (loadedKokoroInference is not { } kokoroInference || selectedKokoroVoice is not { } kokoroVoice)
        {
            Console.WriteLine($"Kokoro isn't ready ({currentStatus}); speaking with the Windows voice.");
            await fallbackSpeaker.SpeakAsync(text, cancellationToken);
            return;
        }

        CancellationTokenSource speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        currentSpeechCancellation = speechCancellation;
        currentKokoroSpeech = SpeakWithKokoroAsync(text, kokoroInference, kokoroVoice, speechCancellation.Token);
        await currentKokoroSpeech;
    }

    public void StopSpeaking()
    {
        currentSpeechCancellation?.Cancel();
        fallbackSpeaker.StopSpeaking();
    }

    /// <summary>Synthesizes chunk by chunk and plays each as soon as it's ready. Never throws.</summary>
    private async Task SpeakWithKokoroAsync(string text, KokoroInference kokoroInference, KokoroVoice kokoroVoice,
        CancellationToken speechCancellationToken)
    {
        Stopwatch firstSoundStopwatch = Stopwatch.StartNew();
        BufferedWaveProvider queuedSpeechAudio = new(KokoroPlayback.waveFormat)
        {
            // Plays silence while waiting for the next chunk instead of stopping; a whole reply always fits.
            ReadFully = true,
            BufferDuration = TimeSpan.FromMinutes(10)
        };
        bool hasStartedPlaying = false;
        try
        {
            using IWavePlayer wavePlayer = createWavePlayer();
            wavePlayer.Init(queuedSpeechAudio);
            using CancellationTokenRegistration stopPlaybackOnCancel = speechCancellationToken.Register(wavePlayer.Stop);

            List<int[]> tokenChunks = await RunExclusiveInferenceAsync(() => SegmentationSystem.SplitToSegments(
                Tokenizer.Tokenize(text.Trim(), KokoroLangCodeHelper.GetLangCode(kokoroVoice)), QuickFirstSoundSegmentation),
                speechCancellationToken);
            foreach (int[] tokenChunk in tokenChunks)
            {
                float[] chunkSamples = await RunExclusiveInferenceAsync(
                    () => KokoroPlayback.PostProcessSamples(kokoroInference.Synthesize(tokenChunk, kokoroVoice.Features)),
                    speechCancellationToken);
                speechCancellationToken.ThrowIfCancellationRequested();
                AddSamples(queuedSpeechAudio, chunkSamples);
                if (tokenChunk.Length > 0 && Tokenizer.PunctuationTokens.Contains(tokenChunk[^1]))
                {
                    AddSilence(queuedSpeechAudio, TimeSpan.FromSeconds(PauseAfterPunctuation[Tokenizer.TokenToChar[tokenChunk[^1]]]));
                }

                if (!hasStartedPlaying)
                {
                    hasStartedPlaying = true;
                    wavePlayer.Play();
                    Console.WriteLine($"Kokoro first sound after {firstSoundStopwatch.ElapsedMilliseconds} ms");
                }
            }

            AddSilence(queuedSpeechAudio, TrailingSilence);
            while (queuedSpeechAudio.BufferedBytes > 0)
            {
                await Task.Delay(50, speechCancellationToken);
            }

            wavePlayer.Stop();
        }
        catch (OperationCanceledException) when (speechCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Kokoro failed to speak: {exception}");
            if (!hasStartedPlaying)
            {
                // Nothing was heard yet, so the Windows voice can say the whole reply. Mid-reply it just stops: restarting
                // in another voice would repeat what was already said.
                await fallbackSpeaker.SpeakAsync(text, speechCancellationToken);
            }
        }
    }

    /// <summary>
    /// Runs tokenizing or synthesis off the UI thread, one at a time. An ONNX run can't be interrupted, so on cancel this
    /// returns at once and the run finishes in the background, still holding the lock so the next one waits for it.
    /// </summary>
    private Task<TResult> RunExclusiveInferenceAsync<TResult>(Func<TResult> runInference, CancellationToken cancellationToken) =>
        RunUnderInferenceLockAsync(runInference, cancellationToken).WaitAsync(cancellationToken);

    private async Task<TResult> RunUnderInferenceLockAsync<TResult>(Func<TResult> runInference, CancellationToken cancellationToken)
    {
        await inferenceLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); // Queued for speech that has since been stopped: skip the run.
            return await Task.Run(runInference, CancellationToken.None);
        }
        finally
        {
            inferenceLock.Release();
        }
    }

    private static void AddSamples(BufferedWaveProvider queuedSpeechAudio, float[] samples)
    {
        byte[] sampleBytes = KokoroPlayback.GetBytes(samples);
        queuedSpeechAudio.AddSamples(sampleBytes, 0, sampleBytes.Length);
    }

    private static void AddSilence(BufferedWaveProvider queuedSpeechAudio, TimeSpan silenceDuration) =>
        AddSamples(queuedSpeechAudio, new float[(int)(silenceDuration.TotalSeconds * KokoroPlayback.waveFormat.SampleRate)]);

    private void SelectVoice(string kokoroVoiceName)
    {
        try
        {
            selectedKokoroVoice = KokoroVoiceManager.GetVoice(kokoroVoiceName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DirectoryNotFoundException)
        {
            selectedKokoroVoice = null;
            Console.WriteLine($"Kokoro voice \"{kokoroVoiceName}\" not found: {exception.Message}");
            ReportStatus($"Kokoro voice \"{kokoroVoiceName}\" not found (try \"{DefaultVoiceName}\"); using the Windows voice.");
        }
    }

    private async Task LoadModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            string modelFilePath = await ensureModelDownloadedAsync(
                downloadPercent => ReportStatus($"Downloading voice model: {downloadPercent}%"), cancellationToken);
            ReportStatus("Loading voice model...");
            Stopwatch loadStopwatch = Stopwatch.StartNew();
            KokoroInference kokoroInference = await Task.Run(() => loadKokoroInference(modelFilePath), cancellationToken);
            // The first synthesis also loads the phonemizer's dictionaries (measured 3.5 s to first sound cold against
            // ~2 s warm), so pay that now rather than on the first reply. Skipped when no voice files were found.
            if (selectedKokoroVoice is { } warmUpVoice)
            {
                await RunExclusiveInferenceAsync(() => kokoroInference.Synthesize(
                    Tokenizer.Tokenize("Warming up the voice.", KokoroLangCodeHelper.GetLangCode(warmUpVoice)), warmUpVoice.Features),
                    cancellationToken);
            }

            loadedKokoroInference = kokoroInference;
            Console.WriteLine($"Kokoro loaded and warmed up in {loadStopwatch.ElapsedMilliseconds} ms from {modelFilePath}");
            if (selectedKokoroVoice is not null)
            {
                ReportStatus("Voice model ready.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            isModelLoadStarted = false; // Lets the next Prepare (or config save) retry.
            Console.WriteLine($"Kokoro failed to load: {exception}");
            ReportStatus($"Voice model failed to load: {exception.Message}");
        }
    }

    private void ReportStatus(string status)
    {
        currentStatus = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        disposalCancellation.Cancel();
        StopSpeaking();
        fallbackSpeaker.Dispose();
        // Released only when no synthesis is mid-run (an ONNX run can't be interrupted); at exit the OS frees it otherwise.
        if (loadedKokoroInference is { } kokoroInference && inferenceLock.Wait(TimeSpan.FromSeconds(2)))
        {
            kokoroInference.Release();
        }
    }
}

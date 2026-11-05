using System.Diagnostics;
using System.IO;
using Clicky.Windows.Configuration;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

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
    /// "eleven-labs" (and the legacy "edge-tts") still get Kokoro, with a log line saying so.
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

/// <summary>
/// Kokoro-82M in-process through KokoroSharp (ONNX Runtime, CPU; DirectML fails on the Arc GPU, see the E7 ticket log). The ~320 MB model is downloaded on first use and
/// loaded once; voices are the .npy files in the voices folder beside Clicky.exe. Until the model is ready, or if
/// Kokoro fails, replies are spoken with Windows' built-in voice instead, so Clicky is never silent.
/// <see cref="SpeakAsync"/> and <see cref="StopSpeaking"/> are called from the UI thread.
/// </summary>
internal sealed class KokoroLocalSpeaker : ITextToSpeechSpeaker
{
    public const string DefaultVoiceName = "af_heart";

    /// <summary>
    /// The first chunk is synthesized before anything plays, so it's kept short: 30 tokens gave first sound in ~0.8 s on the
    /// dev PC's CPU against ~2 s with KokoroSharp's 200, for about a second more in total.
    /// </summary>
    private static readonly KokoroTTSPipelineConfig QuickFirstSoundPipelineConfig =
        new(new DefaultSegmentationConfig { MaxFirstSegmentLength = 30 });

    private const string KokoroModelDownloadUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";

    private readonly Func<Action<int>, CancellationToken, Task<string>> ensureModelDownloadedAsync;
    private readonly ITextToSpeechSpeaker fallbackSpeaker;
    private readonly CancellationTokenSource disposalCancellation = new();
    private KokoroTTS? loadedKokoroTts;
    private KokoroVoice? selectedKokoroVoice;
    private bool isModelLoadStarted;
    private string currentStatus = "Voice model not loaded yet.";
    private TaskCompletionSource? currentKokoroSpeechCompletion;

    public KokoroLocalSpeaker() : this(EnsureModelDownloadedAsync, new WindowsBuiltInSpeaker())
    {
    }

    /// <summary>Takes the model download step and the fallback voice so tests can run without the network or speakers.</summary>
    internal KokoroLocalSpeaker(Func<Action<int>, CancellationToken, Task<string>> ensureModelDownloadedAsync,
        ITextToSpeechSpeaker fallbackSpeaker)
    {
        this.ensureModelDownloadedAsync = ensureModelDownloadedAsync;
        this.fallbackSpeaker = fallbackSpeaker;
    }

    public event Action<string>? StatusChanged;

    public bool IsSpeaking => currentKokoroSpeechCompletion is { Task.IsCompleted: false } || fallbackSpeaker.IsSpeaking;

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

        if (loadedKokoroTts is not { } kokoroTts || selectedKokoroVoice is not { } kokoroVoice)
        {
            Console.WriteLine($"Kokoro isn't ready ({currentStatus}); speaking with the Windows voice.");
            await fallbackSpeaker.SpeakAsync(text, cancellationToken);
            return;
        }

        TaskCompletionSource kokoroSpeechCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        currentKokoroSpeechCompletion = kokoroSpeechCompletion;
        try
        {
            // SpeakFast plays the first sentence as soon as it's synthesized while the rest is still being synthesized.
            // Subscribing after the call is safe: the callbacks fire only after background inference and playback.
            Stopwatch firstSoundStopwatch = Stopwatch.StartNew();
            SynthesisHandle synthesisHandle = kokoroTts.SpeakFast(text, kokoroVoice, QuickFirstSoundPipelineConfig);
            synthesisHandle.OnSpeechStarted += _ => Console.WriteLine($"Kokoro first sound after {firstSoundStopwatch.ElapsedMilliseconds} ms");
            synthesisHandle.OnSpeechCompleted += _ => kokoroSpeechCompletion.TrySetResult();
            synthesisHandle.OnSpeechCanceled += _ => kokoroSpeechCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            kokoroSpeechCompletion.TrySetResult();
            Console.WriteLine($"Kokoro failed to speak; using the Windows voice: {exception}");
            await fallbackSpeaker.SpeakAsync(text, cancellationToken);
            return;
        }

        using (cancellationToken.Register(StopSpeaking))
        {
            await kokoroSpeechCompletion.Task;
        }
    }

    public void StopSpeaking()
    {
        loadedKokoroTts?.StopPlayback();
        // Completed here too: a job cancelled before its first sentence was synthesized raises no cancel callback.
        currentKokoroSpeechCompletion?.TrySetResult();
        fallbackSpeaker.StopSpeaking();
    }

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
            KokoroTTS kokoroTts = await Task.Run(() => new KokoroTTS(modelFilePath), cancellationToken);
            // The first synthesis also loads the phonemizer's dictionaries (measured 3.5 s to first sound cold against
            // ~2 s warm), so pay that now rather than on the first reply. Skipped when no voice files were found.
            if (selectedKokoroVoice is { } warmUpVoice)
            {
                await WarmUpAsync(kokoroTts, warmUpVoice).WaitAsync(cancellationToken);
            }

            loadedKokoroTts = kokoroTts;
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

    /// <summary>Synthesizes a short phrase without playing it.</summary>
    private static Task WarmUpAsync(KokoroTTS kokoroTts, KokoroVoice kokoroVoice)
    {
        TaskCompletionSource warmUpCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.Run(async () =>
        {
            int[] warmUpTokens = Tokenizer.Tokenize("Warming up the voice.", KokoroLangCodeHelper.GetLangCode(kokoroVoice));
            kokoroTts.EnqueueJob(KokoroJob.Create(warmUpTokens, kokoroVoice.Features, 1f, _ => warmUpCompletion.TrySetResult()));
            await warmUpCompletion.Task;
        });
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
        loadedKokoroTts?.Dispose();
        fallbackSpeaker.Dispose();
    }
}

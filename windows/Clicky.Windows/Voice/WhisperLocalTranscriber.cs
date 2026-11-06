using System.Diagnostics;
using Clicky.Windows.Configuration;
using Whisper.net;

namespace Clicky.Windows.Voice;

/// <summary>Turns a recorded push-to-talk clip into text. Port target of the Mac transcription providers.</summary>
internal interface ISpeechToTextTranscriber : IDisposable
{
    /// <summary>Short human-readable state ("Downloading speech model base.en: 42%"), shown in the panel.</summary>
    event Action<string>? StatusChanged;

    /// <summary>Loads (downloading first if needed) whatever the voice settings ask for. Safe to call repeatedly.</summary>
    void Prepare(VoiceSettings voiceSettings);

    /// <param name="samples">16 kHz mono samples in -1..1.</param>
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken);

    /// <summary>
    /// Starts transcribing while the user is still talking (fed by <see cref="ILiveTranscription.Append"/>), or null when this
    /// transcriber only works on a finished clip (Whisper) or isn't ready; the clip then goes to <see cref="TranscribeAsync"/>.
    /// </summary>
    ILiveTranscription? BeginLiveTranscription() => null;
}

/// <summary>One push-to-talk recording being transcribed as it is spoken. Dispose drops it (a tap, silence, a new press).</summary>
internal interface ILiveTranscription : IDisposable
{
    /// <param name="samples">The next 16 kHz mono samples in -1..1, as the microphone delivers them (any thread).</param>
    void Append(float[] samples);

    /// <summary>The recording has ended: the final transcript, ready a moment after the last samples.</summary>
    Task<string> FinishAsync(CancellationToken cancellationToken);
}

internal static class SpeechToTextTranscriberFactory
{
    /// <summary>
    /// "foundry-local" (the default) is Foundry Local with Whisper as its fallback. AssemblyAI (T6.3) isn't built, so
    /// "assembly-ai" gets Whisper, with a log line saying so.
    /// </summary>
    public static ISpeechToTextTranscriber Create(VoiceSettings voiceSettings)
    {
        switch (voiceSettings.SpeechToText)
        {
            case SpeechToTextProviderKind.FoundryLocal:
                return new FallbackSpeechToTextTranscriber(
                    onUnavailable => new FoundryLocalTranscriber(onUnavailable), () => new WhisperLocalTranscriber());
            case SpeechToTextProviderKind.WhisperLocal:
                return new WhisperLocalTranscriber();
            default:
                Console.WriteLine($"Speech-to-text \"{voiceSettings.SpeechToText}\" isn't built yet; using local Whisper.");
                return new WhisperLocalTranscriber();
        }
    }
}

/// <summary>
/// In-process Whisper.net (whisper.cpp; Vulkan GPU runtime, CPU fallback). The model is loaded once and kept; a changed
/// <c>voice.whisperModel</c> loads the new one in the background and swaps it in when ready.
/// </summary>
internal sealed class WhisperLocalTranscriber : ISpeechToTextTranscriber
{
    /// <summary>Held while transcribing or swapping models, so a model is never disposed mid-transcription.</summary>
    private readonly SemaphoreSlim whisperFactoryLock = new(1, 1);
    private readonly CancellationTokenSource disposalCancellation = new();
    private WhisperFactory? loadedWhisperFactory;
    private string? requestedModelName;
    private string currentStatus = "Speech model not loaded yet.";
    private readonly Func<string, Action<int>, CancellationToken, Task<string>> ensureModelDownloadedAsync;

    public WhisperLocalTranscriber() : this(WhisperModelFile.EnsureDownloadedAsync)
    {
    }

    /// <summary>Takes the model download step so tests can serve a model file without the network.</summary>
    internal WhisperLocalTranscriber(Func<string, Action<int>, CancellationToken, Task<string>> ensureModelDownloadedAsync)
    {
        this.ensureModelDownloadedAsync = ensureModelDownloadedAsync;
    }

    public event Action<string>? StatusChanged;

    public void Prepare(VoiceSettings voiceSettings)
    {
        string whisperModelName = voiceSettings.WhisperModel;
        if (whisperModelName == requestedModelName)
        {
            return;
        }

        requestedModelName = whisperModelName;
        _ = LoadModelAsync(whisperModelName, disposalCancellation.Token);
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        List<string> segmentTexts = [];
        await whisperFactoryLock.WaitAsync(cancellationToken);
        try
        {
            if (loadedWhisperFactory is null)
            {
                throw new InvalidOperationException(currentStatus);
            }

            await using WhisperProcessor whisperProcessor = loadedWhisperFactory.CreateBuilder().WithLanguage("en").Build();
            await foreach (SegmentData segmentData in whisperProcessor.ProcessAsync(SpeechClip.PadWithSilenceForWhisper(samples), cancellationToken))
            {
                segmentTexts.Add(segmentData.Text);
            }
        }
        finally
        {
            whisperFactoryLock.Release();
        }

        return SpeechClip.CleanTranscript(segmentTexts);
    }

    private async Task LoadModelAsync(string whisperModelName, CancellationToken cancellationToken)
    {
        try
        {
            string modelFilePath = await ensureModelDownloadedAsync(whisperModelName,
                downloadPercent => ReportStatus($"Downloading speech model {whisperModelName}: {downloadPercent}%"),
                cancellationToken);
            if (whisperModelName != requestedModelName)
            {
                return; // The setting changed again while downloading; the newer load wins.
            }

            ReportStatus($"Loading speech model {whisperModelName}...");
            Stopwatch loadStopwatch = Stopwatch.StartNew();
            WhisperFactory newWhisperFactory = await Task.Run(() => WhisperFactory.FromPath(modelFilePath), cancellationToken);

            await whisperFactoryLock.WaitAsync(cancellationToken);
            try
            {
                loadedWhisperFactory?.Dispose();
                loadedWhisperFactory = newWhisperFactory;
            }
            finally
            {
                whisperFactoryLock.Release();
            }

            // The first Vulkan decode compiles GPU shaders (measured 11 s for small.en, then 1.8 s), so pay
            // that now on silence rather than on the user's first question.
            await TranscribeAsync(new float[SpeechClip.SampleRateHertz], cancellationToken);
            Console.WriteLine($"Whisper {whisperModelName} loaded and warmed up in {loadStopwatch.ElapsedMilliseconds} ms " +
                $"({Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary} runtime) from {modelFilePath}");
            ReportStatus($"Speech model {whisperModelName} ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            requestedModelName = null; // Lets the next Prepare (or config save) retry.
            Console.WriteLine($"Whisper {whisperModelName} failed to load: {exception}");
            ReportStatus($"Speech model {whisperModelName} failed to load: {exception.Message}");
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
        loadedWhisperFactory?.Dispose();
    }
}

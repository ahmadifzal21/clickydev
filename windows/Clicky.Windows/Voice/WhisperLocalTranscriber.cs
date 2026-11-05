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
}

internal static class SpeechToTextTranscriberFactory
{
    /// <summary>
    /// Only Whisper exists for now: AssemblyAI (T6.3) is built only if local transcription feels slow
    /// end to end after E8, so "assembly-ai" in the config still gets Whisper, with a log line saying so.
    /// </summary>
    public static ISpeechToTextTranscriber Create(VoiceSettings voiceSettings)
    {
        if (voiceSettings.SpeechToText != SpeechToTextProviderKind.WhisperLocal)
        {
            Console.WriteLine($"Speech-to-text \"{voiceSettings.SpeechToText}\" isn't built yet; using local Whisper.");
        }

        return new WhisperLocalTranscriber();
    }
}

/// <summary>
/// In-process Whisper.net (whisper.cpp, CPU runtime). The model is loaded once and kept; a changed
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
        await whisperFactoryLock.WaitAsync(cancellationToken);
        try
        {
            if (loadedWhisperFactory is null)
            {
                throw new InvalidOperationException(currentStatus);
            }

            await using WhisperProcessor whisperProcessor = loadedWhisperFactory.CreateBuilder().WithLanguage("en").Build();
            List<string> segmentTexts = [];
            await foreach (SegmentData segmentData in whisperProcessor.ProcessAsync(SpeechClip.PadWithSilenceForWhisper(samples), cancellationToken))
            {
                segmentTexts.Add(segmentData.Text);
            }

            return SpeechClip.CleanTranscript(segmentTexts);
        }
        finally
        {
            whisperFactoryLock.Release();
        }
    }

    private async Task LoadModelAsync(string whisperModelName, CancellationToken cancellationToken)
    {
        try
        {
            string modelFilePath = await WhisperModelFile.EnsureDownloadedAsync(whisperModelName,
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

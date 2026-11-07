using Clicky.Windows.Configuration;

namespace Clicky.Windows.Voice;

/// <summary>
/// A primary transcriber (Foundry Local) with a fallback (Whisper) that is only loaded when needed: when the primary says it
/// is unavailable (downloading or failed to load), or when it fails on a clip. Until then the fallback costs no memory.
/// </summary>
internal sealed class FallbackSpeechToTextTranscriber : ISpeechToTextTranscriber
{
    private readonly ISpeechToTextTranscriber primaryTranscriber;
    private readonly Func<ISpeechToTextTranscriber> createFallbackTranscriber;
    private readonly object fallbackLock = new();
    private ISpeechToTextTranscriber? fallbackTranscriber;
    private VoiceSettings? lastVoiceSettings;

    /// <param name="createPrimaryTranscriber">Builds the primary, given the callback it runs when it can't serve clips yet.</param>
    public FallbackSpeechToTextTranscriber(Func<Action, ISpeechToTextTranscriber> createPrimaryTranscriber,
        Func<ISpeechToTextTranscriber> createFallbackTranscriber)
    {
        this.createFallbackTranscriber = createFallbackTranscriber;
        primaryTranscriber = createPrimaryTranscriber(() => EnsureFallbackPrepared());
        primaryTranscriber.StatusChanged += status => StatusChanged?.Invoke(status);
    }

    public event Action<string>? StatusChanged;

    public void Prepare(VoiceSettings voiceSettings)
    {
        lastVoiceSettings = voiceSettings;
        primaryTranscriber.Prepare(voiceSettings);
        lock (fallbackLock) fallbackTranscriber?.Prepare(voiceSettings); // A changed Whisper model applies to a loaded fallback.
    }

    public ILiveTranscription? BeginLiveTranscription() => primaryTranscriber.BeginLiveTranscription();

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        try
        {
            return await primaryTranscriber.TranscribeAsync(samples, cancellationToken);
        }
        catch (Exception primaryException) when (primaryException is not OperationCanceledException)
        {
            Console.WriteLine($"Live speech model couldn't transcribe ({primaryException.Message}); using Whisper.");
            return await EnsureFallbackPrepared().TranscribeAsync(samples, cancellationToken);
        }
    }

    private ISpeechToTextTranscriber EnsureFallbackPrepared()
    {
        lock (fallbackLock)
        {
            if (fallbackTranscriber is not null) return fallbackTranscriber;
            fallbackTranscriber = createFallbackTranscriber();
            fallbackTranscriber.StatusChanged += status => StatusChanged?.Invoke(status);
            fallbackTranscriber.Prepare(lastVoiceSettings ?? new VoiceSettings());
            return fallbackTranscriber;
        }
    }

    public void Dispose()
    {
        primaryTranscriber.Dispose();
        lock (fallbackLock) fallbackTranscriber?.Dispose();
    }
}

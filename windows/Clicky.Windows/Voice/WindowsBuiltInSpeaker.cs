using System.IO;
using System.Threading.Channels;
using Clicky.Windows.Configuration;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Clicky.Windows.Voice;

/// <summary>
/// Windows' own offline voice (WinRT SpeechSynthesizer, the voice set in Settings &gt; Speech), played through NAudio.
/// Only the fallback for when Kokoro isn't downloaded yet or fails: it sounds flatter, but it's always there.
/// </summary>
internal sealed class WindowsBuiltInSpeaker : ITextToSpeechSpeaker
{
    private WaveOutEvent? currentWaveOut;

    /// <summary>Bumped by every Speak/Stop, so a stop that lands while text is still being synthesized cancels it.</summary>
    private int speechGeneration;

    public event Action<string>? StatusChanged { add { } remove { } }

    public event Action? SoundStarted;

    public bool IsSpeaking => currentWaveOut is not null;

    public void Prepare(VoiceSettings voiceSettings)
    {
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken) =>
        SpeakLinesAsync(SpokenLines.Single(text), cancellationToken);

    public async Task SpeakLinesAsync(ChannelReader<SpokenLine> spokenLines, CancellationToken cancellationToken)
    {
        StopSpeaking();
        int thisSpeechGeneration = speechGeneration;
        try
        {
            await foreach (SpokenLine spokenLine in spokenLines.ReadAllAsync(cancellationToken))
            {
                if (thisSpeechGeneration != speechGeneration) return;
                await SpeakLineAsync(spokenLine, thisSpeechGeneration, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SpeakLineAsync(SpokenLine spokenLine, int thisSpeechGeneration, CancellationToken cancellationToken)
    {
        string text = spokenLine.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            spokenLine.OnStarted?.Invoke(); // A line that only points: nothing to say.
            return;
        }

        using SpeechSynthesizer speechSynthesizer = new();
        using SpeechSynthesisStream synthesizedSpeechStream = await speechSynthesizer.SynthesizeTextToStreamAsync(text);
        if (thisSpeechGeneration != speechGeneration || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        using WaveFileReader synthesizedWaveReader = new(synthesizedSpeechStream.AsStreamForRead());
        using WaveOutEvent waveOut = new();
        TaskCompletionSource playbackStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        waveOut.PlaybackStopped += (_, _) => playbackStopped.TrySetResult();
        waveOut.Init(synthesizedWaveReader);
        currentWaveOut = waveOut;
        try
        {
            waveOut.Play();
            SoundStarted?.Invoke();
            spokenLine.OnStarted?.Invoke();
            using (cancellationToken.Register(StopSpeaking))
            {
                await playbackStopped.Task;
            }
        }
        finally
        {
            if (currentWaveOut == waveOut)
            {
                currentWaveOut = null;
            }
        }
    }

    public void StopSpeaking()
    {
        speechGeneration++;
        currentWaveOut?.Stop();
    }

    public void Dispose() => StopSpeaking();
}

using NAudio.Wave;

namespace Clicky.Windows.Voice;

/// <summary>
/// Records the default microphone while push-to-talk is held: 16 kHz mono PCM16, the format
/// Whisper wants, so no resampling is needed (the WinMM driver converts). A new WaveIn device is
/// opened per recording so a microphone plugged in or switched since the last press is picked up.
/// </summary>
internal sealed class MicrophoneRecorder
{
    private readonly object recordedSamplesLock = new();
    private List<float> recordedSamples = [];
    private IWaveIn? activeWaveIn;
    private TaskCompletionSource? recordingStoppedCompletion;
    private double currentAudioPowerLevel;
    private readonly Func<IWaveIn> createWaveIn;
    private readonly Func<int> getInputDeviceCount;

    public MicrophoneRecorder() : this(CreateDefaultMicrophoneWaveIn, GetDefaultInputDeviceCount)
    {
    }

    internal static IWaveIn CreateDefaultMicrophoneWaveIn() =>
        new WaveInEvent { WaveFormat = new WaveFormat(SpeechClip.SampleRateHertz, 16, 1), BufferMilliseconds = 50 };

    internal static int GetDefaultInputDeviceCount() => WaveInEvent.DeviceCount;

    /// <summary>Takes the device factory and count so tests can stand in for the sound card.</summary>
    internal MicrophoneRecorder(Func<IWaveIn> createWaveIn, Func<int> getInputDeviceCount)
    {
        this.createWaveIn = createWaveIn;
        this.getInputDeviceCount = getInputDeviceCount;
    }

    /// <summary>Raised on the NAudio callback thread with the Mac-style 0..1 level, about every 50 ms.</summary>
    public event Action<double>? AudioPowerLevelChanged;

    /// <summary>Raised on the NAudio callback thread with each new buffer of samples (-1..1), for live transcription.</summary>
    public event Action<float[]>? SamplesRecorded;

    public bool IsRecording => activeWaveIn is not null;

    /// <summary>Starts recording. Throws <see cref="MicrophoneUnavailableException"/> with a fix-it message.</summary>
    public void Start()
    {
        if (activeWaveIn is not null)
        {
            return;
        }

        if (getInputDeviceCount() == 0)
        {
            throw new MicrophoneUnavailableException("Clicky can't find a microphone. Plug one in or turn one on in Windows sound settings.");
        }

        lock (recordedSamplesLock)
        {
            recordedSamples = [];
        }

        currentAudioPowerLevel = 0;
        IWaveIn waveIn = createWaveIn();
        TaskCompletionSource stoppedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        waveIn.DataAvailable += HandleWaveInDataAvailable;
        waveIn.RecordingStopped += (_, _) => stoppedCompletion.TrySetResult();

        try
        {
            waveIn.StartRecording();
        }
        catch (NAudio.MmException mmException)
        {
            waveIn.Dispose();
            string? privacyMessage = MicrophonePrivacy.GetBlockedMessage();
            throw new MicrophoneUnavailableException(
                privacyMessage ?? $"Clicky couldn't open the microphone ({mmException.Result}). Another app may be using it exclusively.",
                mmException);
        }

        activeWaveIn = waveIn;
        recordingStoppedCompletion = stoppedCompletion;
    }

    /// <summary>Stops recording and returns everything captured since <see cref="Start"/>, as -1..1 floats.</summary>
    public async Task<float[]> StopAsync()
    {
        IWaveIn? waveIn = activeWaveIn;
        TaskCompletionSource? stoppedCompletion = recordingStoppedCompletion;
        activeWaveIn = null;
        recordingStoppedCompletion = null;
        if (waveIn is null || stoppedCompletion is null)
        {
            return [];
        }

        waveIn.StopRecording();
        // The last buffers arrive before RecordingStopped; the timeout guards a driver that never reports it.
        await Task.WhenAny(stoppedCompletion.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        waveIn.Dispose();
        AudioPowerLevelChanged?.Invoke(0);

        lock (recordedSamplesLock)
        {
            return recordedSamples.ToArray();
        }
    }

    private void HandleWaveInDataAvailable(object? sender, WaveInEventArgs waveInEventArgs)
    {
        int sampleCount = waveInEventArgs.BytesRecorded / 2;
        float[] bufferSamples = new float[sampleCount];
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            bufferSamples[sampleIndex] = BitConverter.ToInt16(waveInEventArgs.Buffer, sampleIndex * 2) / 32768f;
        }

        lock (recordedSamplesLock)
        {
            recordedSamples.AddRange(bufferSamples);
        }

        SamplesRecorded?.Invoke(bufferSamples);
        currentAudioPowerLevel = SpeechClip.GetSmoothedAudioPowerLevel(bufferSamples, currentAudioPowerLevel);
        AudioPowerLevelChanged?.Invoke(currentAudioPowerLevel);
    }
}

internal sealed class MicrophoneUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

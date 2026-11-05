using System.IO;
using System.Windows.Threading;
using Clicky.Windows.Voice;
using NAudio.Wave;

namespace Clicky.Windows.Tests;

/// <summary>Stands in for the sound card: the test feeds samples and decides when "recording stopped" fires.</summary>
internal sealed class FakeWaveIn : IWaveIn
{
    public NAudio.MmResult? StartFailure { get; init; }
    public bool RaisesStoppedWhenStopped { get; init; } = true;
    public bool IsStarted { get; private set; }
    public bool IsDisposed { get; private set; }

    public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (StartFailure is { } startFailure) throw new NAudio.MmException(startFailure, "waveInOpen");
        IsStarted = true;
    }

    public void StopRecording()
    {
        if (RaisesStoppedWhenStopped) RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    /// <summary>Delivers 16-bit PCM like the driver does.</summary>
    public void Feed(float[] samples)
    {
        byte[] buffer = new byte[samples.Length * 2];
        for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
        {
            BitConverter.GetBytes((short)(samples[sampleIndex] * 32767)).CopyTo(buffer, sampleIndex * 2);
        }

        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, buffer.Length));
    }

    public void Dispose() => IsDisposed = true;
}

internal sealed class FakeSpeechToTextTranscriber(Func<float[], Task<string>> transcribe) : ISpeechToTextTranscriber
{
    public event Action<string>? StatusChanged { add { } remove { } }
    public void Prepare(Configuration.VoiceSettings voiceSettings) { }
    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) => transcribe(samples);
    public void Dispose() { }
}

internal static class VoiceTestAudio
{
    public static float[] Tone(double seconds, float amplitude) =>
        Enumerable.Range(0, (int)(seconds * SpeechClip.SampleRateHertz))
            .Select(sampleIndex => amplitude * (float)Math.Sin(sampleIndex * 0.1))
            .ToArray();

    public const string SynthesizedSentence = "Please open the settings menu.";

    private static readonly Lazy<float[]> synthesizedSpeechSamples = new(SynthesizeSpeech);

    /// <summary>
    /// <see cref="SynthesizedSentence"/> spoken by Windows' built-in offline synthesizer (System.Speech through Windows
    /// PowerShell 5.1) as 16 kHz mono, so real-Whisper tests have real speech without the network or a microphone.
    /// </summary>
    public static float[] SynthesizedSpeech => synthesizedSpeechSamples.Value;

    private static float[] SynthesizeSpeech()
    {
        string wavFilePath = Path.Combine(Path.GetTempPath(), $"clicky-test-speech-{Guid.NewGuid():N}.wav");
        try
        {
            string synthesisScript =
                "Add-Type -AssemblyName System.Speech; $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                "$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo 16000, ([System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen), ([System.Speech.AudioFormat.AudioChannel]::Mono); " +
                $"$synth.SetOutputToWaveFile('{wavFilePath}', $format); $synth.Speak('{SynthesizedSentence}'); $synth.Dispose()";
            using System.Diagnostics.Process synthesisProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", synthesisScript },
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
            Assert.True(synthesisProcess.WaitForExit(60_000), "Speech synthesis timed out.");
            Assert.True(File.Exists(wavFilePath), "Windows speech synthesis produced no audio.");

            using WaveFileReader waveFileReader = new(wavFilePath);
            ISampleProvider sampleProvider = waveFileReader.ToSampleProvider();
            List<float> samples = [];
            float[] readBuffer = new float[16000];
            int readCount;
            while ((readCount = sampleProvider.Read(readBuffer, 0, readBuffer.Length)) > 0)
            {
                samples.AddRange(readBuffer.Take(readCount));
            }

            return samples.ToArray();
        }
        finally
        {
            File.Delete(wavFilePath);
        }
    }

    /// <summary>The base.en model the app (or the LiveWhisper test) cached; tests that run real Whisper need it.</summary>
    public static string CachedBaseEnModelPath
    {
        get
        {
            string modelFilePath = Path.Combine(WhisperModelFile.ModelsFolderPath, "ggml-base.en.bin");
            Assert.True(File.Exists(modelFilePath),
                $"{modelFilePath} is missing. Run Clicky once (or the LiveWhisper test) to download it, then rerun.");
            return modelFilePath;
        }
    }
}

internal static class StaTestThread
{
    /// <summary>Runs a test on an STA thread with a WPF dispatcher, as the app's UI thread would.</summary>
    public static void Run(Action testAction)
    {
        Exception? testException = null;
        Thread testThread = new(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { testAction(); }
            catch (Exception exception) { testException = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        testThread.SetApartmentState(ApartmentState.STA);
        testThread.Start();
        testThread.Join();
        if (testException is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(testException).Throw();
    }

    /// <summary>Pumps the dispatcher until the task finishes (its continuations post back to this thread).</summary>
    public static void WaitFor(Task task)
    {
        DispatcherFrame frame = new();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    public static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}

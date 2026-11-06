using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Clicky.Windows.Configuration;
using Clicky.Windows.Voice;
using NAudio.Wave;
using Xunit.Abstractions;

namespace Clicky.Windows.Tests;

/// <summary>
/// Opt-in: downloads the real Whisper model (into %LOCALAPPDATA%\Clicky\models, the app's cache) and the
/// whisper.cpp "jfk.wav" sample, then transcribes it through <see cref="WhisperLocalTranscriber"/> and logs
/// load and decode times. Run with
/// <c>$env:CLICKY_LIVE_WHISPER=1; dotnet test --filter Category=LiveWhisper --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "LiveWhisper")]
public sealed class LiveWhisperTranscriptionTests(ITestOutputHelper output)
{
    private static bool IsEnabled => Environment.GetEnvironmentVariable("CLICKY_LIVE_WHISPER") == "1";

    [Theory]
    [InlineData("base.en")]
    [InlineData("small.en")]
    public async Task SampleSpeech_IsTranscribed(string whisperModelName)
    {
        if (!IsEnabled) return;

        float[] samples = await DownloadJfkSampleAsync();
        using WhisperLocalTranscriber transcriber = new();
        TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transcriber.StatusChanged += status =>
        {
            if (status.EndsWith("ready.")) modelReady.TrySetResult();
            if (status.Contains("failed")) modelReady.TrySetException(new Exception(status));
        };

        Stopwatch loadStopwatch = Stopwatch.StartNew();
        transcriber.Prepare(new VoiceSettings { WhisperModel = whisperModelName });
        await modelReady.Task.WaitAsync(TimeSpan.FromMinutes(15));
        output.WriteLine($"{whisperModelName}: download+load {loadStopwatch.ElapsedMilliseconds} ms");

        for (int run = 1; run <= 2; run++)
        {
            Stopwatch decodeStopwatch = Stopwatch.StartNew();
            string transcript = await transcriber.TranscribeAsync(samples, CancellationToken.None);
            output.WriteLine($"{whisperModelName} run {run}: {samples.Length / 16000.0:0.0} s audio in {decodeStopwatch.ElapsedMilliseconds} ms: {transcript}");
            Assert.Contains("ask not what your country can do for you", transcript, StringComparison.OrdinalIgnoreCase);
        }

        // Push-to-talk questions are a few seconds long; whisper.cpp still encodes a 30 s window, so time one.
        float[] threeSecondClip = samples.Skip(3 * 16000).Take(3 * 16000).ToArray();
        Stopwatch shortClipStopwatch = Stopwatch.StartNew();
        string shortClipTranscript = await transcriber.TranscribeAsync(threeSecondClip, CancellationToken.None);
        output.WriteLine($"{whisperModelName} 3 s clip in {shortClipStopwatch.ElapsedMilliseconds} ms: {shortClipTranscript}");
    }

    internal static async Task<float[]> DownloadJfkSampleAsync()
    {
        using HttpClient httpClient = new();
        byte[] wavBytes = await httpClient.GetByteArrayAsync("https://github.com/ggml-org/whisper.cpp/raw/master/samples/jfk.wav");
        using WaveFileReader waveFileReader = new(new MemoryStream(wavBytes));
        Assert.Equal(16000, waveFileReader.WaveFormat.SampleRate);
        Assert.Equal(1, waveFileReader.WaveFormat.Channels);
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
}

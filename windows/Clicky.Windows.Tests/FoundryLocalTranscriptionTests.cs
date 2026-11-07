using System.Diagnostics;
using Clicky.Windows.Configuration;
using Clicky.Windows.Voice;
using Xunit.Abstractions;

namespace Clicky.Windows.Tests;

/// <summary>The Foundry-with-Whisper-fallback logic with fakes, plus an opt-in run of the real Foundry Local model.</summary>
public sealed class FoundryLocalTranscriptionTests(ITestOutputHelper output)
{
    /// <summary>A transcriber whose availability the test controls; it records what it was asked.</summary>
    private sealed class ScriptedTranscriber(string name) : ISpeechToTextTranscriber
    {
        public Action? ReportUnavailable { get; set; }
        public Exception? FailsWith { get; set; }
        public int PrepareCount { get; private set; }
        public int TranscribeCount { get; private set; }
        public ILiveTranscription? LiveTranscription { get; set; }
        public event Action<string>? StatusChanged;
        public void Prepare(VoiceSettings voiceSettings)
        {
            PrepareCount++;
            StatusChanged?.Invoke($"{name} prepared");
        }

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
        {
            TranscribeCount++;
            return FailsWith is null ? Task.FromResult($"from {name}") : Task.FromException<string>(FailsWith);
        }

        public ILiveTranscription? BeginLiveTranscription() => LiveTranscription;
        public void Dispose() { }
    }

    private static (FallbackSpeechToTextTranscriber Transcriber, ScriptedTranscriber Primary, List<ScriptedTranscriber> Fallbacks) Create()
    {
        ScriptedTranscriber primary = new("foundry");
        List<ScriptedTranscriber> fallbacks = [];
        FallbackSpeechToTextTranscriber transcriber = new(onUnavailable =>
        {
            primary.ReportUnavailable = onUnavailable;
            return primary;
        }, () =>
        {
            ScriptedTranscriber fallback = new("whisper");
            fallbacks.Add(fallback);
            return fallback;
        });
        return (transcriber, primary, fallbacks);
    }

    [Fact]
    public async Task WorkingPrimary_NeverLoadsTheFallback()
    {
        (FallbackSpeechToTextTranscriber transcriber, ScriptedTranscriber primary, List<ScriptedTranscriber> fallbacks) = Create();
        transcriber.Prepare(new VoiceSettings());

        Assert.Equal("from foundry", await transcriber.TranscribeAsync(new float[16000], CancellationToken.None));
        Assert.Empty(fallbacks);
        Assert.Equal(1, primary.PrepareCount);
    }

    [Fact]
    public async Task UnavailablePrimary_GetsTheFallbackReady_AndAFailingClipGoesToIt()
    {
        (FallbackSpeechToTextTranscriber transcriber, ScriptedTranscriber primary, List<ScriptedTranscriber> fallbacks) = Create();
        List<string> statuses = [];
        transcriber.StatusChanged += statuses.Add;
        transcriber.Prepare(new VoiceSettings());

        primary.ReportUnavailable!(); // Downloading, or failed to load.
        ScriptedTranscriber fallback = Assert.Single(fallbacks);
        Assert.Equal(1, fallback.PrepareCount);
        Assert.Contains("whisper prepared", statuses);

        primary.FailsWith = new InvalidOperationException("Downloading live speech model: 12%");
        Assert.Equal("from whisper", await transcriber.TranscribeAsync(new float[16000], CancellationToken.None));
        Assert.Single(fallbacks); // Created once, reused.
    }

    [Fact]
    public async Task Cancellation_IsNotTreatedAsAFailure()
    {
        (FallbackSpeechToTextTranscriber transcriber, ScriptedTranscriber primary, List<ScriptedTranscriber> fallbacks) = Create();
        primary.FailsWith = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => transcriber.TranscribeAsync(new float[16000], CancellationToken.None));
        Assert.Empty(fallbacks);
    }

    [Fact]
    public void Factory_MakesFoundryWithWhisperFallback_ForTheDefaultSetting()
    {
        Assert.Equal(SpeechToTextProviderKind.FoundryLocal, new VoiceSettings().SpeechToText);
        using ISpeechToTextTranscriber transcriber = SpeechToTextTranscriberFactory.Create(new VoiceSettings());

        Assert.IsType<FallbackSpeechToTextTranscriber>(transcriber);
    }

    /// <summary>
    /// Opt-in: downloads (~700 MB, once) and loads the real model into %LOCALAPPDATA%\Clicky\models\foundry, then feeds speech
    /// at real-time pace like the microphone and times the transcript after the end. Run with
    /// <c>$env:CLICKY_LIVE_FOUNDRY=1; dotnet test --filter FullyQualifiedName~RealFoundry --logger "console;verbosity=detailed"</c>.
    /// </summary>
    [Fact]
    public async Task RealFoundry_TranscribesSpeechFedLive()
    {
        if (Environment.GetEnvironmentVariable("CLICKY_LIVE_FOUNDRY") != "1") return;
        float[] samples = await LiveWhisperTranscriptionTests.DownloadJfkSampleAsync();
        using FoundryLocalTranscriber transcriber = new(() => output.WriteLine("reported unavailable (downloading)"));
        TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        transcriber.StatusChanged += status =>
        {
            if (status.EndsWith("ready.")) modelReady.TrySetResult();
            if (status.Contains("failed")) modelReady.TrySetException(new Exception(status));
        };
        Stopwatch loadStopwatch = Stopwatch.StartNew();
        transcriber.Prepare(new VoiceSettings());
        await modelReady.Task.WaitAsync(TimeSpan.FromMinutes(30));
        output.WriteLine($"download+load {loadStopwatch.ElapsedMilliseconds} ms");

        using ILiveTranscription liveTranscription = transcriber.BeginLiveTranscription()!;
        Stopwatch feedClock = Stopwatch.StartNew();
        for (int offset = 0, index = 0; offset < samples.Length; offset += 800, index++) // 50 ms buffers, like the microphone.
        {
            while (feedClock.Elapsed.TotalSeconds < index * 0.05) await Task.Delay(1);
            liveTranscription.Append(samples[offset..Math.Min(samples.Length, offset + 800)]);
        }
        Stopwatch finishStopwatch = Stopwatch.StartNew();
        string transcript = await liveTranscription.FinishAsync(CancellationToken.None);
        output.WriteLine($"{samples.Length / 16000.0:0.0} s fed live; transcript {finishStopwatch.ElapsedMilliseconds} ms after the end: {transcript}");
        Assert.Contains("ask not what your country can do for you", transcript, StringComparison.OrdinalIgnoreCase);
    }
}

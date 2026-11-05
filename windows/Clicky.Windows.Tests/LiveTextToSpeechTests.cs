using System.Diagnostics;
using Clicky.Windows.Configuration;
using Clicky.Windows.Voice;
using KokoroSharp;
using KokoroSharp.Core;
using Xunit.Abstractions;

namespace Clicky.Windows.Tests;

/// <summary>
/// Opt-in, and audible: downloads the real Kokoro model (into %LOCALAPPDATA%\Clicky\models, the app's cache), then
/// times loading, time to first sound and synthesis speed on the CPU (the T7.3 CPU-vs-DirectML decision), and checks
/// that stopping really stops both Kokoro and the Windows fallback voice. Run with
/// <c>$env:CLICKY_LIVE_TTS=1; dotnet test --filter Category=LiveTts --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "LiveTts")]
public sealed class LiveTextToSpeechTests(ITestOutputHelper output)
{
    private const string TypicalReply = "That's the Save button, up in the top left corner. Click it, and your changes are kept.";
    private const string LongReply = "Here's a longer answer, so there's time to interrupt it. First, open the File menu. " +
        "Then choose Export, pick a folder, and give the file a name you'll remember later.";

    private static bool IsEnabled => Environment.GetEnvironmentVariable("CLICKY_LIVE_TTS") == "1";

    [Fact]
    public async Task Kokoro_OnCpu_IsTimed_AndCanBeInterrupted()
    {
        if (!IsEnabled) return;

        Stopwatch downloadStopwatch = Stopwatch.StartNew();
        string modelFilePath = await KokoroLocalSpeaker.EnsureModelDownloadedAsync(_ => { }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(20));
        output.WriteLine($"Model ready (download if needed) in {downloadStopwatch.ElapsedMilliseconds} ms");

        Stopwatch loadStopwatch = Stopwatch.StartNew();
        using KokoroTTS kokoroTts = new(modelFilePath);
        output.WriteLine($"Model loaded in {loadStopwatch.ElapsedMilliseconds} ms");
        foreach (ProcessModule loadedModule in Process.GetCurrentProcess().Modules)
        {
            if (loadedModule.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
                output.WriteLine($"Loaded {loadedModule.FileName} {loadedModule.FileVersionInfo.FileVersion}");
        }
        KokoroVoice heartVoice = KokoroVoiceManager.GetVoice("af_heart");

        // Synthesis speed: the whole reply rendered without playback.
        using (KokoroWavSynthesizer kokoroWavSynthesizer = KokoroWavSynthesizer.LoadModel(modelFilePath))
        {
            for (int run = 1; run <= 2; run++)
            {
                Stopwatch synthesisStopwatch = Stopwatch.StartNew();
                byte[] waveBytes = await kokoroWavSynthesizer.SynthesizeAsync(TypicalReply, heartVoice);
                double audioSeconds = (waveBytes.Length - 44) / 2.0 / 24_000;
                output.WriteLine($"Synthesis run {run}: {audioSeconds:0.0} s of audio in {synthesisStopwatch.ElapsedMilliseconds} ms " +
                    $"(real-time factor {synthesisStopwatch.Elapsed.TotalSeconds / audioSeconds:0.00})");
            }
        }

        // Time to first sound with streaming playback, which is what a reply feels like.
        for (int run = 1; run <= 2; run++)
        {
            TaskCompletionSource firstSound = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource speechCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Stopwatch speechStopwatch = Stopwatch.StartNew();
            SynthesisHandle synthesisHandle = kokoroTts.SpeakFast(TypicalReply, heartVoice);
            synthesisHandle.OnSpeechStarted += _ => firstSound.TrySetResult();
            synthesisHandle.OnSpeechCompleted += _ => speechCompleted.TrySetResult();
            await firstSound.Task.WaitAsync(TimeSpan.FromSeconds(30));
            long firstSoundMilliseconds = speechStopwatch.ElapsedMilliseconds;
            await speechCompleted.Task.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine($"Streaming run {run}: first sound after {firstSoundMilliseconds} ms, finished after {speechStopwatch.ElapsedMilliseconds} ms");
        }

        // Through the app's speaker: speaks, reports IsSpeaking, and stops promptly when interrupted.
        using KokoroLocalSpeaker kokoroLocalSpeaker = new((_, _) => Task.FromResult(modelFilePath), KokoroInference.Load,
            () => new NAudio.Wave.WaveOutEvent(), new WindowsBuiltInSpeaker());
        TaskCompletionSource modelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kokoroLocalSpeaker.StatusChanged += status => { if (status == "Voice model ready.") modelReady.TrySetResult(); };
        kokoroLocalSpeaker.Prepare(new VoiceSettings());
        await modelReady.Task.WaitAsync(TimeSpan.FromMinutes(2));

        Task longSpeech = kokoroLocalSpeaker.SpeakAsync(LongReply, CancellationToken.None);
        await Task.Delay(2500);
        Assert.True(kokoroLocalSpeaker.IsSpeaking);
        Stopwatch stopStopwatch = Stopwatch.StartNew();
        kokoroLocalSpeaker.StopSpeaking();
        await longSpeech.WaitAsync(TimeSpan.FromSeconds(2));
        output.WriteLine($"Interrupted after 2.5 s; SpeakAsync returned {stopStopwatch.ElapsedMilliseconds} ms after StopSpeaking");
        Assert.False(kokoroLocalSpeaker.IsSpeaking);

        Stopwatch fullSpeechStopwatch = Stopwatch.StartNew();
        await kokoroLocalSpeaker.SpeakAsync(TypicalReply, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"Full typical reply through KokoroLocalSpeaker: {fullSpeechStopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task WindowsVoice_Speaks_AndCanBeInterrupted()
    {
        if (!IsEnabled) return;

        using WindowsBuiltInSpeaker windowsBuiltInSpeaker = new();
        Stopwatch fullSpeechStopwatch = Stopwatch.StartNew();
        await windowsBuiltInSpeaker.SpeakAsync(TypicalReply, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"Windows voice, typical reply: {fullSpeechStopwatch.ElapsedMilliseconds} ms");
        Assert.True(fullSpeechStopwatch.ElapsedMilliseconds > 2000, "the reply should take seconds to say");

        using CancellationTokenSource speechCancellation = new();
        Task longSpeech = windowsBuiltInSpeaker.SpeakAsync(LongReply, speechCancellation.Token);
        await Task.Delay(2000);
        Assert.True(windowsBuiltInSpeaker.IsSpeaking);
        speechCancellation.Cancel();
        await longSpeech.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(windowsBuiltInSpeaker.IsSpeaking);
    }
}

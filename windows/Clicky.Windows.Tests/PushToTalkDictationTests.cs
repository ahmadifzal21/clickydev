using System.Windows.Threading;
using Clicky.Windows.Companion;
using Clicky.Windows.Display;
using Clicky.Windows.Input;
using Clicky.Windows.Overlay;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

/// <summary>
/// Drives the press / release flow on an STA dispatcher thread with the real overlay, a fake microphone
/// and a fake transcriber, checking the voice states the overlay (and panel) see and the notes shown.
/// </summary>
public class PushToTalkDictationTests
{
    private sealed class DictationHarness : IDisposable
    {
        private readonly DisplayMonitorCatalog displayMonitorCatalog = new();
        public CursorOverlayManager CursorOverlayManager { get; }
        public List<CompanionVoiceState> VoiceStates { get; } = [];
        public List<string> VoiceNotes { get; } = [];
        public List<FakeWaveIn> CreatedWaveIns { get; } = [];
        public PushToTalkDictation PushToTalkDictation { get; }

        public DictationHarness(Func<float[], Task<string>> transcribe, int inputDeviceCount = 1, bool raisesStoppedWhenStopped = true,
            Func<string, TurnTimeline, CancellationToken, Task>? answerTranscriptAsync = null)
        {
            CursorOverlayManager = new CursorOverlayManager(displayMonitorCatalog, isCursorAlwaysVisible: false);
            // The overlay also reports status when it fades in or out; keep only real voice-state changes (starting from Idle).
            CursorOverlayManager.CompanionStatusChanged += (_, voiceState) =>
            {
                if (voiceState != (VoiceStates.Count == 0 ? CompanionVoiceState.Idle : VoiceStates[^1])) VoiceStates.Add(voiceState);
            };
            MicrophoneRecorder microphoneRecorder = new(() =>
            {
                FakeWaveIn fakeWaveIn = new() { RaisesStoppedWhenStopped = raisesStoppedWhenStopped };
                CreatedWaveIns.Add(fakeWaveIn);
                return fakeWaveIn;
            }, () => inputDeviceCount);
            PushToTalkDictation = new PushToTalkDictation(CursorOverlayManager, new FakeSpeechToTextTranscriber(transcribe),
                VoiceNotes.Add, Dispatcher.CurrentDispatcher, microphoneRecorder, answerTranscriptAsync: answerTranscriptAsync);
        }

        public void Press() => StaTestThread.WaitFor(PushToTalkDictation.HandleShortcutTransition(PushToTalkShortcutTransition.Pressed));

        public Task Release() => PushToTalkDictation.HandleShortcutTransition(PushToTalkShortcutTransition.Released);

        public void SpeakAndRelease(float[] samples)
        {
            Press();
            CreatedWaveIns[^1].Feed(samples);
            StaTestThread.WaitFor(Release());
            StaTestThread.DrainDispatcher();
        }

        public void Dispose()
        {
            CursorOverlayManager.Dispose();
            displayMonitorCatalog.Dispose();
        }
    }

    [Fact]
    public void Speech_GoesListeningProcessingIdle_AndShowsTheTranscript() => StaTestThread.Run(() =>
    {
        float[]? transcribedSamples = null;
        using DictationHarness harness = new(samples => { transcribedSamples = samples; return Task.FromResult("what is this button"); });
        float[] spokenSamples = VoiceTestAudio.Tone(1, 0.3f);

        harness.SpeakAndRelease(spokenSamples);

        Assert.Equal([CompanionVoiceState.Listening, CompanionVoiceState.Processing, CompanionVoiceState.Idle], harness.VoiceStates);
        Assert.Equal(["Heard: \"what is this button\""], harness.VoiceNotes);
        Assert.Equal(spokenSamples.Length, transcribedSamples!.Length);
        Assert.True(harness.CreatedWaveIns.Single().IsDisposed);
    });

    [Fact]
    public void Transcript_IsHandedToTheAnswerStep_WhichOwnsTheRestOfTheTurn() => StaTestThread.Run(() =>
    {
        List<string> answeredTranscripts = [];
        using DictationHarness harness = new(_ => Task.FromResult("where is save"),
            answerTranscriptAsync: (transcript, _, _) => { answeredTranscripts.Add(transcript); return Task.CompletedTask; });

        harness.SpeakAndRelease(VoiceTestAudio.Tone(1, 0.3f));

        Assert.Equal(["where is save"], answeredTranscripts);
        Assert.Equal(["Heard: \"where is save\""], harness.VoiceNotes);
        // The answer step (faked here) moves on from Processing; dictation doesn't end the turn itself.
        Assert.Equal([CompanionVoiceState.Listening, CompanionVoiceState.Processing], harness.VoiceStates);
    });

    [Fact]
    public void EmptyTranscript_ReturnsToIdle_WithoutAnswering() => StaTestThread.Run(() =>
    {
        bool wasAnswered = false;
        using DictationHarness harness = new(_ => Task.FromResult(""),
            answerTranscriptAsync: (_, _, _) => { wasAnswered = true; return Task.CompletedTask; });

        harness.SpeakAndRelease(VoiceTestAudio.Tone(1, 0.3f));

        Assert.False(wasAnswered);
        Assert.Equal([CompanionVoiceState.Listening, CompanionVoiceState.Processing, CompanionVoiceState.Idle], harness.VoiceStates);
    });

    [Fact]
    public void NoWordsInTranscript_SaysSo() => StaTestThread.Run(() =>
    {
        using DictationHarness harness = new(_ => Task.FromResult(""));

        harness.SpeakAndRelease(VoiceTestAudio.Tone(1, 0.3f));

        Assert.Equal(["Didn't catch any words."], harness.VoiceNotes);
        Assert.Equal(CompanionVoiceState.Idle, harness.VoiceStates[^1]);
    });

    [Fact]
    public void TranscriberFailure_IsShown_AndReturnsToIdle() => StaTestThread.Run(() =>
    {
        using DictationHarness harness = new(_ => Task.FromException<string>(new InvalidOperationException("Speech model base.en failed to load: HTTP 503")));

        harness.SpeakAndRelease(VoiceTestAudio.Tone(1, 0.3f));

        Assert.Equal(["Couldn't transcribe: Speech model base.en failed to load: HTTP 503"], harness.VoiceNotes);
        Assert.Equal(CompanionVoiceState.Idle, harness.VoiceStates[^1]);
    });

    [Fact]
    public void AccidentalTap_IsIgnoredQuietly() => StaTestThread.Run(() =>
    {
        bool wasTranscribed = false;
        using DictationHarness harness = new(_ => { wasTranscribed = true; return Task.FromResult("x"); });

        harness.SpeakAndRelease(VoiceTestAudio.Tone(0.1, 0.3f));

        Assert.False(wasTranscribed);
        Assert.Empty(harness.VoiceNotes);
        Assert.Equal([CompanionVoiceState.Listening, CompanionVoiceState.Idle], harness.VoiceStates);
    });

    [Fact]
    public void QuietClip_AsksAboutTheMicrophone() => StaTestThread.Run(() =>
    {
        using DictationHarness harness = new(_ => Task.FromResult("x"));

        harness.SpeakAndRelease(VoiceTestAudio.Tone(1, 0.002f));

        Assert.Equal(["Didn't hear anything. Is the right microphone selected and unmuted?"], harness.VoiceNotes);
    });

    [Fact]
    public void AllZeroAudio_ExplainsTheBlockedOrMutedMicrophone() => StaTestThread.Run(() =>
    {
        using DictationHarness harness = new(_ => Task.FromResult("x"));

        harness.SpeakAndRelease(new float[16000]);

        Assert.Contains(MicrophonePrivacy.FixInstructions, Assert.Single(harness.VoiceNotes));
        Assert.Equal(CompanionVoiceState.Idle, harness.VoiceStates[^1]);
    });

    [Fact]
    public void MissingMicrophone_IsShownOnPress_WithoutListening() => StaTestThread.Run(() =>
    {
        using DictationHarness harness = new(_ => Task.FromResult("x"), inputDeviceCount: 0);

        harness.Press();
        StaTestThread.WaitFor(harness.Release()); // Nothing was recording: no-op.

        Assert.Contains("can't find a microphone", Assert.Single(harness.VoiceNotes));
        Assert.Empty(harness.VoiceStates); // Never left Idle.
    });

    [Fact]
    public void WithoutAnInjectedRecorder_UsesTheRealMicrophone_AndReleaseWithoutPressIsANoOp() => StaTestThread.Run(() =>
    {
        using DisplayMonitorCatalog displayMonitorCatalog = new();
        using CursorOverlayManager cursorOverlayManager = new(displayMonitorCatalog, isCursorAlwaysVisible: false);
        List<string> voiceNotes = [];
        PushToTalkDictation pushToTalkDictation = new(cursorOverlayManager, new FakeSpeechToTextTranscriber(_ => Task.FromResult("x")),
            voiceNotes.Add, Dispatcher.CurrentDispatcher);

        // No press, so the real microphone is never opened.
        StaTestThread.WaitFor(pushToTalkDictation.HandleShortcutTransition(PushToTalkShortcutTransition.Released));

        Assert.Empty(voiceNotes);
    });

    [Fact]
    public void NoneTransition_DoesNothing() => StaTestThread.Run(() =>
    {
        using DictationHarness harness = new(_ => Task.FromResult("x"));

        StaTestThread.WaitFor(harness.PushToTalkDictation.HandleShortcutTransition(PushToTalkShortcutTransition.None));

        Assert.Empty(harness.VoiceStates);
        Assert.Empty(harness.CreatedWaveIns);
    });

    [Fact]
    public void NewPressDuringSlowTranscription_KeepsTheOldResultFromOverwritingTheNewRecording() => StaTestThread.Run(() =>
    {
        TaskCompletionSource<string> firstTranscription = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue<Task<string>> transcriptions = new([firstTranscription.Task, Task.FromResult("second question")]);
        using DictationHarness harness = new(_ => transcriptions.Dequeue());

        harness.Press();
        harness.CreatedWaveIns[0].Feed(VoiceTestAudio.Tone(1, 0.3f));
        Task firstRelease = harness.Release();
        StaTestThread.DrainDispatcher();
        harness.Press(); // Talking again before the first answer arrived.
        firstTranscription.SetResult("first question");
        StaTestThread.WaitFor(firstRelease);
        StaTestThread.DrainDispatcher();

        Assert.Empty(harness.VoiceNotes);
        Assert.Equal(CompanionVoiceState.Listening, harness.VoiceStates[^1]);

        harness.CreatedWaveIns[1].Feed(VoiceTestAudio.Tone(1, 0.3f));
        StaTestThread.WaitFor(harness.Release());
        Assert.Equal(["Heard: \"second question\""], harness.VoiceNotes);
    });

    [Fact]
    public void NewPressWhileTheDeviceIsStillClosing_LeavesTheNewRecordingAlone() => StaTestThread.Run(() =>
    {
        bool wasTranscribed = false;
        // The fake driver never reports "stopped", so the first release waits out the recorder's 1 s timeout.
        using DictationHarness harness = new(_ => { wasTranscribed = true; return Task.FromResult("x"); }, raisesStoppedWhenStopped: false);

        harness.Press();
        harness.CreatedWaveIns[0].Feed(VoiceTestAudio.Tone(1, 0.3f));
        Task firstRelease = harness.Release();
        harness.Press();
        StaTestThread.WaitFor(firstRelease);
        StaTestThread.DrainDispatcher();

        Assert.False(wasTranscribed);
        Assert.Empty(harness.VoiceNotes);
        Assert.Equal(CompanionVoiceState.Listening, harness.VoiceStates[^1]);
        Assert.Equal(2, harness.CreatedWaveIns.Count);
    });
}

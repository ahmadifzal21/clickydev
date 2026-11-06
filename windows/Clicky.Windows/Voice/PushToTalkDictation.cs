using System.Diagnostics;
using System.Windows.Threading;
using Clicky.Windows.Companion;
using Clicky.Windows.Input;
using Clicky.Windows.Overlay;

namespace Clicky.Windows.Voice;

/// <summary>
/// Hold the shortcut to record, release to transcribe. The transcript is logged, shown in the panel and handed to the
/// answer step (Mac: CompanionManager.sendTranscriptToClaudeWithScreenshot); an empty one just returns to idle.
/// All methods run on the WPF dispatcher thread.
/// </summary>
internal sealed class PushToTalkDictation
{
    private readonly CursorOverlayManager cursorOverlayManager;
    private readonly ISpeechToTextTranscriber speechToTextTranscriber;
    private readonly Action<string> showVoiceNote;
    private readonly MicrophoneRecorder microphoneRecorder;
    private readonly CompanionInteractionStateMachine interactionStateMachine;

    /// <summary>Answers a non-empty transcript (capture, backend, speech, pointing); null = dictation only (E6 behaviour).</summary>
    private readonly Func<string, CancellationToken, Task>? answerTranscriptAsync;

    /// <summary>Bumped on every press, so a slow transcription from an earlier press can't overwrite a newer state.</summary>
    private int dictationGeneration;

    public PushToTalkDictation(CursorOverlayManager cursorOverlayManager, ISpeechToTextTranscriber speechToTextTranscriber,
        Action<string> showVoiceNote, Dispatcher dispatcher, MicrophoneRecorder? microphoneRecorder = null,
        CompanionInteractionStateMachine? interactionStateMachine = null,
        Func<string, CancellationToken, Task>? answerTranscriptAsync = null)
    {
        this.answerTranscriptAsync = answerTranscriptAsync;
        this.interactionStateMachine = interactionStateMachine ?? new CompanionInteractionStateMachine(cursorOverlayManager.SetVoiceState);
        this.microphoneRecorder = microphoneRecorder ?? new MicrophoneRecorder();
        this.cursorOverlayManager = cursorOverlayManager;
        this.speechToTextTranscriber = speechToTextTranscriber;
        this.showVoiceNote = showVoiceNote;
        // Levels arrive on NAudio's callback thread; the waveform lives on the UI thread.
        this.microphoneRecorder.AudioPowerLevelChanged += audioPowerLevel =>
            dispatcher.BeginInvoke(() => cursorOverlayManager.SetAudioPowerLevel(audioPowerLevel));
    }

    /// <summary>The returned task finishes when a release has been transcribed (the app ignores it; tests await it).</summary>
    public Task HandleShortcutTransition(PushToTalkShortcutTransition shortcutTransition)
    {
        if (shortcutTransition == PushToTalkShortcutTransition.Pressed)
        {
            StartRecording();
        }
        else if (shortcutTransition == PushToTalkShortcutTransition.Released)
        {
            return StopRecordingAndTranscribeAsync();
        }

        return Task.CompletedTask;
    }

    private void StartRecording()
    {
        dictationGeneration++;
        cursorOverlayManager.ShowTransiently();
        try
        {
            microphoneRecorder.Start();
        }
        catch (MicrophoneUnavailableException microphoneUnavailableException)
        {
            FinishWithNote(microphoneUnavailableException.Message);
            return;
        }

        interactionStateMachine.Apply(CompanionInteractionTrigger.ShortcutPressed);
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        if (!microphoneRecorder.IsRecording)
        {
            return;
        }

        int thisDictationGeneration = dictationGeneration;
        float[] recordedSamples = await microphoneRecorder.StopAsync();
        if (thisDictationGeneration != dictationGeneration)
        {
            return; // Pressed again while the device was closing; the new recording owns the state.
        }

        double recordedSeconds = (double)recordedSamples.Length / SpeechClip.SampleRateHertz;

        switch (SpeechClip.GetRejection(recordedSamples))
        {
            case SpeechClipRejection.TooShort:
                FinishWithNote(null); // An accidental tap; stay quiet.
                return;
            case SpeechClipRejection.AllZeroSamples:
                FinishWithNote(MicrophonePrivacy.GetBlockedMessage()
                    ?? "The microphone sent only silence. It may be muted, or " + MicrophonePrivacy.FixInstructions);
                return;
            case SpeechClipRejection.Silent:
                FinishWithNote("Didn't hear anything. Is the right microphone selected and unmuted?");
                return;
        }

        interactionStateMachine.Apply(CompanionInteractionTrigger.RecordingFinished);
        Stopwatch transcriptionStopwatch = Stopwatch.StartNew();
        string voiceNote;
        string transcript = "";
        try
        {
            transcript = await speechToTextTranscriber.TranscribeAsync(recordedSamples, CancellationToken.None);
            Console.WriteLine($"Transcribed {recordedSeconds:0.0} s of audio in {transcriptionStopwatch.ElapsedMilliseconds} ms: \"{transcript}\"");
            voiceNote = transcript.Length == 0 ? "Didn't catch any words." : $"Heard: \"{transcript}\"";
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Transcription failed: {exception}");
            voiceNote = $"Couldn't transcribe: {exception.Message}";
        }

        if (thisDictationGeneration != dictationGeneration) return;
        if (transcript.Length > 0 && answerTranscriptAsync is not null)
        {
            showVoiceNote(voiceNote);
            await answerTranscriptAsync(transcript, CancellationToken.None); // Ends Idle and schedules the transient hide itself.
            return;
        }

        FinishWithNote(voiceNote);
    }

    private void FinishWithNote(string? voiceNote)
    {
        if (voiceNote is not null)
        {
            Console.WriteLine($"Push-to-talk: {voiceNote}");
            showVoiceNote(voiceNote);
        }

        interactionStateMachine.Apply(CompanionInteractionTrigger.InteractionEnded);
        cursorOverlayManager.ScheduleTransientHide();
    }
}

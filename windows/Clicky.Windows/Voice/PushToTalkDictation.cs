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

    /// <summary>
    /// Answers a non-empty transcript (capture, backend, speech, pointing) as the given turn of the event log; null = dictation
    /// only (E6 behaviour).
    /// </summary>
    private readonly Func<string, int, CancellationToken, Task>? answerTranscriptAsync;

    /// <summary>Each release starts a turn here (its clock starts at the release); the transcript is its first stage.</summary>
    private readonly CompanionTurnEventLog turnEventLog;

    /// <summary>Called when a release ends without a question to answer (a tap, silence, an empty transcript).</summary>
    private readonly Action? onReleaseWithoutQuestion;

    /// <summary>Bumped on every press, so a slow transcription from an earlier press can't overwrite a newer state.</summary>
    private int dictationGeneration;

    /// <summary>The current recording's live transcription (fed from the microphone thread), or null for Whisper-style.</summary>
    private volatile ILiveTranscription? liveTranscription;

    public PushToTalkDictation(CursorOverlayManager cursorOverlayManager, ISpeechToTextTranscriber speechToTextTranscriber,
        Action<string> showVoiceNote, Dispatcher dispatcher, MicrophoneRecorder? microphoneRecorder = null,
        CompanionInteractionStateMachine? interactionStateMachine = null,
        Func<string, int, CancellationToken, Task>? answerTranscriptAsync = null, CompanionTurnEventLog? turnEventLog = null,
        Action? onReleaseWithoutQuestion = null)
    {
        this.onReleaseWithoutQuestion = onReleaseWithoutQuestion;
        this.turnEventLog = turnEventLog ?? new CompanionTurnEventLog();
        this.answerTranscriptAsync = answerTranscriptAsync;
        this.interactionStateMachine = interactionStateMachine ?? new CompanionInteractionStateMachine(cursorOverlayManager.SetVoiceState);
        this.microphoneRecorder = microphoneRecorder ?? new MicrophoneRecorder();
        this.cursorOverlayManager = cursorOverlayManager;
        this.speechToTextTranscriber = speechToTextTranscriber;
        this.showVoiceNote = showVoiceNote;
        // Levels arrive on NAudio's callback thread; the waveform lives on the UI thread.
        this.microphoneRecorder.AudioPowerLevelChanged += audioPowerLevel =>
            dispatcher.BeginInvoke(() => cursorOverlayManager.SetAudioPowerLevel(audioPowerLevel));
        this.microphoneRecorder.SamplesRecorded += samples => liveTranscription?.Append(samples);
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

    /// <summary>Stops recording and drops it without a word (a quick tap, which is never a question).</summary>
    public Task DiscardRecording() => StopRecordingAndTranscribeAsync(isDiscarded: true);

    private void StartRecording()
    {
        dictationGeneration++;
        cursorOverlayManager.ShowTransiently();
        liveTranscription?.Dispose();
        liveTranscription = speechToTextTranscriber.BeginLiveTranscription(); // Before the microphone, so no buffer is missed.
        try
        {
            microphoneRecorder.Start();
        }
        catch (MicrophoneUnavailableException microphoneUnavailableException)
        {
            liveTranscription?.Dispose();
            liveTranscription = null;
            FinishWithNote(microphoneUnavailableException.Message);
            return;
        }

        interactionStateMachine.Apply(CompanionInteractionTrigger.ShortcutPressed);
    }

    private async Task StopRecordingAndTranscribeAsync(bool isDiscarded = false)
    {
        if (!microphoneRecorder.IsRecording)
        {
            return;
        }

        int thisDictationGeneration = dictationGeneration;
        int turnId = turnEventLog.StartTurn("release");
        bool isAnswering = false;
        ILiveTranscription? thisLiveTranscription = liveTranscription;
        try
        {
            float[] recordedSamples = await microphoneRecorder.StopAsync(); // The last buffers still reach the live transcription.
            if (liveTranscription == thisLiveTranscription) liveTranscription = null;
            if (thisDictationGeneration != dictationGeneration)
            {
                return; // Pressed again while the device was closing; the new recording owns the state.
            }

            if (isDiscarded)
            {
                FinishWithNote(null);
                return;
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
                string transcribedHow = "after the release";
                if (thisLiveTranscription is not null)
                {
                    try
                    {
                        transcript = await thisLiveTranscription.FinishAsync(CancellationToken.None);
                        transcribedHow = "live";
                    }
                    catch (Exception liveException)
                    {
                        Console.WriteLine($"Live transcription failed ({liveException.Message}); transcribing the recording instead.");
                        transcript = await speechToTextTranscriber.TranscribeAsync(recordedSamples, CancellationToken.None);
                    }
                }
                else
                {
                    transcript = await speechToTextTranscriber.TranscribeAsync(recordedSamples, CancellationToken.None);
                }
                turnEventLog.Append(turnId, CompanionTurnEventKind.TranscriptReady, transcript);
                Console.WriteLine($"Transcribed {recordedSeconds:0.0} s of audio {transcribedHow}, ready {transcriptionStopwatch.ElapsedMilliseconds} ms after: \"{transcript}\"");
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
                isAnswering = true; // The answer ends the turn in the log.
                await answerTranscriptAsync(transcript, turnId, CancellationToken.None); // Ends Idle and schedules the transient hide itself.
                return;
            }

            FinishWithNote(voiceNote);
        }
        finally
        {
            thisLiveTranscription?.Dispose(); // Finished, or dropped (a tap, silence, a newer press).
            if (!isAnswering)
            {
                turnEventLog.Append(turnId, CompanionTurnEventKind.TurnEnded);
                if (thisDictationGeneration == dictationGeneration) onReleaseWithoutQuestion?.Invoke(); // A newer press owns its own.
            }
        }
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

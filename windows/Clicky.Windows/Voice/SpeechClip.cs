using System.Text.RegularExpressions;

namespace Clicky.Windows.Voice;

/// <summary>Why a recorded push-to-talk clip wasn't sent to the transcriber.</summary>
internal enum SpeechClipRejection
{
    None,
    TooShort,
    Silent,

    /// Every sample is exactly zero. A real microphone always has some noise, so this is what
    /// Windows hands a desktop app whose microphone access is turned off in privacy settings.
    AllZeroSamples
}

/// <summary>
/// Pure checks and clean-up around a recorded clip of 16 kHz mono float samples (-1..1),
/// kept free of NAudio and Whisper so they can be unit tested.
/// </summary>
internal static class SpeechClip
{
    public const int SampleRateHertz = 16000;

    /// <summary>Shorter than this is an accidental tap of the shortcut, not speech.</summary>
    public const double MinimumSpeechDurationSeconds = 0.3;

    /// <summary>Peak amplitude below about -40 dBFS: nothing was said loudly enough to transcribe.</summary>
    public const float SilentPeakAmplitude = 0.01f;

    /// <summary>
    /// whisper.cpp ignores input shorter than 1000 ms ("consider padding the input audio with
    /// silence"), so short clips are padded a little past that.
    /// </summary>
    public const double MinimumWhisperInputSeconds = 1.1;

    public static SpeechClipRejection GetRejection(float[] samples)
    {
        if (samples.Length < MinimumSpeechDurationSeconds * SampleRateHertz)
        {
            return SpeechClipRejection.TooShort;
        }

        float peakAmplitude = 0;
        foreach (float sample in samples)
        {
            peakAmplitude = Math.Max(peakAmplitude, Math.Abs(sample));
        }

        if (peakAmplitude == 0)
        {
            return SpeechClipRejection.AllZeroSamples;
        }

        return peakAmplitude < SilentPeakAmplitude ? SpeechClipRejection.Silent : SpeechClipRejection.None;
    }

    public static float[] PadWithSilenceForWhisper(float[] samples)
    {
        int minimumSampleCount = (int)(MinimumWhisperInputSeconds * SampleRateHertz);
        if (samples.Length >= minimumSampleCount)
        {
            return samples;
        }

        float[] paddedSamples = new float[minimumSampleCount];
        Array.Copy(samples, paddedSamples, samples.Length);
        return paddedSamples;
    }

    /// <summary>
    /// Joins Whisper segments and drops its non-speech markers such as "[BLANK_AUDIO]" or
    /// "(keyboard clicking)", which it emits for silence and noise.
    /// </summary>
    public static string CleanTranscript(IEnumerable<string> segmentTexts)
    {
        string joinedTranscript = string.Join(" ", segmentTexts);
        string transcriptWithoutMarkers = Regex.Replace(joinedTranscript, @"\[[^\]]*\]|\([^)]*\)", " ");
        return Regex.Replace(transcriptWithoutMarkers, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Mac <c>BuddyDictationManager.updateAudioPowerLevel</c>: RMS boosted by 10.2 and clamped to
    /// 0..1, then smoothed so it falls back slowly (keeps 72% of the previous level).
    /// </summary>
    public static double GetSmoothedAudioPowerLevel(ReadOnlySpan<float> bufferSamples, double previousAudioPowerLevel)
    {
        if (bufferSamples.Length == 0)
        {
            return previousAudioPowerLevel;
        }

        double summedSquares = 0;
        foreach (float sample in bufferSamples)
        {
            summedSquares += sample * sample;
        }

        double rootMeanSquare = Math.Sqrt(summedSquares / bufferSamples.Length);
        double boostedLevel = Math.Clamp(rootMeanSquare * 10.2, 0, 1);
        return Math.Max(boostedLevel, previousAudioPowerLevel * 0.72);
    }
}

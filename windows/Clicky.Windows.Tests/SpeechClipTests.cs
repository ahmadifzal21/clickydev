using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

public class SpeechClipTests
{
    private static float[] Tone(double seconds, float amplitude) =>
        Enumerable.Range(0, (int)(seconds * SpeechClip.SampleRateHertz))
            .Select(sampleIndex => amplitude * (float)Math.Sin(sampleIndex * 0.1))
            .ToArray();

    [Fact]
    public void TapShorterThanMinimum_IsTooShort() =>
        Assert.Equal(SpeechClipRejection.TooShort, SpeechClip.GetRejection(Tone(0.2, 0.5f)));

    [Fact]
    public void AllZeroSamples_PointAtBlockedMicrophone() =>
        Assert.Equal(SpeechClipRejection.AllZeroSamples, SpeechClip.GetRejection(new float[SpeechClip.SampleRateHertz]));

    [Fact]
    public void QuietNoise_IsSilent() =>
        Assert.Equal(SpeechClipRejection.Silent, SpeechClip.GetRejection(Tone(1, 0.002f)));

    [Fact]
    public void AudibleClip_IsAccepted() =>
        Assert.Equal(SpeechClipRejection.None, SpeechClip.GetRejection(Tone(0.5, 0.2f)));

    [Fact]
    public void ShortClip_IsPaddedPastWhisperMinimum_AndLongClipIsUntouched()
    {
        float[] shortClip = Tone(0.5, 0.2f);
        float[] paddedClip = SpeechClip.PadWithSilenceForWhisper(shortClip);
        Assert.Equal((int)(SpeechClip.MinimumWhisperInputSeconds * SpeechClip.SampleRateHertz), paddedClip.Length);
        Assert.Equal(shortClip, paddedClip.Take(shortClip.Length));
        Assert.Equal(0, paddedClip[^1]);

        float[] longClip = Tone(2, 0.2f);
        Assert.Same(longClip, SpeechClip.PadWithSilenceForWhisper(longClip));
    }

    [Fact]
    public void CleanTranscript_JoinsSegments_AndDropsNonSpeechMarkers()
    {
        Assert.Equal("what is this button? it does nothing",
            SpeechClip.CleanTranscript([" what is this button?", "[BLANK_AUDIO]", " (keyboard clicking) it does  nothing "]));
        Assert.Equal("", SpeechClip.CleanTranscript(["[BLANK_AUDIO]"]));
    }

    [Fact]
    public void AudioPowerLevel_MatchesMacBoostAndDecay()
    {
        Assert.Equal(1, SpeechClip.GetSmoothedAudioPowerLevel(Enumerable.Repeat(0.5f, 800).ToArray(), 0));
        Assert.Equal(0.01 * 10.2, SpeechClip.GetSmoothedAudioPowerLevel(Enumerable.Repeat(0.01f, 800).ToArray(), 0), 5);
        // Silence after a loud buffer falls back to 72% instead of dropping to zero.
        Assert.Equal(0.72, SpeechClip.GetSmoothedAudioPowerLevel(new float[800], 1), 5);
        // An empty buffer carries no information: keep the previous level.
        Assert.Equal(0.4, SpeechClip.GetSmoothedAudioPowerLevel([], 0.4));
    }

    [Theory]
    [InlineData("base.en", true)]
    [InlineData("small.en-q5_1", true)]
    [InlineData("../evil", false)]
    [InlineData("base.en/x", false)]
    [InlineData("", false)]
    public void ModelNames_FromConfigAreValidatedBeforeUseInPathsAndUrls(string whisperModelName, bool isValid) =>
        Assert.Equal(isValid, WhisperModelFile.IsValidModelName(whisperModelName));
}

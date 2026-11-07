using System.Diagnostics;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

public class MicrophoneRecorderTests
{
    [Fact]
    public void NoInputDevices_ExplainsThatNoMicrophoneWasFound()
    {
        MicrophoneRecorder microphoneRecorder = new(() => new FakeWaveIn(), () => 0);

        MicrophoneUnavailableException exception = Assert.Throws<MicrophoneUnavailableException>(microphoneRecorder.Start);

        Assert.Contains("can't find a microphone", exception.Message);
        Assert.False(microphoneRecorder.IsRecording);
    }

    [Fact]
    public void DeviceThatFailsToOpen_IsDisposedAndReported()
    {
        FakeWaveIn fakeWaveIn = new() { StartFailure = NAudio.MmResult.AlreadyAllocated };
        MicrophoneRecorder microphoneRecorder = new(() => fakeWaveIn, () => 1);

        MicrophoneUnavailableException exception = Assert.Throws<MicrophoneUnavailableException>(microphoneRecorder.Start);

        Assert.True(fakeWaveIn.IsDisposed);
        Assert.False(microphoneRecorder.IsRecording);
        Assert.IsType<NAudio.MmException>(exception.InnerException);
        // Either this PC's privacy switches are off, or the generic message names the driver error.
        Assert.True(exception.Message.Contains("AlreadyAllocated") || exception.Message.Contains(MicrophonePrivacy.FixInstructions));
    }

    [Fact]
    public async Task RecordedBuffers_ComeBackAsFloats_WithLevelsWhileRecordingAndZeroAtStop()
    {
        FakeWaveIn fakeWaveIn = new();
        int createdWaveInCount = 0;
        MicrophoneRecorder microphoneRecorder = new(() => { createdWaveInCount++; return fakeWaveIn; }, () => 1);
        List<double> audioPowerLevels = [];
        microphoneRecorder.AudioPowerLevelChanged += audioPowerLevels.Add;

        microphoneRecorder.Start();
        microphoneRecorder.Start(); // Already recording: ignored, no second device.
        Assert.True(microphoneRecorder.IsRecording);
        Assert.True(fakeWaveIn.IsStarted);
        fakeWaveIn.Feed([0.5f, -0.5f, 0.25f]);
        fakeWaveIn.Feed([0f, 0.1f]);
        float[] recordedSamples = await microphoneRecorder.StopAsync();

        Assert.Equal(1, createdWaveInCount);
        Assert.False(microphoneRecorder.IsRecording);
        Assert.True(fakeWaveIn.IsDisposed);
        Assert.Equal(5, recordedSamples.Length);
        Assert.Equal(0.5f, recordedSamples[0], 3);
        Assert.Equal(-0.5f, recordedSamples[1], 3);
        Assert.Equal(0.1f, recordedSamples[4], 3);
        Assert.Equal(3, audioPowerLevels.Count);
        Assert.True(audioPowerLevels[0] > 0);
        Assert.Equal(0, audioPowerLevels[^1]);
    }

    [Fact]
    public async Task EachRecording_StartsEmpty()
    {
        Queue<FakeWaveIn> fakeWaveIns = new([new FakeWaveIn(), new FakeWaveIn()]);
        FakeWaveIn? currentWaveIn = null;
        MicrophoneRecorder microphoneRecorder = new(() => currentWaveIn = fakeWaveIns.Dequeue(), () => 1);

        microphoneRecorder.Start();
        currentWaveIn!.Feed([0.5f, 0.5f]);
        await microphoneRecorder.StopAsync();
        microphoneRecorder.Start();
        currentWaveIn.Feed([0.25f]);

        Assert.Single(await microphoneRecorder.StopAsync());
    }

    [Fact]
    public async Task StopWithoutStart_ReturnsNothing()
    {
        MicrophoneRecorder microphoneRecorder = new(() => new FakeWaveIn(), () => 1);

        Assert.Empty(await microphoneRecorder.StopAsync());
    }

    [Fact]
    public async Task DriverThatNeverReportsStopped_StillReturnsAfterTheTimeout()
    {
        FakeWaveIn fakeWaveIn = new() { RaisesStoppedWhenStopped = false };
        MicrophoneRecorder microphoneRecorder = new(() => fakeWaveIn, () => 1);
        microphoneRecorder.Start();
        fakeWaveIn.Feed([0.5f]);

        Stopwatch stopwatch = Stopwatch.StartNew();
        float[] recordedSamples = await microphoneRecorder.StopAsync();

        Assert.Single(recordedSamples);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 900, 5000);
        Assert.True(fakeWaveIn.IsDisposed);
    }

    [Fact]
    public void ProductionDeviceFactory_Opens16KilohertzMonoPcm16_WithoutRecording()
    {
        using NAudio.Wave.IWaveIn productionWaveIn = MicrophoneRecorder.CreateDefaultMicrophoneWaveIn();

        Assert.Equal(16000, productionWaveIn.WaveFormat.SampleRate);
        Assert.Equal(1, productionWaveIn.WaveFormat.Channels);
        Assert.Equal(16, productionWaveIn.WaveFormat.BitsPerSample);
        Assert.Equal(50, ((NAudio.Wave.WaveInEvent)productionWaveIn).BufferMilliseconds);
        Assert.True(MicrophoneRecorder.GetDefaultInputDeviceCount() >= 0);
        Assert.False(new MicrophoneRecorder().IsRecording);
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using Clicky.Windows.Voice;

namespace Clicky.Windows.Tests;

public sealed class WhisperModelFileTests : IDisposable
{
    private readonly string scratchModelsFolderPath = Path.Combine(Path.GetTempPath(), "ClickyTests", Guid.NewGuid().ToString("N"), "models");

    private sealed class FakeModelServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task MissingModel_IsDownloadedFromWhisperCppRepository_WithProgressToHundred()
    {
        byte[] modelBytes = Enumerable.Range(0, 200_000).Select(byteIndex => (byte)byteIndex).ToArray();
        FakeModelServer fakeModelServer = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(modelBytes) });
        List<int> reportedPercents = [];

        string modelFilePath = await WhisperModelFile.EnsureDownloadedAsync("base.en", reportedPercents.Add,
            new HttpClient(fakeModelServer), scratchModelsFolderPath, CancellationToken.None);

        Assert.Equal(Path.Combine(scratchModelsFolderPath, "ggml-base.en.bin"), modelFilePath);
        Assert.Equal(modelBytes, File.ReadAllBytes(modelFilePath));
        Assert.False(File.Exists(modelFilePath + ".part"));
        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin", fakeModelServer.RequestedUris.Single().ToString());
        Assert.Equal(100, reportedPercents[^1]);
        Assert.Equal(reportedPercents.Distinct(), reportedPercents); // Each percent reported once.
    }

    [Fact]
    public async Task UnknownLength_StillDownloads_ReportingZero()
    {
        // A non-seekable stream gives the response no Content-Length.
        FakeModelServer fakeModelServer = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[100_000]))
        });
        List<int> reportedPercents = [];

        string modelFilePath = await WhisperModelFile.EnsureDownloadedAsync("tiny.en", reportedPercents.Add,
            new HttpClient(fakeModelServer), scratchModelsFolderPath, CancellationToken.None);

        Assert.Equal(100_000, new FileInfo(modelFilePath).Length);
        Assert.Equal([0], reportedPercents);
    }

    [Fact]
    public async Task CachedModel_IsReturnedWithoutDownloading()
    {
        Directory.CreateDirectory(scratchModelsFolderPath);
        string cachedModelFilePath = Path.Combine(scratchModelsFolderPath, "ggml-small.en.bin");
        File.WriteAllBytes(cachedModelFilePath, [1, 2, 3]);
        FakeModelServer fakeModelServer = new(_ => throw new InvalidOperationException("must not download"));

        string modelFilePath = await WhisperModelFile.EnsureDownloadedAsync("small.en", _ => { },
            new HttpClient(fakeModelServer), scratchModelsFolderPath, CancellationToken.None);

        Assert.Equal(cachedModelFilePath, modelFilePath);
        Assert.Empty(fakeModelServer.RequestedUris);
    }

    [Fact]
    public async Task HttpError_IsReported_AndLeavesNoModelFile()
    {
        FakeModelServer fakeModelServer = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhisperModelFile.EnsureDownloadedAsync("nope.en", _ => { }, new HttpClient(fakeModelServer), scratchModelsFolderPath, CancellationToken.None));

        Assert.Contains("HTTP 404", exception.Message);
        Assert.False(File.Exists(Path.Combine(scratchModelsFolderPath, "ggml-nope.en.bin")));
    }

    [Fact]
    public async Task InvalidModelName_IsRejectedBeforeAnyRequest()
    {
        FakeModelServer fakeModelServer = new(_ => throw new InvalidOperationException("must not download"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WhisperModelFile.EnsureDownloadedAsync(@"..\..\evil", _ => { }, new HttpClient(fakeModelServer), scratchModelsFolderPath, CancellationToken.None));
        Assert.Empty(fakeModelServer.RequestedUris);
    }

    [Fact]
    public async Task ProductionOverload_FindsTheModelAlreadyCachedInLocalAppData()
    {
        string cachedBaseEnModelPath = VoiceTestAudio.CachedBaseEnModelPath;

        Assert.Equal(cachedBaseEnModelPath, await WhisperModelFile.EnsureDownloadedAsync("base.en", _ => { }, CancellationToken.None));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ModelFileDownload.ModelsFolderPath);
    }

    public void Dispose()
    {
        string scratchRootPath = Path.GetDirectoryName(scratchModelsFolderPath)!;
        if (Directory.Exists(scratchRootPath)) Directory.Delete(scratchRootPath, recursive: true);
    }
}

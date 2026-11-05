using System.IO;
using System.Net.Http;

namespace Clicky.Windows.Voice;

/// <summary>
/// Downloads the local voice models (Whisper for speech-to-text, Kokoro for text-to-speech) into
/// %LOCALAPPDATA%\Clicky\models with a real progress percentage. Done here rather than through the
/// libraries' own downloaders: Whisper.net's returns a stream with no length, and KokoroSharp's saves to
/// the current directory (wherever Clicky happened to be started from).
/// </summary>
internal static class ModelFileDownload
{
    public static HttpClient SharedModelDownloadHttpClient { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string ModelsFolderPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky", "models");

    /// <summary>Downloads <paramref name="modelDownloadUrl"/> to <paramref name="modelFilePath"/>, reporting 0..100.</summary>
    /// <param name="modelDescription">Names the model in the error message, e.g. <c>the Whisper model "base.en"</c>.</param>
    public static async Task DownloadAsync(string modelDownloadUrl, string modelFilePath, string modelDescription,
        Action<int> reportDownloadPercent, HttpClient modelDownloadHttpClient, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(modelFilePath)!);
        // Written to a .part file and renamed at the end, so a cancelled or failed download never
        // leaves a truncated model that would be loaded on the next start.
        string partialModelFilePath = modelFilePath + ".part";

        using HttpResponseMessage downloadResponse = await modelDownloadHttpClient.GetAsync(modelDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Couldn't download {modelDescription} (HTTP {(int)downloadResponse.StatusCode}).");
        }

        long? totalByteCount = downloadResponse.Content.Headers.ContentLength;
        await using (Stream downloadStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken))
        await using (FileStream partialModelFileStream = File.Create(partialModelFilePath))
        {
            byte[] copyBuffer = new byte[1 << 16];
            long downloadedByteCount = 0;
            int lastReportedPercent = -1;
            int readByteCount;
            while ((readByteCount = await downloadStream.ReadAsync(copyBuffer, cancellationToken)) > 0)
            {
                await partialModelFileStream.WriteAsync(copyBuffer.AsMemory(0, readByteCount), cancellationToken);
                downloadedByteCount += readByteCount;
                int downloadPercent = totalByteCount > 0 ? (int)(downloadedByteCount * 100 / totalByteCount.Value) : 0;
                if (downloadPercent != lastReportedPercent)
                {
                    lastReportedPercent = downloadPercent;
                    reportDownloadPercent(downloadPercent);
                }
            }
        }

        File.Move(partialModelFilePath, modelFilePath, overwrite: true);
    }
}

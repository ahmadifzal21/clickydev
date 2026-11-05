using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Clicky.Windows.Voice;

/// <summary>
/// Locates and downloads a Whisper GGML model ("base.en" -> ggml-base.en.bin) from the
/// whisper.cpp Hugging Face repository into %LOCALAPPDATA%\Clicky\models. Downloaded here rather
/// than through Whisper.net's downloader because that one returns a stream with no length, and
/// the first download (~140 MB for base.en) needs a progress percentage.
/// </summary>
internal static class WhisperModelFile
{
    private const string ModelDownloadBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static readonly HttpClient sharedModelDownloadHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string ModelsFolderPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clicky", "models");

    /// <summary>
    /// The model name comes from the editable config and ends up in a URL and a file path, so only
    /// whisper.cpp-style names (letters, digits, dots, dashes, e.g. "small.en-q5_1") are accepted.
    /// </summary>
    public static bool IsValidModelName(string whisperModelName) =>
        Regex.IsMatch(whisperModelName, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") && !whisperModelName.Contains("..");

    public static string GetModelFilePath(string whisperModelName) =>
        Path.Combine(ModelsFolderPath, $"ggml-{whisperModelName}.bin");

    /// <summary>Returns the cached model path, downloading it first if needed. Reports 0..100.</summary>
    public static async Task<string> EnsureDownloadedAsync(string whisperModelName, Action<int> reportDownloadPercent,
        CancellationToken cancellationToken)
    {
        if (!IsValidModelName(whisperModelName))
        {
            throw new ArgumentException($"\"{whisperModelName}\" isn't a Whisper model name (try \"base.en\" or \"small.en\").");
        }

        string modelFilePath = GetModelFilePath(whisperModelName);
        if (File.Exists(modelFilePath))
        {
            return modelFilePath;
        }

        Directory.CreateDirectory(ModelsFolderPath);
        // Written to a .part file and renamed at the end, so a cancelled or failed download never
        // leaves a truncated model that would be loaded on the next start.
        string partialModelFilePath = modelFilePath + ".part";
        string modelDownloadUrl = ModelDownloadBaseUrl + $"ggml-{whisperModelName}.bin";

        using HttpResponseMessage downloadResponse = await sharedModelDownloadHttpClient.GetAsync(modelDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Couldn't download the Whisper model \"{whisperModelName}\" (HTTP {(int)downloadResponse.StatusCode}).");
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
        return modelFilePath;
    }
}

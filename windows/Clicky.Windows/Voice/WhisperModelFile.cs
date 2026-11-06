using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Clicky.Windows.Voice;

/// <summary>
/// Locates and downloads a Whisper GGML model ("base.en" -> ggml-base.en.bin) from the
/// whisper.cpp Hugging Face repository into %LOCALAPPDATA%\Clicky\models (see <see cref="ModelFileDownload"/>).
/// </summary>
internal static class WhisperModelFile
{
    private const string ModelDownloadBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    /// <summary>
    /// The model name comes from the editable config and ends up in a URL and a file path, so only
    /// whisper.cpp-style names (letters, digits, dots, dashes, e.g. "small.en-q5_1") are accepted.
    /// </summary>
    public static bool IsValidModelName(string whisperModelName) =>
        Regex.IsMatch(whisperModelName, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") && !whisperModelName.Contains("..");

    /// <summary>Returns the cached model path, downloading it first if needed. Reports 0..100.</summary>
    public static Task<string> EnsureDownloadedAsync(string whisperModelName, Action<int> reportDownloadPercent,
        CancellationToken cancellationToken) =>
        EnsureDownloadedAsync(whisperModelName, reportDownloadPercent, ModelFileDownload.SharedModelDownloadHttpClient, ModelFileDownload.ModelsFolderPath, cancellationToken);

    /// <summary>Same, with the HTTP client and folder passed in so tests can serve a model without the network.</summary>
    internal static async Task<string> EnsureDownloadedAsync(string whisperModelName, Action<int> reportDownloadPercent,
        HttpClient modelDownloadHttpClient, string modelsFolderPath, CancellationToken cancellationToken)
    {
        if (!IsValidModelName(whisperModelName))
        {
            throw new ArgumentException($"\"{whisperModelName}\" isn't a Whisper model name (try \"base.en\" or \"small.en\").");
        }

        string modelFilePath = Path.Combine(modelsFolderPath, $"ggml-{whisperModelName}.bin");
        if (File.Exists(modelFilePath))
        {
            return modelFilePath;
        }

        string modelDownloadUrl = ModelDownloadBaseUrl + $"ggml-{whisperModelName}.bin";
        await ModelFileDownload.DownloadAsync(modelDownloadUrl, modelFilePath, $"the Whisper model \"{whisperModelName}\"",
            reportDownloadPercent, modelDownloadHttpClient, cancellationToken);
        return modelFilePath;
    }
}

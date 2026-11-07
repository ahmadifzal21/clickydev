namespace Clicky.Windows.Configuration;

/// <summary>
/// The AI backends Clicky can send a request to. Serialized in clicky.config.json as
/// kebab-case: "claude-code-cli", "codex-cli", "cloudflare-worker".
/// </summary>
public enum ChatBackendKind
{
    /// Shells out to `claude -p` using the user's existing Claude Code login.
    ClaudeCodeCli,

    /// Shells out to `codex exec -i screenshot.jpg` using the user's existing Codex login.
    CodexCli,

    /// The existing Cloudflare Worker /chat route (worker/src/index.ts), which holds an API key.
    CloudflareWorker
}

/// <summary>
/// The kinds of request Clicky makes. Each one is routed to a backend independently.
/// </summary>
public enum CompanionJobKind
{
    /// The push-to-talk voice reply: spoken text plus an inline [POINT] tag.
    Answer,

    /// Point-only requests ("find something on screen and point at it").
    Pointing
}

/// <summary>Speech-to-text engines. Serialized as "foundry-local" / "whisper-local" / "assembly-ai".</summary>
public enum SpeechToTextProviderKind
{
    WhisperLocal,
    AssemblyAi,

    /// <summary>
    /// Foundry Local's live model (Nemotron Speech Streaming 0.6B, on the CPU), transcribing while the shortcut is held, with
    /// local Whisper as the fallback (E10 T10.8).
    /// </summary>
    FoundryLocal
}

/// <summary>Text-to-speech engines. Serialized as "kokoro-local" / "eleven-labs".</summary>
public enum TextToSpeechProviderKind
{
    /// Kokoro-82M in-process (KokoroSharp, ONNX on the CPU).
    KokoroLocal,

    ElevenLabs
}

/// <summary>
/// Root of clicky.config.json. Every property has a default, so a partial or
/// empty file still produces a complete configuration.
/// </summary>
public sealed class ClickyConfiguration
{
    public JobBackendRouting JobRouting { get; set; } = new();

    public ClaudeCodeCliBackendSettings ClaudeCodeCli { get; set; } = new();

    public CodexCliBackendSettings CodexCli { get; set; } = new();

    public CloudflareWorkerBackendSettings CloudflareWorker { get; set; } = new();

    public VoiceSettings Voice { get; set; } = new();

    /// <summary>
    /// Push-to-talk shortcut held down to talk, e.g. "ctrl+alt" (the Windows equivalent of the
    /// Mac's control+option). Modifier-only combinations are allowed.
    /// </summary>
    public string PushToTalkShortcut { get; set; } = Input.PushToTalkShortcut.DefaultShortcutText;

    /// <summary>
    /// Mirrors the Mac "Show Clicky" toggle. When false the cursor only appears while
    /// a push-to-talk interaction is in progress (transient cursor mode).
    /// </summary>
    public bool IsClickyCursorAlwaysVisible { get; set; } = true;

    /// <summary>
    /// "Start with Windows" (a per-user Run registry value). On start the app sets this from the registry, which is the
    /// truth; changing it here (panel, tray or the file) writes or removes the registry value.
    /// </summary>
    public bool StartWithWindows { get; set; }
}

/// <summary>Which backend handles each job, plus the backend to try if that one fails.</summary>
public sealed class JobBackendRouting
{
    public ChatBackendKind Answer { get; set; } = ChatBackendKind.ClaudeCodeCli;

    public ChatBackendKind Pointing { get; set; } = ChatBackendKind.ClaudeCodeCli;

    /// <summary>
    /// Tried once when the routed backend throws or times out. Null disables fallback.
    /// Ignored when it's the same backend that just failed.
    /// </summary>
    public ChatBackendKind? Fallback { get; set; } = ChatBackendKind.CodexCli;

    public ChatBackendKind GetBackendForJob(CompanionJobKind companionJobKind)
    {
        return companionJobKind switch
        {
            CompanionJobKind.Answer => Answer,
            CompanionJobKind.Pointing => Pointing,
            _ => throw new ArgumentOutOfRangeException(nameof(companionJobKind), companionJobKind, "Unknown job kind")
        };
    }

    public void SetBackendForJob(CompanionJobKind companionJobKind, ChatBackendKind chatBackendKind)
    {
        switch (companionJobKind)
        {
            case CompanionJobKind.Answer:
                Answer = chatBackendKind;
                break;
            case CompanionJobKind.Pointing:
                Pointing = chatBackendKind;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(companionJobKind), companionJobKind, "Unknown job kind");
        }
    }
}

public sealed class ClaudeCodeCliBackendSettings
{
    /// <summary>Path to the claude executable. A bare name is resolved on PATH.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>Passed as --model. An alias like "sonnet" or "opus" tracks the latest model.</summary>
    public string? Model { get; set; } = "sonnet";

    /// <summary>Passed as --effort (low, medium, high, xhigh, max). Low: a short spoken answer with marks needs little thinking.</summary>
    public string? Effort { get; set; } = "low";

    public int ResponseTimeoutSeconds { get; set; } = 120;
}

public sealed class CodexCliBackendSettings
{
    /// <summary>Path to the codex executable. A bare name is resolved on PATH.</summary>
    public string ExecutablePath { get; set; } = "codex";

    /// <summary>Passed as -m. Null uses whatever ~/.codex/config.toml defaults to.</summary>
    public string? Model { get; set; }

    /// <summary>Passed as -c model_reasoning_effort=... Low cuts most of Codex's thinking time before the first word.</summary>
    public string? ReasoningEffort { get; set; } = "low";

    public int ResponseTimeoutSeconds { get; set; } = 120;
}

public sealed class CloudflareWorkerBackendSettings
{
    /// <summary>Base URL of a deployed worker/, e.g. "https://clicky-proxy.you.workers.dev". Empty means not set up.</summary>
    public string WorkerBaseUrl { get; set; } = "";

    public string Model { get; set; } = "claude-sonnet-5-5";

    public int ResponseTimeoutSeconds { get; set; } = 120;
}

public sealed class VoiceSettings
{
    public SpeechToTextProviderKind SpeechToText { get; set; } = SpeechToTextProviderKind.FoundryLocal;

    /// <summary>Whisper GGML model name, downloaded on first use (for example "base.en", "small.en"); also Foundry's fallback.</summary>
    public string WhisperModel { get; set; } = "base.en";

    public TextToSpeechProviderKind TextToSpeech { get; set; } = TextToSpeechProviderKind.KokoroLocal;

    /// <summary>Kokoro voice name, e.g. "af_heart", "am_michael", "bf_emma" (the .npy files in the voices folder).</summary>
    public string KokoroVoice { get; set; } = "af_heart";

    /// <summary>Only needed when SpeechToText is "assembly-ai".</summary>
    public string AssemblyAiApiKey { get; set; } = "";

    /// <summary>Only needed when TextToSpeech is "eleven-labs".</summary>
    public string ElevenLabsApiKey { get; set; } = "";

    public string ElevenLabsVoiceId { get; set; } = "";
}

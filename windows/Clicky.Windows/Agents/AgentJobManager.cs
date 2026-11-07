using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Clicky.Windows.AI;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Agents;

internal enum AgentJobStatus
{
    Working,
    NeedsYou,
    Done,
    Failed,
    Stopped
}

/// <summary>One background job: a full CLI agent working in its own folder.</summary>
internal sealed class AgentJob(string id, string title, string task, string folder)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Task { get; } = task;
    public string Folder { get; } = folder;
    public DateTime StartedAt { get; } = DateTime.Now;
    public AgentJobStatus Status { get; set; } = AgentJobStatus.Working;
    public DateTime? FinishedAt { get; set; }

    /// <summary>The agent's latest words: progress while working, its summary or question at the end.</summary>
    public string LastMessage { get; set; } = "";

    /// <summary>Claude Code's session id, to continue the job with a follow-up message.</summary>
    public string? SessionId { get; set; }

    internal Process? Process { get; set; }
}

/// <summary>
/// Background jobs (E11 T11.27): <c>agent("title", "task")</c> starts a full Claude Code (or Codex) agent that works for minutes
/// in its own folder under "Clicky Jobs" while the user keeps going. The safety boundary is the folder and the permissions:
/// Claude runs in print mode with acceptEdits, so it can create and edit files there (and search the web) but nothing can
/// approve a shell command; Codex runs with a workspace-write sandbox limited to the folder. The prompt makes the model ask
/// before starting one. Each turn's context lists recent jobs; when one finishes, the user hears a line and gets a result card.
/// <c>agent_send(id, "…")</c> continues a job (Claude resumes its session; Codex starts a new run in the same folder with the
/// earlier task), <c>agent_stop(id)</c> kills it. Quitting Clicky stops every job.
/// </summary>
internal sealed partial class AgentJobManager(AgentSettings settings, Func<ClaudeCodeCliBackendSettings> claudeSettings,
    Func<CodexCliBackendSettings> codexSettings, Action<AgentJob> onJobFinished) : IDisposable
{
    private static readonly TimeSpan RecentJobWindow = TimeSpan.FromMinutes(30);
    private readonly object jobsGate = new();
    private readonly List<AgentJob> jobs = [];
    private int nextJobNumber = 1;

    public IReadOnlyList<AgentJob> Jobs
    {
        get { lock (jobsGate) return [.. jobs]; }
    }

    public event Action? JobsChanged;

    public string JobsRootFolder => Environment.ExpandEnvironmentVariables(settings.JobsFolder);

    public AgentJob Start(string title, string task)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Background job" : title.Trim();
        AgentJob job;
        lock (jobsGate)
        {
            string id = $"j{nextJobNumber++}";
            string folder = Path.Combine(JobsRootFolder, $"{DateTime.Now:yyyyMMdd-HHmm}-{Slug(title)}");
            Directory.CreateDirectory(folder);
            job = new AgentJob(id, title, task.Trim(), folder);
            jobs.Add(job);
        }
        Trace.WriteLine($"Background job {job.Id} \"{job.Title}\" started in {job.Folder}.");
        Run(job, job.Task, resume: false);
        return job;
    }

    /// <summary>The user's answer or more instructions for a job; false when there is no such job.</summary>
    public bool Send(string jobId, string message)
    {
        AgentJob? job = Find(jobId);
        if (job is null || message.Trim().Length == 0) return false;
        Kill(job);
        job.Status = AgentJobStatus.Working;
        job.FinishedAt = null;
        Run(job, message.Trim(), resume: true);
        return true;
    }

    public bool Stop(string jobId)
    {
        AgentJob? job = Find(jobId);
        if (job is null || job.Status is not (AgentJobStatus.Working or AgentJobStatus.NeedsYou)) return false;
        Kill(job);
        Finish(job, AgentJobStatus.Stopped, job.LastMessage, announce: false);
        return true;
    }

    /// <summary>The [jobs] context line: jobs still going, waiting, or finished in the last 30 minutes (id, title, status).</summary>
    public string? ContextLine()
    {
        AgentJob[] recent = [.. Jobs.Where(job => job.Status is AgentJobStatus.Working or AgentJobStatus.NeedsYou
            || DateTime.Now - (job.FinishedAt ?? DateTime.Now) < RecentJobWindow)];
        if (recent.Length == 0) return null;
        return "[jobs] " + string.Join("; ", recent.Select(job =>
        {
            string status = job.Status switch
            {
                AgentJobStatus.Working => $"working ({(DateTime.Now - job.StartedAt).TotalMinutes:0} min)",
                AgentJobStatus.NeedsYou => "needs you",
                AgentJobStatus.Done => "done",
                AgentJobStatus.Failed => "failed",
                _ => "stopped"
            };
            return $"{job.Id} \"{job.Title}\" {status}"; // Not the agent's words: they're untrusted text; the user sees them on a card.
        }));
    }

    private AgentJob? Find(string jobId)
    {
        lock (jobsGate) return jobs.FirstOrDefault(job => job.Id.Equals(jobId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void Run(AgentJob job, string message, bool resume)
    {
        bool useCodex = settings.Backend == ChatBackendKind.CodexCli;
        ProcessStartInfo startInfo = useCodex
            ? BuildCodexStartInfo(codexSettings(), job, resume ? $"Earlier task in this folder: {job.Task}\n\nNow: {message}" : message)
            : BuildClaudeStartInfo(claudeSettings(), settings, job, message, resume ? job.SessionId : null);
        startInfo.FileName = CliChatProcess.ResolveExecutablePath(startInfo.FileName, Environment.GetEnvironmentVariable("PATH"));
        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        StringBuilder errors = new();
        string? resultText = null;
        bool isError = false;
        process.OutputDataReceived += (_, line) =>
        {
            if (string.IsNullOrWhiteSpace(line.Data)) return;
            ReadProgress(job, line.Data, ref resultText, ref isError);
        };
        process.ErrorDataReceived += (_, line) => { if (line.Data is { } data) lock (errors) if (errors.Length < 4000) errors.AppendLine(data); };
        process.Exited += (_, _) =>
        {
            if (job.Process != process) return; // Replaced by a follow-up or stopped.
            process.WaitForExit(); // Flush the output events.
            string summary = resultText ?? job.LastMessage;
            AgentJobStatus status = process.ExitCode != 0 || isError
                ? AgentJobStatus.Failed
                : summary.TrimEnd().EndsWith('?') ? AgentJobStatus.NeedsYou : AgentJobStatus.Done;
            if (status == AgentJobStatus.Failed && summary.Length == 0) lock (errors) summary = errors.ToString().Trim();
            Finish(job, status, summary, announce: true);
        };
        try
        {
            process.Start();
            job.Process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Finish(job, AgentJobStatus.Failed, $"Couldn't start the agent: {exception.Message}", announce: true);
        }
        JobsChanged?.Invoke();
    }

    /// <summary>Claude: stream-json lines (init with the session id, assistant text, the final result). Codex: agent messages.</summary>
    internal static void ReadProgress(AgentJob job, string line, ref string? resultText, ref bool isError)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string? type = root.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
            if (root.TryGetProperty("session_id", out JsonElement session) && session.GetString() is { Length: > 0 } sessionId) job.SessionId = sessionId;
            if (type == "assistant" && root.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.Array)
            {
                string text = string.Concat(content.EnumerateArray()
                    .Where(block => block.TryGetProperty("type", out JsonElement blockType) && blockType.GetString() == "text")
                    .Select(block => block.GetProperty("text").GetString()));
                if (text.Trim().Length > 0) job.LastMessage = text.Trim();
            }
            else if (type == "result")
            {
                resultText = root.TryGetProperty("result", out JsonElement result) ? result.GetString() : null;
                isError = root.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True;
            }
            else if (CodexCliProtocol.TryGetAgentMessageText(line) is { Length: > 0 } codexMessage)
            {
                job.LastMessage = codexMessage.Trim();
            }
        }
        catch (JsonException)
        {
        }
    }

    private void Finish(AgentJob job, AgentJobStatus status, string summary, bool announce)
    {
        job.Status = status;
        job.FinishedAt = DateTime.Now;
        if (summary.Length > 0) job.LastMessage = summary.Trim();
        Trace.WriteLine($"Background job {job.Id} \"{job.Title}\" {status}.");
        JobsChanged?.Invoke();
        if (announce) onJobFinished(job);
    }

    internal const string JobAllowedTools = "Read(./**),Edit(./**),Write(./**),WebSearch";
    // Reads outside the folder aren't pre-approved, and print mode denies anything that isn't (probed). Glob and Grep, which
    // take no path rule here, are denied outright.
    internal const string JobDeniedTools = "Bash,PowerShell,WebFetch,Glob,Grep";

    internal static ProcessStartInfo BuildClaudeStartInfo(ClaudeCodeCliBackendSettings claude, AgentSettings agentSettings, AgentJob job,
        string message, string? resumeSessionId)
    {
        ProcessStartInfo startInfo = NewStartInfo(claude.ExecutablePath, job.Folder);
        // The task goes as the prompt argument after "--": a new line in it survives because ArgumentList quotes it, and cmd's
        // shim is not involved for claude.exe. Print mode can't ask permission, so commands are refused; edits in the folder aren't.
        // File tools are allowed only under the job folder (the working directory); the shell is denied outright.
        string[] arguments =
        [
            "-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "acceptEdits",
            "--allowedTools", JobAllowedTools, "--disallowedTools", JobDeniedTools,
            "--safe-mode", "--strict-mcp-config", "--disable-slash-commands"
        ];
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        string? model = agentSettings.Model ?? claude.Model;
        if (!string.IsNullOrWhiteSpace(model)) { startInfo.ArgumentList.Add("--model"); startInfo.ArgumentList.Add(model); }
        if (resumeSessionId is not null) { startInfo.ArgumentList.Add("--resume"); startInfo.ArgumentList.Add(resumeSessionId); }
        startInfo.ArgumentList.Add("--append-system-prompt");
        startInfo.ArgumentList.Add($"You are a background helper started by Clicky, a desktop companion. Work only inside {job.Folder}. " +
            "When you finish, reply with a short summary of what you made and where. If you need a decision from the user, end with one clear question.");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(message);
        return startInfo;
    }

    internal static ProcessStartInfo BuildCodexStartInfo(CodexCliBackendSettings codex, AgentJob job, string message)
    {
        ProcessStartInfo startInfo = NewStartInfo(codex.ExecutablePath, job.Folder);
        string[] arguments = ["exec", "--skip-git-repo-check", "--sandbox", "workspace-write", "-C", job.Folder, "--color", "never", "--json"];
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(codex.Model)) { startInfo.ArgumentList.Add("--model"); startInfo.ArgumentList.Add(codex.Model); }
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(message);
        return startInfo;
    }

    private static ProcessStartInfo NewStartInfo(string executablePath, string folder) => new(executablePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = folder,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    internal static string Slug(string title)
    {
        string slug = SlugRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "job" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();

    private static void Kill(AgentJob job)
    {
        Process? process = job.Process;
        job.Process = null;
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"Couldn't stop background job {job.Id}: {exception.Message}");
        }
    }

    public void Dispose()
    {
        foreach (AgentJob job in Jobs) Kill(job);
    }
}

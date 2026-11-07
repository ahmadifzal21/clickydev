using System.IO;
using Clicky.Windows.Agents;
using Clicky.Windows.Configuration;

namespace Clicky.Windows.Tests;

/// <summary>E11 T11.27: background jobs: how the agent is started (sandbox), how its progress is read, and its lifecycle.</summary>
public sealed class AgentJobTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "clicky-jobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { Thread.Sleep(200); }
        }
    }

    [Fact]
    public void ClaudeJob_CanEditFilesInItsFolderAndSearch_ButHasNoShell()
    {
        AgentJob job = new("j1", "Site", "make a site", @"C:\Jobs\site");
        var startInfo = AgentJobManager.BuildClaudeStartInfo(new ClaudeCodeCliBackendSettings { Model = "sonnet" }, new AgentSettings(), job,
            "make a site\nwith a menu", resumeSessionId: null);

        List<string> arguments = [.. startInfo.ArgumentList];
        Assert.Equal(@"C:\Jobs\site", startInfo.WorkingDirectory);
        Assert.Equal("acceptEdits", arguments[arguments.IndexOf("--permission-mode") + 1]);
        string allowed = arguments[arguments.IndexOf("--allowedTools") + 1];
        Assert.DoesNotContain("Bash", allowed);
        Assert.Contains("Write", allowed);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.Equal(["--", "make a site\nwith a menu"], arguments.TakeLast(2));
        Assert.DoesNotContain("--resume", arguments);

        var resumed = AgentJobManager.BuildClaudeStartInfo(new ClaudeCodeCliBackendSettings(), new AgentSettings(), job, "more", "session-42");
        Assert.Equal("session-42", resumed.ArgumentList[resumed.ArgumentList.IndexOf("--resume") + 1]);
    }

    [Fact]
    public void CodexJob_IsSandboxedToItsFolder()
    {
        var startInfo = AgentJobManager.BuildCodexStartInfo(new CodexCliBackendSettings(), new AgentJob("j1", "x", "t", @"C:\Jobs\x"), "task");

        List<string> arguments = [.. startInfo.ArgumentList];
        Assert.Equal("workspace-write", arguments[arguments.IndexOf("--sandbox") + 1]);
        Assert.Equal(@"C:\Jobs\x", arguments[arguments.IndexOf("-C") + 1]);
        Assert.DoesNotContain("danger-full-access", arguments);
    }

    [Theory]
    [InlineData("Bakery website!", "bakery-website")]
    [InlineData("   ", "job")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    public void FolderNames_AreSafeSlugs(string title, string slug) => Assert.Equal(slug, AgentJobManager.Slug(title));

    [Fact]
    public void Progress_ReadsTheSessionTheLatestWordsAndTheResult()
    {
        AgentJob job = new("j1", "x", "t", root);
        string? result = null;
        bool isError = false;
        AgentJobManager.ReadProgress(job, """{"type":"system","subtype":"init","session_id":"abc"}""", ref result, ref isError);
        AgentJobManager.ReadProgress(job, """{"type":"assistant","message":{"content":[{"type":"text","text":"Writing index.html"}]}}""", ref result, ref isError);
        Assert.Equal("abc", job.SessionId);
        Assert.Equal("Writing index.html", job.LastMessage);

        AgentJobManager.ReadProgress(job, """{"type":"result","subtype":"success","is_error":false,"result":"Made index.html."}""", ref result, ref isError);
        Assert.Equal("Made index.html.", result);
        Assert.False(isError);
    }

    private string FakeClaude(string finalText, int exitCode = 0)
    {
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "fake-claude.ps1");
        File.WriteAllText(script, $$"""
            [Console]::Out.WriteLine('{"type":"system","subtype":"init","session_id":"s1"}')
            [Console]::Out.WriteLine('{"type":"result","subtype":"success","is_error":false,"result":"{{finalText}}"}')
            exit {{exitCode}}
            """);
        string shim = Path.Combine(root, "claude.cmd");
        File.WriteAllText(shim, $"@powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" %*\r\n@exit /b %errorlevel%\r\n");
        return shim;
    }

    private (AgentJobManager Manager, TaskCompletionSource<AgentJob> Finished) Manager(string fakeClaude)
    {
        TaskCompletionSource<AgentJob> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentJobManager manager = new(new AgentSettings { JobsFolder = Path.Combine(root, "jobs") },
            () => new ClaudeCodeCliBackendSettings { ExecutablePath = fakeClaude, Model = null }, () => new CodexCliBackendSettings(),
            job => finished.TrySetResult(job));
        return (manager, finished);
    }

    [Theory]
    [InlineData("Made the site in index.html.", 0, "Done")]
    [InlineData("Which colours do you want?", 0, "NeedsYou")]
    [InlineData("crashed", 3, "Failed")]
    public void AJobRunsInItsOwnFolder_AndEndsDoneWaitingOrFailed(string finalText, int exitCode, string expectedStatus)
    {
        (AgentJobManager manager, TaskCompletionSource<AgentJob> finished) = Manager(FakeClaude(finalText, exitCode));
        using (manager)
        {
            AgentJob job = manager.Start("Bakery site", "Make a one page site.");
            Assert.True(Directory.Exists(job.Folder));
            Assert.StartsWith(Path.Combine(root, "jobs"), job.Folder);

            Assert.True(finished.Task.Wait(TimeSpan.FromSeconds(20)), "the job never finished");
            AgentJobStatus expected = Enum.Parse<AgentJobStatus>(expectedStatus);
            Assert.Equal(expected, job.Status);
            Assert.Equal("s1", job.SessionId);
            Assert.Contains("j1 \"Bakery site\"", manager.ContextLine());
            if (expected != AgentJobStatus.Failed) Assert.Equal(finalText, job.LastMessage);
        }
    }
}

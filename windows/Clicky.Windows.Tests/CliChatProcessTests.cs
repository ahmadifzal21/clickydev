using System.Diagnostics;
using System.Text;
using Clicky.Windows.AI;

namespace Clicky.Windows.Tests;

public class CliChatProcessTests
{
    [Fact]
    public async Task RedirectedPipes_AreDrainedTogether_AndPreserveUtf8Input()
    {
        var startInfo = CreatePowerShellStartInfo("""
            [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::Error.Write(('e' * 131072))
            [Console]::Out.Write(('o' * 131072))
            [Console]::Out.Write([Console]::In.ReadToEnd())
            exit 7
            """);
        string input = "café 日本語\n" + new string('i', 131072);

        var result = await CliChatProcess.RunAsync(startInfo, input, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(new string('e', 131072), result.StandardError);
        Assert.Equal(new string('o', 131072) + input, result.StandardOutput);
    }

    [Fact]
    public async Task EarlyExit_KeepsDiagnosticEvenWhenStdinIsNotConsumed()
    {
        var startInfo = CreatePowerShellStartInfo("[Console]::Error.Write('Not logged in'); exit 1");

        var result = await CliChatProcess.RunAsync(startInfo, new string('i', 4194304), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Not logged in", result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrTimeout_KillsParentAndChild(bool useTimeout)
    {
        string readyPath = Path.Combine(Path.GetTempPath(), $"clicky-process-test-{Guid.NewGuid():N}.txt");
        string childCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 60"));
        string script = $$"""
            $childProcess = Start-Process -FilePath '{{PowerShellPath.Replace("'", "''")}}' -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', '{{childCommand}}' -WindowStyle Hidden -PassThru
            $processReadyPath = '{{readyPath.Replace("'", "''")}}'
            [IO.File]::WriteAllText("$processReadyPath.tmp", "$PID,$($childProcess.Id)")
            [IO.File]::Move("$processReadyPath.tmp", $processReadyPath)
            Start-Sleep -Seconds 60
            """;
        int[] processIds = [];
        using var cancellation = new CancellationTokenSource();
        Task<(string StandardOutput, string StandardError, int ExitCode)> runningProcess = CliChatProcess.RunAsync(
            CreatePowerShellStartInfo(script), new string('i', 4194304), TimeSpan.FromSeconds(useTimeout ? 6 : 20), cancellation.Token);
        try
        {
            var startup = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && !runningProcess.IsCompleted && startup.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);
            Assert.True(File.Exists(readyPath), "The fake CLI did not start its child.");
            processIds = (await File.ReadAllTextAsync(readyPath)).Split(',').Select(int.Parse).ToArray();
            Assert.Equal(2, processIds.Length);
            if (useTimeout)
                await Assert.ThrowsAsync<TimeoutException>(() => runningProcess);
            else
            {
                cancellation.Cancel();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runningProcess);
                Assert.Equal(cancellation.Token, exception.CancellationToken);
            }
            foreach (int processId in processIds)
            {
                using Process? process = FindProcess(processId);
                if (process is not null)
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await runningProcess; } catch { }
            foreach (int processId in processIds)
            {
                using Process? process = FindProcess(processId);
                if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true);
            }
            File.Delete(readyPath);
            File.Delete(readyPath + ".tmp");
        }
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotStartAnExecutable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliChatProcess.RunAsync(
            new ProcessStartInfo("does-not-exist.exe"), "", TimeSpan.FromSeconds(1), cancellation.Token));
    }

    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    [Fact]
    public void ResolveExecutablePath_FindsNpmCmdShimOnPathButPrefersExe()
    {
        string shimOnlyDirectory = Directory.CreateTempSubdirectory("clicky-path-").FullName;
        string exeDirectory = Directory.CreateTempSubdirectory("clicky-path-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(shimOnlyDirectory, "codex"), "");      // npm's extensionless sh shim: not runnable
            File.WriteAllText(Path.Combine(shimOnlyDirectory, "codex.cmd"), "");
            File.WriteAllText(Path.Combine(exeDirectory, "claude.cmd"), "");
            File.WriteAllText(Path.Combine(exeDirectory, "claude.exe"), "");
            string searchPath = $"{shimOnlyDirectory};\"{exeDirectory}\"";

            Assert.Equal(Path.Combine(shimOnlyDirectory, "codex.cmd"), CliChatProcess.ResolveExecutablePath("codex", searchPath));
            Assert.Equal(Path.Combine(exeDirectory, "claude.exe"), CliChatProcess.ResolveExecutablePath("claude", searchPath));
            Assert.Equal("missing", CliChatProcess.ResolveExecutablePath("missing", searchPath));
            Assert.Equal("codex.exe", CliChatProcess.ResolveExecutablePath("codex.exe", searchPath));
            Assert.Equal(@"C:\tools\codex", CliChatProcess.ResolveExecutablePath(@"C:\tools\codex", searchPath));
        }
        finally
        {
            Directory.Delete(shimOnlyDirectory, recursive: true);
            Directory.Delete(exeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NpmCmdShim_ReceivesClaudeArgumentsIntact()
    {
        string shimDirectory = Directory.CreateTempSubdirectory("clicky shim ").FullName;
        try
        {
            // Like npm's shim, it forwards %* untouched; echo shows exactly what cmd.exe parsed.
            string shimPath = Path.Combine(shimDirectory, "claude.cmd");
            File.WriteAllText(shimPath, "@echo %*\r\n");
            string promptFilePath = Path.Combine(shimDirectory, "clicky-claude-1.txt");
            var startInfo = ClaudeCodeCliProtocol.BuildStartInfo(
                new Clicky.Windows.Configuration.ClaudeCodeCliBackendSettings { ExecutablePath = shimPath }, promptFilePath);

            var result = await CliChatProcess.RunAsync(startInfo, "", TimeSpan.FromSeconds(15), CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"--system-prompt-file \"{promptFilePath}\" --tools \"\" --safe-mode", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(shimDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartedProcess_TakesItsRequestLater_AndStreamsTheLinesAfterIt()
    {
        var startInfo = CreatePowerShellStartInfo("""
            [Console]::Out.WriteLine('ready')
            $request = [Console]::In.ReadToEnd()
            [Console]::Out.WriteLine("got $request")
            """);
        using CliChatProcess chatProcess = CliChatProcess.Start(startInfo);
        await Task.Delay(1500); // The user is still talking.
        List<string> streamedLines = [];

        var result = await chatProcess.SendAsync("question", TimeSpan.FromSeconds(15), CancellationToken.None, streamedLines.Add);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready\ngot question\n", result.StandardOutput);
        Assert.Contains("got question", streamedLines);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chatProcess.SendAsync("again", TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public void UnusedStartedProcess_IsKilledOnDispose()
    {
        CliChatProcess chatProcess = CliChatProcess.Start(CreatePowerShellStartInfo("Start-Sleep -Seconds 60"));
        Assert.False(chatProcess.HasExited);

        var stopwatch = Stopwatch.StartNew();
        chatProcess.Dispose();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PrestartSlot_HandsOutAMatchingProcess_AndKillsMismatchedReplacedAndIdleOnes()
    {
        static (PrestartedCliProcess Prestarted, List<string> CleanedUp) Prestart(string key)
        {
            List<string> cleanedUp = [];
            return (new PrestartedCliProcess(CliChatProcess.Start(CreatePowerShellStartInfo("Start-Sleep -Seconds 60")), key, key,
                () => cleanedUp.Add(key)), cleanedUp);
        }

        using PrestartedCliProcessSlot slot = new(idleTimeout: TimeSpan.FromSeconds(2));
        var (first, firstCleanedUp) = Prestart("voice prompt");
        slot.Put(first);
        using (PrestartedCliProcess? taken = slot.Take(key => key.Equals("voice prompt")))
        {
            Assert.Same(first, taken);
            Assert.Empty(firstCleanedUp); // Taken, not killed: the request uses it.
        }
        Assert.Equal(["voice prompt"], firstCleanedUp);

        var (mismatched, mismatchedCleanedUp) = Prestart("voice prompt");
        slot.Put(mismatched);
        Assert.Null(slot.Take(key => key.Equals("pointing prompt")));
        Assert.Equal(["voice prompt"], mismatchedCleanedUp);

        var (replaced, replacedCleanedUp) = Prestart("old press");
        slot.Put(replaced);
        var (newer, newerCleanedUp) = Prestart("new press");
        slot.Put(newer);
        Assert.Equal(["old press"], replacedCleanedUp);

        await Task.Delay(TimeSpan.FromSeconds(4)); // Nobody asked: the idle timeout kills it.
        Assert.Equal(["new press"], newerCleanedUp);
        Assert.Null(slot.Take(_ => true));
    }

    private static Process? FindProcess(int processId)
    {
        try { return Process.GetProcessById(processId); }
        catch (ArgumentException) { return null; }
    }
}

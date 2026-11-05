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

    private static Process? FindProcess(int processId)
    {
        try { return Process.GetProcessById(processId); }
        catch (ArgumentException) { return null; }
    }
}

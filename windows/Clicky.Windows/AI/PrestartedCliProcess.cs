namespace Clicky.Windows.AI;

/// <summary>
/// A CLI process started before its request (at the push-to-talk press), waiting on stdin. <paramref name="MatchKey"/> says
/// which request it suits (same system prompt, screenshots, executable and model); <paramref name="WorkPath"/> is its temp
/// file or folder, removed by <paramref name="CleanUp"/> together with the process.
/// </summary>
internal sealed record PrestartedCliProcess(CliChatProcess Process, object MatchKey, string WorkPath, Action CleanUp) : IDisposable
{
    public void Dispose()
    {
        Process.Dispose();
        CleanUp();
    }
}

/// <summary>
/// Holds at most one prestarted process for a backend. The next prestart replaces it, a request takes it if it matches, and
/// it is killed when unused for <see cref="IdleTimeout"/> (a tap, an empty transcript) or when discarded. Thread-safe.
/// </summary>
internal sealed class PrestartedCliProcessSlot(TimeSpan? idleTimeout = null) : IDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(90);

    private readonly object gate = new();
    private PrestartedCliProcess? heldProcess;
    private Timer? idleTimer;

    public TimeSpan IdleTimeout { get; } = idleTimeout ?? DefaultIdleTimeout;

    public void Put(PrestartedCliProcess prestartedProcess)
    {
        PrestartedCliProcess? replacedProcess;
        lock (gate)
        {
            replacedProcess = heldProcess;
            heldProcess = prestartedProcess;
            idleTimer?.Dispose();
            idleTimer = new Timer(_ => DiscardIfStillHeld(prestartedProcess), null, IdleTimeout, Timeout.InfiniteTimeSpan);
        }
        replacedProcess?.Dispose();
    }

    /// <summary>The held process if <paramref name="matches"/> its key and it is still running; otherwise it is killed and null returned.</summary>
    public PrestartedCliProcess? Take(Func<object, bool> matches)
    {
        PrestartedCliProcess? takenProcess;
        lock (gate)
        {
            takenProcess = heldProcess;
            heldProcess = null;
            idleTimer?.Dispose();
            idleTimer = null;
        }
        if (takenProcess is null) return null;
        if (matches(takenProcess.MatchKey) && !takenProcess.Process.HasExited) return takenProcess;
        takenProcess.Dispose();
        return null;
    }

    public void Discard() => Take(_ => false);

    public void Dispose() => Discard();

    private void DiscardIfStillHeld(PrestartedCliProcess prestartedProcess)
    {
        lock (gate)
        {
            if (heldProcess != prestartedProcess) return;
            heldProcess = null;
        }
        prestartedProcess.Dispose();
    }
}

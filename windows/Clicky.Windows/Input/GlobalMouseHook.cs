using System.Runtime.InteropServices;

namespace Clicky.Windows.Input;

internal enum LowLevelMouseEventKind
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    MiddleDown,
    Wheel
}

/// <summary>One mouse event anywhere in the session, in physical virtual-screen pixels.</summary>
internal readonly record struct LowLevelMouseEvent(LowLevelMouseEventKind Kind, int X, int Y, long TimestampMilliseconds)
{
    public bool IsButtonDown => Kind is LowLevelMouseEventKind.LeftDown or LowLevelMouseEventKind.RightDown or LowLevelMouseEventKind.MiddleDown;
}

/// <summary>
/// System-wide listen-only mouse watcher (E11 T11.13): guided-step clicks, the step watcher, hover rests and the highlighter
/// all need to see the mouse, and the overlays are click-through. Like <see cref="GlobalPushToTalkKeyboardHook"/>, the
/// WH_MOUSE_LL hook lives on its own thread with a message loop (a busy UI thread must never slow every mouse move on the
/// machine) and is reinstalled every 10 s, since Windows silently drops slow hooks. It is installed only while someone holds
/// a lease (<see cref="Acquire"/>), so an idle Clicky adds nothing to every mouse event. Events never get swallowed.
/// </summary>
internal sealed class GlobalMouseHook : IDisposable, Companion.IMouseEventSource
{
    private static readonly TimeSpan HookReinstallInterval = TimeSpan.FromSeconds(10);
    private readonly object leaseGate = new();
    private int leaseCount;
    private Thread? hookThread;
    private uint hookThreadId;
    private IntPtr mouseHookHandle;
    private LowLevelMouseProc? mouseHookCallback; // kept in a field so the GC can't collect it while hooked
    private bool isDisposed;

    /// <summary>
    /// Raised on the hook thread for every event while installed. Handlers must return at once and marshal real work to
    /// their own thread; the mouse waits for them.
    /// </summary>
    public event Action<LowLevelMouseEvent>? MouseEventObserved;

    public bool IsInstalled => mouseHookHandle != IntPtr.Zero;

    /// <summary>Installs the hook if it isn't yet; it comes off when the last lease is disposed.</summary>
    public IDisposable Acquire()
    {
        lock (leaseGate)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            leaseCount++;
            if (leaseCount == 1) StartHookThread();
        }
        return new Lease(this);
    }

    private sealed class Lease(GlobalMouseHook mouseHook) : IDisposable
    {
        private int isReleased;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref isReleased, 1) == 0) mouseHook.Release();
        }
    }

    private void Release()
    {
        lock (leaseGate)
        {
            leaseCount--;
            if (leaseCount == 0) StopHookThread();
        }
    }

    private void StartHookThread()
    {
        using ManualResetEventSlim hookInstallAttempted = new();
        hookThread = new Thread(() => RunHookMessageLoop(hookInstallAttempted)) { IsBackground = true, Name = "Clicky mouse hook" };
        hookThread.Start();
        hookInstallAttempted.Wait();
    }

    private void StopHookThread()
    {
        if (hookThread is null) return;
        PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join();
        hookThread = null;
    }

    private void RunHookMessageLoop(ManualResetEventSlim hookInstallAttempted)
    {
        hookThreadId = GetCurrentThreadId();
        mouseHookCallback = HandleLowLevelMouseEvent;
        mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseHookCallback, GetModuleHandle(null), 0);
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // The queue exists before Acquire returns, so WM_QUIT isn't lost.
        hookInstallAttempted.Set();
        if (mouseHookHandle == IntPtr.Zero) return;

        UIntPtr reinstallTimerId = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)HookReinstallInterval.TotalMilliseconds, IntPtr.Zero);
        while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == WM_TIMER && message.WParam == (IntPtr)(nuint)reinstallTimerId)
            {
                IntPtr replacementHook = SetWindowsHookEx(WH_MOUSE_LL, mouseHookCallback, GetModuleHandle(null), 0);
                if (replacementHook == IntPtr.Zero) continue; // Keep the old one rather than none.
                UnhookWindowsHookEx(mouseHookHandle);
                mouseHookHandle = replacementHook;
                continue;
            }
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        KillTimer(IntPtr.Zero, reinstallTimerId);
        UnhookWindowsHookEx(mouseHookHandle);
        mouseHookHandle = IntPtr.Zero;
    }

    private IntPtr HandleLowLevelMouseEvent(int hookCode, IntPtr mouseMessage, IntPtr mouseHookStructPointer)
    {
        if (hookCode >= 0)
        {
            LowLevelMouseEventKind? kind = (int)mouseMessage switch
            {
                WM_MOUSEMOVE => LowLevelMouseEventKind.Move,
                WM_LBUTTONDOWN => LowLevelMouseEventKind.LeftDown,
                WM_LBUTTONUP => LowLevelMouseEventKind.LeftUp,
                WM_RBUTTONDOWN => LowLevelMouseEventKind.RightDown,
                WM_MBUTTONDOWN => LowLevelMouseEventKind.MiddleDown,
                WM_MOUSEWHEEL or WM_MOUSEHWHEEL => LowLevelMouseEventKind.Wheel,
                _ => null
            };
            if (kind is { } eventKind)
            {
                MouseHookStruct hookStruct = Marshal.PtrToStructure<MouseHookStruct>(mouseHookStructPointer);
                try
                {
                    MouseEventObserved?.Invoke(new LowLevelMouseEvent(eventKind, hookStruct.Point.X, hookStruct.Point.Y, Environment.TickCount64));
                }
                catch (Exception exception)
                {
                    // A handler bug must never break the mouse for the whole machine.
                    System.Diagnostics.Trace.TraceWarning($"Mouse hook handler failed: {exception.Message}");
                }
            }
        }
        return CallNextHookEx(mouseHookHandle, hookCode, mouseMessage, mouseHookStructPointer);
    }

    public void Dispose()
    {
        lock (leaseGate)
        {
            if (isDisposed) return;
            isDisposed = true;
            leaseCount = 0;
            StopHookThread();
        }
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint PM_NOREMOVE = 0x0000;

    private delegate IntPtr LowLevelMouseProc(int hookCode, IntPtr mouseMessage, IntPtr mouseHookStructPointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int hookCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr windowHandle, UIntPtr timerId, uint intervalMilliseconds, IntPtr timerProc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr windowHandle, UIntPtr timerId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax, uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}

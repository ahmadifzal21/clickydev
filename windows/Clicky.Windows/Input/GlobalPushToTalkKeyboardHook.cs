using System.Runtime.InteropServices;

namespace Clicky.Windows.Input;

/// <summary>One key going down or up anywhere in the session, as seen by the low-level hook.</summary>
/// <param name="VirtualKeyCode">Left/right-specific virtual key (e.g. VK_LCONTROL 0xA2, VK_RMENU 0xA5).</param>
/// <param name="IsKeyDown">True for WM_KEYDOWN / WM_SYSKEYDOWN (auto-repeat included), false for key-up.</param>
public readonly record struct LowLevelKeyboardEvent(uint VirtualKeyCode, bool IsKeyDown);

/// <summary>
/// System-wide listen-only keyboard watcher, the Windows counterpart of the Mac's listen-only
/// CGEvent tap in GlobalPushToTalkShortcutMonitor. A WH_KEYBOARD_LL hook is called on the thread
/// that installed it, and only while that thread pumps messages, so the hook gets its own
/// background thread with a GetMessage loop: a busy WPF dispatcher can then never delay every
/// keystroke on the machine (Windows silently drops hooks slower than LowLevelHooksTimeout).
/// The shortcut state machine and the foreground-change reset both run on that thread, so the
/// state machine needs no locking.
/// </summary>
public sealed class GlobalPushToTalkKeyboardHook : IDisposable
{
    private Thread? hookThread;
    private uint hookThreadId;
    private IntPtr keyboardHookHandle;
    private LowLevelKeyboardProc? keyboardHookCallback; // kept in a field so the GC can't collect it while hooked
    private IntPtr foregroundEventHook;
    private WinEventCallback? foregroundEventCallback; // same GC reason
    private readonly PushToTalkShortcutStateMachine shortcutStateMachine;

    public GlobalPushToTalkKeyboardHook(PushToTalkShortcut shortcut)
    {
        shortcutStateMachine = new PushToTalkShortcutStateMachine(shortcut);
    }

    /// <summary>
    /// Raised on the hook thread for every key down/up. Handlers must return quickly and marshal
    /// any real work elsewhere; the keystroke is held up until they return.
    /// </summary>
    public event Action<LowLevelKeyboardEvent>? KeyboardEventObserved;

    /// <summary>Raised on the hook thread when the shortcut is pressed or released; same rules as above.</summary>
    public event Action<PushToTalkShortcutTransition>? ShortcutTransitioned;

    public bool IsInstalled => keyboardHookHandle != IntPtr.Zero;

    /// <summary>Starts the hook thread and waits until the hook is installed. Returns whether it was.</summary>
    public bool Start()
    {
        if (hookThread != null)
        {
            return IsInstalled;
        }

        using var hookInstallAttempted = new ManualResetEventSlim();
        hookThread = new Thread(() => RunHookMessageLoop(hookInstallAttempted))
        {
            IsBackground = true, // never keeps the process alive on exit
            Name = "Clicky push-to-talk keyboard hook",
        };
        hookThread.Start();
        hookInstallAttempted.Wait();
        return IsInstalled;
    }

    private void RunHookMessageLoop(ManualResetEventSlim hookInstallAttempted)
    {
        hookThreadId = GetCurrentThreadId();
        keyboardHookCallback = HandleLowLevelKeyboardEvent;
        keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardHookCallback, GetModuleHandle(null), 0);
        // Forces this thread's message queue to exist before Start returns, so Dispose's WM_QUIT can't be lost.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        hookInstallAttempted.Set();
        if (keyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        // Out-of-context WinEvents are delivered through this thread's message loop.
        foregroundEventCallback = HandleForegroundChanged;
        foregroundEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            foregroundEventCallback, 0, 0, WINEVENT_OUTOFCONTEXT);

        // GetMessage returns 0 on WM_QUIT (posted by Dispose) and -1 on error; stop on either.
        while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (foregroundEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(foregroundEventHook);
            foregroundEventHook = IntPtr.Zero;
        }

        UnhookWindowsHookEx(keyboardHookHandle);
        keyboardHookHandle = IntPtr.Zero;
    }

    /// <summary>A focus change mid-press can swallow the key-up (see ResetHeldKeys), so release rather than stick.</summary>
    private void HandleForegroundChanged(IntPtr eventHook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTimeMilliseconds)
    {
        RaiseShortcutTransition(shortcutStateMachine.ResetHeldKeys());
    }

    private void RaiseShortcutTransition(PushToTalkShortcutTransition shortcutTransition)
    {
        if (shortcutTransition == PushToTalkShortcutTransition.None)
        {
            return;
        }

        try
        {
            ShortcutTransitioned?.Invoke(shortcutTransition);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Push-to-talk transition handler failed: {exception}");
        }
    }

    private IntPtr HandleLowLevelKeyboardEvent(int hookCode, IntPtr keyboardMessage, IntPtr keyboardHookStructPointer)
    {
        if (hookCode >= 0)
        {
            int message = keyboardMessage.ToInt32();
            // Alt (alone or with ctrl) turns key messages into WM_SYS*, so both families count.
            bool isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;
            if (isKeyDown || isKeyUp)
            {
                uint virtualKeyCode = (uint)Marshal.ReadInt32(keyboardHookStructPointer); // KBDLLHOOKSTRUCT.vkCode is first
                LowLevelKeyboardEvent keyboardEvent = new(virtualKeyCode, isKeyDown);
                try
                {
                    KeyboardEventObserved?.Invoke(keyboardEvent);
                }
                catch (Exception exception)
                {
                    // An exception escaping into user32 would crash the process; keep typing alive instead.
                    System.Diagnostics.Debug.WriteLine($"Push-to-talk keyboard handler failed: {exception}");
                }

                RaiseShortcutTransition(shortcutStateMachine.HandleKeyboardEvent(keyboardEvent));
            }
        }

        return CallNextHookEx(IntPtr.Zero, hookCode, keyboardMessage, keyboardHookStructPointer);
    }

    public void Dispose()
    {
        if (hookThread == null)
        {
            return;
        }

        PostThreadMessage(hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join();
        hookThread = null;
        keyboardHookCallback = null;
        foregroundEventCallback = null;
    }

    // ---- Win32 interop ----

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate IntPtr LowLevelKeyboardProc(int hookCode, IntPtr keyboardMessage, IntPtr keyboardHookStructPointer);

    private delegate void WinEventCallback(IntPtr eventHook, uint eventType, IntPtr windowHandle,
        int objectId, int childId, uint eventThreadId, uint eventTimeMilliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int hookCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr eventHookModule,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr windowHandle, uint messageFilterMin, uint messageFilterMax, uint removeFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Clicky.Windows.Input;

namespace Clicky.Windows.Tests;

public sealed class GlobalPushToTalkKeyboardHookTests
{
    private const ushort VK_F24 = 0x87; // no app binds F24, so injecting it is harmless

    [Fact]
    public void InstalledHook_SeesInjectedKeyDownThenUp_AndStopsOnDispose()
    {
        ConcurrentQueue<LowLevelKeyboardEvent> observedEvents = new();
        GlobalPushToTalkKeyboardHook keyboardHook = new(PushToTalkShortcut.Default);
        keyboardHook.KeyboardEventObserved += observedEvents.Enqueue;

        Assert.True(keyboardHook.Start());
        Assert.True(keyboardHook.Start()); // second Start is a no-op
        SendKey(VK_F24, isKeyUp: false);
        SendKey(VK_F24, isKeyUp: true);
        Assert.True(SpinWait.SpinUntil(() => observedEvents.Count(e => e.VirtualKeyCode == VK_F24) >= 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(
            new[] { new LowLevelKeyboardEvent(VK_F24, true), new LowLevelKeyboardEvent(VK_F24, false) },
            observedEvents.Where(e => e.VirtualKeyCode == VK_F24).ToArray());

        keyboardHook.Dispose();
        Assert.False(keyboardHook.IsInstalled);
        observedEvents.Clear();
        SendKey(VK_F24, isKeyUp: false);
        SendKey(VK_F24, isKeyUp: true);
        Thread.Sleep(200);
        Assert.DoesNotContain(observedEvents, e => e.VirtualKeyCode == VK_F24);
    }

    [Fact]
    public void InstalledHook_TurnsInjectedCtrlAltIntoPressedThenReleased()
    {
        const ushort VK_LCONTROL = 0xA2, VK_LMENU = 0xA4;
        ConcurrentQueue<PushToTalkShortcutTransition> observedTransitions = new();
        using GlobalPushToTalkKeyboardHook keyboardHook = new(PushToTalkShortcut.Default);
        keyboardHook.ShortcutTransitioned += observedTransitions.Enqueue;
        Assert.True(keyboardHook.Start());
        try
        {
            SendKey(VK_LCONTROL, isKeyUp: false);
            SendKey(VK_LMENU, isKeyUp: false);
        }
        finally
        {
            // Always let go, so a failure can't leave ctrl+alt held down on the test machine.
            SendKey(VK_LMENU, isKeyUp: true);
            SendKey(VK_LCONTROL, isKeyUp: true);
        }

        Assert.True(SpinWait.SpinUntil(() => observedTransitions.Count >= 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { PushToTalkShortcutTransition.Pressed, PushToTalkShortcutTransition.Released }, observedTransitions.ToArray());
    }

    private static void SendKey(ushort virtualKey, bool isKeyUp)
    {
        KeyboardInput[] inputs = [new KeyboardInput { Type = 1, VirtualKey = virtualKey, Flags = isKeyUp ? 2u : 0u }];
        Assert.Equal(1u, SendInput(1, inputs, Marshal.SizeOf<KeyboardInput>()));
    }

    /// <summary>INPUT with a KEYBDINPUT payload, padded to the size of the union's largest member (MOUSEINPUT).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)] // x64 layout: the union starts at 8 for pointer alignment
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, KeyboardInput[] inputs, int inputSize);
}

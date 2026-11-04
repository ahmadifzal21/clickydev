# Clicky for Windows — Porting Plan

Port of the macOS Clicky app (`leanring-buddy/`) to Windows as a C# / .NET 8 WPF
single-file `.exe`. The Mac app and `worker/` are untouched; everything Windows
lives in `windows/`.

## Layout

```
windows/
  Clicky.Windows.sln
  Clicky.Windows/                 the app (WPF + a little WinForms for the tray icon)
    App.xaml(.cs)                 entry point, single-instance guard, no main window
    app.manifest                  PerMonitorV2 DPI awareness
    Companion/                    state machine, system prompts, [POINT] tag parsing
    Configuration/                clicky.config.json model + load/save
    AI/                           chat backends (Claude Code CLI, Codex CLI, Worker) + router
    Tray/                         tray icon + floating companion panel   (step 1)
    Overlay/                      transparent click-through cursor overlay (step 2)
    Input/                        low-level keyboard hook for push-to-talk (step 3)
    ScreenCapture/                multi-monitor capture                    (step 4)
    Voice/                        mic capture, Whisper.net STT, Kokoro TTS  (steps 6-7)
  Clicky.Windows.Tests/           xUnit tests for the pure logic (parsing, coordinates, config)
```

## Swift → Windows mapping

| Mac (Swift) | Windows (C#) |
|---|---|
| `leanring_buddyApp.swift` / `CompanionAppDelegate` | `App.xaml.cs` (`ShutdownMode=OnExplicitShutdown`, no main window) |
| `MenuBarPanelManager.swift` (`NSStatusItem` + `NSPanel`) | `Tray/TrayIconManager.cs` (WinForms `NotifyIcon`) + `Tray/CompanionPanelWindow.xaml` (borderless, topmost, `WS_EX_NOACTIVATE`, hides on deactivate/outside click) |
| `CompanionPanelView.swift` | `CompanionPanelWindow.xaml`: status, push-to-talk hint, backend pickers, quit |
| `OverlayWindow.swift` | `Overlay/CursorOverlayWindow.xaml`: one per monitor, `WS_EX_TRANSPARENT \| WS_EX_LAYERED \| WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE`, topmost |
| `CompanionResponseOverlay.swift` | response bubble + waveform inside the overlay window |
| `GlobalPushToTalkShortcutMonitor.swift` (`CGEvent` tap) | `Input/GlobalPushToTalkKeyboardHook.cs` (`SetWindowsHookEx(WH_KEYBOARD_LL)`), default `ctrl+alt` |
| `CompanionScreenCaptureUtility.swift` (ScreenCaptureKit) | `ScreenCapture/CompanionScreenCaptureUtility.cs` (Windows Graphics Capture per monitor, GDI `BitBlt` fallback, overlay excluded via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`) |
| `ClaudeAPI.swift` via Worker | `AI/ClaudeCodeCliChatBackend.cs` (`claude -p --input-format stream-json`, base64 images over stdin), `AI/CodexCliChatBackend.cs` (`codex exec -i <jpg>`), `AI/CloudflareWorkerChatBackend.cs` (existing `/chat` route) |
| AssemblyAI / OpenAI / Apple Speech | `Voice/WhisperLocalTranscriber.cs` (default: Whisper.net in-process, model loaded once), `Voice/AssemblyAITranscriber.cs` (only if local feels slow) |
| `ElevenLabsTTSClient.swift` | `Voice/KokoroTextToSpeech.cs` (default: Kokoro-82M in-process via KokoroSharp/ONNX), `Voice/ElevenLabsTextToSpeech.cs` (only if local feels slow) |
| `CompanionManager.swift` | `Companion/CompanionManager.cs` (same idle → listening → processing → responding states) |

Kept verbatim: the companion voice system prompt, the onboarding-demo pointing
prompt, and the `[POINT:x,y:label:screenN]` regex.

Not ported (Mac-only or out of scope): onboarding video/music, PostHog analytics,
FormSpark email capture, macOS permission flows (Windows needs no
screen-recording/accessibility grants; the mic prompt is handled by Windows
privacy settings).

## Coordinates (multi-monitor + DPI)

The process is PerMonitorV2 DPI aware, so every Win32 API (cursor position,
monitor rects, captured frames) works in **physical pixels in virtual-screen space**.
That is the single "global" coordinate system, and it replaces the Mac's
AppKit bottom-left points.

1. The screenshot of monitor N is downscaled to a max dimension of 1280 and labeled
   with its pixel size (same as Mac).
2. The model answers in screenshot pixels: `[POINT:x,y:label:screenN]`.
3. Screenshot pixels → monitor physical pixels: `x * monitorWidthPx / screenshotWidthPx`.
4. Plus the monitor's top-left in virtual-screen space → global physical point.
5. The overlay window for that monitor converts global physical → its own WPF DIPs:
   `(globalX - monitorLeft) / monitorDpiScale`.

There's no Y flip on Windows because both spaces use a top-left origin.

## Routing — `clicky.config.json`

Loaded from next to the `.exe` if present, otherwise `%APPDATA%\Clicky\clicky.config.json`
(created with defaults on first run). The tray menu edits and re-saves it.

Jobs:
- **answer**: the push-to-talk voice reply (spoken text + inline `[POINT]` tag)
- **pointing**: point-only requests (the onboarding-style "find something and point")
- **fallback**: tried once if the routed backend throws/times out

Backends: `claude-code-cli`, `codex-cli`, `cloudflare-worker`.

## Steps (each one is a test checkpoint on your machine)

0. **Skeleton**: solution, csproj (single-file publish), manifest, prompts, POINT parser
   + tests, config load/save + tests, backend interfaces. App launches to a tray icon with Quit.
1. **Tray + floating panel**: tray icon, left-click toggles the dark rounded panel near the
   tray, auto-dismiss on outside click, backend pickers wired to the config.
2. **Cursor overlay**: per-monitor transparent click-through windows, blue triangle
   follows the mouse, bezier-arc flight to a point and back, DPI-correct. Tray item
   "Test pointing" flies to a set of known coordinates on each monitor.
3. **Push-to-talk hook**: low-level keyboard hook, press/release transitions, overlay
   shows listening state. Tray shows the shortcut.
4. **Screen capture**: per-monitor JPEG capture, cursor screen first, labels match the Mac.
   Primary method is Windows Graphics Capture (`Windows.Graphics.Capture`, one frame per
   monitor via `CreateForMonitor`, border and system cursor turned off). It reads what the
   compositor shows, so GPU-drawn apps and HDR monitors come out right where `BitBlt` can
   return black or washed-out frames. GDI `BitBlt` stays as a per-monitor fallback if WGC
   fails to start. Requires targeting `net8.0-windows10.0.19041.0`. Tray item "Save debug screenshots" writes them to a folder so you can check them.
5. **AI backends + router**: Claude Code CLI, Codex CLI, Worker; fallback; history (last 10).
   Tray item "Ask about my screen (typed)" for testing without voice.
6. **Speech-to-text**: NAudio mic capture → Whisper.net in-process (`base.en` first, `small.en`
   if accuracy is short), CPU runtime, model downloaded on first use and loaded once at start-up.
   Transcribes on key-up. AssemblyAI is added only if this feels too slow end to end.
7. **Text-to-speech**: Kokoro-82M (v1.0, ONNX) in-process via the KokoroSharp NuGet package,
   model loaded once at start-up, CPU package first (DirectML if CPU is too slow). ElevenLabs is
   added only if this feels too slow or flat end to end.
8. **Full pipeline**: hotkey → record → transcribe → capture → answer → speak → point;
   transient cursor mode; single-file publish.

## Risks / open questions

- **CLI latency**: `claude -p` and `codex exec` spawn a fresh process per turn (several
  seconds of startup). Acceptable for a first version; we'll measure in step 5.
- **Local voice first, cloud later**: prototype with Whisper.net + Kokoro and measure the
  whole loop in step 8. Swap in AssemblyAI (STT) or ElevenLabs (TTS) only where the lag is felt;
  both sit behind the same transcriber / speaker interfaces.
- **Whisper transcribes after release**; it doesn't stream words while you talk. No NVIDIA GPU
  on the dev PC (Intel Arc), so the CPU runtime first; Whisper.net's Vulkan runtime is an option
  to measure if decoding is slow.
- **Model downloads** on first use: Whisper `base.en` ~140 MB, Kokoro ~320 MB.
- **WGC capture border**: Windows draws a yellow border around a captured screen. Windows 11
  lets us turn it off; Windows 10 can't, so the border flashes briefly per capture there.
  DRM-protected video is black with either capture method.
- `ctrl+alt` is AltGr on some keyboard layouts; the shortcut is configurable.

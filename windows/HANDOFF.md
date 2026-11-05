# Agent handoff — Clicky Windows port

RELAY-STATUS: complete

<!-- The relay (agent-relay/relay.ps1) stops when the line above reads `RELAY-STATUS: complete`.
     Set it to complete only when every ticket in "Scope" is committed. -->

## Scope

Epic **E7 — Text-to-speech** (current; see "Current ticket"). Before it: **E6 — Speech-to-text**. Previous: epic **E5 — AI backends + router** (see `PORTING-PLAN.md` step 5 and "Routing"; code goes in `Clicky.Windows/AI/`,
implementing the existing `ICompanionChatBackend`; settings already exist in `Configuration/ClickyConfiguration.cs`).
Checkpoint: tray "Ask about my screen (typed)" with a typed question gets an answer from each backend; the cursor points
when the answer has a [POINT] tag.
Port from the Mac (read only): `leanring-buddy/ClaudeAPI.swift`, history + POINT handling in `CompanionManager.swift`;
the Worker's `/chat` contract is in `worker/src/index.ts` (read only). Prompts are in `Companion/CompanionSystemPrompts.cs`,
tag parsing in `Companion/PointTagParser.cs`, screenshots from `ScreenCapture/`.

Tickets: T5.1 ClaudeCodeCliChatBackend (`claude -p`: T5.1.1 images as base64 blocks via `--input-format stream-json`;
T5.1.2 `--system-prompt`, `--model`, no tools, `--no-session-persistence`; T5.1.3 parse the final result from the output
stream; T5.1.4 history as prior turns; T5.1.5 timeout/cancel kills the process tree; T5.1.6 clear errors for not
installed / not logged in) · T5.2 CodexCliChatBackend (`codex exec`: T5.2.1 screenshots to temp files, one `-i` each,
cleaned up; T5.2.2 system prompt, labels and history folded into the prompt; T5.2.3 `--skip-git-repo-check`, read-only
sandbox, `--ephemeral`, `-o` last message; T5.2.4 timeout/cancel) · T5.3 CloudflareWorkerChatBackend (existing `/chat`
route, SSE; empty `WorkerBaseUrl` = clear "not set up" error) · T5.4 ChatBackendRouter: per-job routing + one fallback
attempt (T5.4.1 unit tests with fake backends) · T5.5 conversation history: last 10 exchanges, POINT tag stripped ·
T5.6 tray "Ask about my screen (typed)" test box (capture -> route -> show reply in the overlay bubble -> PointAt if tagged)
· T5.7 measure latency per backend (log timings; write the numbers + a keep-a-process-warm recommendation in the Ticket
log; don't build warm processes unless clearly needed). T5.8 (verify each backend on the user's machine) is the user's
job — never mark it.
Use `--help` of the installed `claude` / `codex` to confirm flags before relying on them. Tests must not call the real
CLIs or network; put argument building / output parsing in pure, tested helpers.

## Rules for whichever agent is working

1. Work one ticket at a time, in order. Before starting, read this file, `PORTING-PLAN.md`, and
   `git log --oneline -10` / `git status` (a previous agent may have left uncommitted work).
2. Code style: follow the existing files in `Clicky.Windows/` (long descriptive names, XML doc
   comments explaining *why*, WinForms types fully qualified — implicit WinForms usings are off).
3. Proof per ticket: `dotnet build Clicky.Windows.sln -c Debug` and `dotnet test` from `windows/`
   must pass. Add xUnit tests for any pure logic (e.g. panel positioning math).
4. When a ticket is done: append a line to "Ticket log" and to "Tracker sync pending" below, and
   update "Current ticket". Then commit on branch `windows-port` (message `E<n> T<n>.x: <what>`, no push).
   **Codex** can't write `.git` (sandbox): instead write the full commit message to
   `windows/agent-relay/commit-message.txt` and **exit** — the relay commits everything under
   `windows/` plus `AGENTS.md` and starts you again for the next ticket. One ticket per run.
5. Mid-ticket, if you sense you are running low on context or quota, stop coding, write what is
   half-done under "Current ticket" (files touched, what remains), and exit. Don't commit broken
   builds; leave the work uncommitted and say so.
6. Tracker: https://claude.ai/artifact/MDLM6aQJTQdjsDWwsPb62X, collection `epics`, doc `E5`.
   Claude: if the ArtifactData tool is available, apply every "Tracker sync pending" entry
   (status `testing` for code-complete tasks/subtasks; pin `if_version`), then clear the list.
   Codex: you can't reach the tracker; just append entries there.
7. Never push, never touch `leanring-buddy/` or `worker/`.

## Current ticket

**E7 (text-to-speech) agent work committed: T7.1 (T7.1.1, T7.1.2), T7.2, T7.3 (decided: CPU), T7.5.** T7.4 (ElevenLabs) is
deliberately not built: only if local speech feels slow or flat after E8. T7.6 (verify voices on the user's machine) is the user's:
tray "Speak test line" plays with `voice.kokoroVoice`; holding the push-to-talk shortcut (or clicking the item again) cuts it off;
the console log shows "Kokoro first sound after N ms". Try other voices by editing `kokoroVoice` (e.g. `am_michael`, `bf_emma`).
The config store migrates the old `"textToSpeech": "edge-tts"` default to `kokoro-local` on load (the user's file is migrated).

## Ticket log (newest last)

- T1.1 done: `Tray/TrayIconManager.cs` — NotifyIcon with a code-drawn blue cursor icon (white
  outline), `TrayIconLeftClicked(Point physicalCursorPos)` event, context menu passed in by the
  caller, hide+dispose+DestroyIcon on Dispose. Not wired into App yet (T1.5 does that).
- T1.2 done: `Tray/CompanionPanelWindow.xaml(.cs)` — borderless transparent window, 320-wide
  card (DS tokens in `Tray/ClickyDesignSystem.xaml`, merged in App.xaml), drop shadow,
  WS_EX_NOACTIVATE|TOOLWINDOW, `TogglePanel(Point)` / `ShowNearTrayClick` / `HidePanel`.
  Placement math in `Tray/CompanionPanelPlacement.cs` (+ tests): any taskbar edge, any monitor,
  physical px via GetMonitorInfo + GetDpiForMonitor. Outside click = WH_MOUSE_LL while visible;
  Escape = RegisterHotKey while visible; tray click within 350 ms of an outside-click hide is ignored.
- T1.3 done: panel content in `CompanionPanelWindow.xaml` — header (status dot+glow, "Clicky",
  status text, close x), push-to-talk hint from config, Show Clicky switch (saves
  `IsClickyCursorAlwaysVisible`), Quit (`QuitRequested` event). Window ctor now takes
  `ClickyConfigurationStore`; `SetCompanionStatus(isOverlayVisible, voiceState)` for the app.
  Text mapping in `Tray/CompanionPanelText.cs` (+ tests). Styles: DS.PanelButton,
  DS.CircleIconButton, DS.ToggleSwitch, DS.SegmentButton (all hand cursor + hover).
- T1.4 done: `Tray/BackendRoutingPickerRows.cs` (+ tests) defines the Answer/Pointing/Fallback
  rows once; panel builds segmented pickers from it, `Tray/TrayContextMenu.cs` builds checked
  submenus + Open config file + error line + Quit. Both save via UpdateAndSave and re-sync on
  ConfigurationChanged. Panel shows a warning banner when the config failed to parse.
- T1.5 done: `App.xaml.cs` wires TrayContextMenu + TrayIconManager + CompanionPanelWindow; Quit from
  panel or menu shuts down; OnExit disposes the tray icon first. AGENTS.md Windows section updated.
  Smoke run: Clicky.exe launches and stays up (tray clicks not automatable here; T1.6 is manual).

- T2.1 done: `Display/DisplayMonitorCatalog.cs` (EnumDisplayMonitors + GetMonitorInfo +
  per-axis GetDpiForMonitor, primary first; hidden HWND catches WM_DISPLAYCHANGE / WM_SETTINGCHANGE /
  WM_DPICHANGED, coalesced refresh on the dispatcher, `MonitorsChanged` event), `Display/DisplayMonitor.cs`,
  `PixelRect` moved to `Display/`. App owns the catalog. Tests in `DisplayMonitorTests.cs`.
- T2.2 done: `Overlay/CursorOverlayWindow.cs` (transparent WPF window, WS_EX_TRANSPARENT|LAYERED|TOOLWINDOW|
  NOACTIVATE, WDA_EXCLUDEFROMCAPTURE before show, HWND_TOPMOST at physical monitor bounds, WM_WINDOWPOSCHANGING
  pins physical bounds, WM_DPICHANGED -> catalog RequestRefresh), `Overlay/CursorOverlayManager.cs` (one window per
  monitor, rebuilt on MonitorsChanged, keeps old windows if a rebuild fails, EVENT_SYSTEM_FOREGROUND hook re-raises
  topmost). App owns the manager. STA native tests in `CursorOverlayWindowTests.cs`.
- T2.3 done: `Overlay/BlueCursorView.xaml(.cs)` (16x16 triangle at -35°, #3380FF + glow; new DS token
  `DS.OverlayCursorBlue(Color)`), `Overlay/CursorFollowSpring.cs` (SwiftUI spring response 0.2 / damping 0.6),
  `Overlay/OverlayCoordinateMapper.cs` (physical px -> overlay DIPs; T2.5 extends it). Manager drives every
  window from `CompositionTarget.Rendering` + GetCursorPos; triangle at mouse + (35, 25) DIPs, visible only on
  the mouse's monitor. Idle cost ~5% of one core. Tests in `BlueCursorFollowTests.cs` + overlay window tests.
- T2.4 done: `Overlay/PointingFlightArc.cs` (Mac bezier: 0.6-1.4 s by distance, arc lift min(0.2d, 80), smoothstep,
  tangent rotation +90, 1.3x sin pulse; landing spot target+(8,12) clamped 20 DIPs inside). `BlueCursorView` runs the
  sequence per frame: fly -> bubble (11pt, 0.5->1 spring pop, glow, 30-60 ms chars, random Mac phrase if no label) ->
  hold 3 s -> fade 0.5 s -> fly back; return flight yields if the mouse moves >100 DIPs. Only one buddy visible while
  pointing. `CursorOverlayManager.PointAt(physX, physY, text)` picks the monitor's overlay and cancels others.
  `CursorFollowSpring` now takes response/damping + `PlaceAt`. Nothing calls PointAt yet (T2.8 tray item).
- T2.5 done: `OverlayCoordinateMapper.ScreenshotPixelsToPhysicalPixels(monitor, ScreenshotPixelCoordinate, w, h)`
  (scale by monitor px / screenshot px, add monitor origin, clamp onto the monitor's last pixel like the Mac's clamp).
  Tests: mixed per-axis DPI, negative origins, clamping, zero-size screenshot rejected (`BlueCursorFollowTests.cs`).
- T2.6 done: `BlueCursorView` adds the listening waveform (5 bars, Mac `barHeight` formula, power eased over 80 ms),
  processing spinner (trimmed 0.15-0.85 arc as 24 opacity-graded segments since WPF has no angular gradient,
  0.8 s/turn) and triangle cross-fades by `CompanionVoiceState` (0.25 / 0.15 s easeIn). Response bubble ports the
  (Mac-unused) `CompanionResponseOverlay`: 13pt card, max 300, mouse + (22, 6), flips/clamps in the work area,
  "..." while empty, hold 6 s after `FinishResponse` then 0.4 s fade; drawn only on the mouse's monitor. Waveform /
  spinner only animate in their state. Manager broadcasts state and re-applies it after overlay rebuilds.
  Smoke: app starts; idle ~8% of one core (was ~5%; per-frame `CompositionTarget.Rendering` is the main cost).
- T2.7 done: `CursorOverlayManager(catalog, isCursorAlwaysVisible)` owns visibility: Show Clicky on -> 2 s easeIn
  first-appearance fade-in; `SetCursorAlwaysVisible` shows/hides at once (ignores unchanged values);
  `ShowTransiently` (interaction start) / `ScheduleTransientHide` (interaction over: waits for any pointing flight,
  +1 s, 0.4 s easeIn fade, hide). Fades run per frame in `AdvanceFrame` (tests drive it). Rendering is unsubscribed
  while hidden, so a hidden Clicky costs nothing; `PointAt` is a no-op while hidden. `CompanionStatusChanged`
  replaces the temporary App status wiring. Smoke: starts, idle ~4% of one core with Show Clicky on.
- T2.8 done: tray menu "Test pointing" -> `Overlay/PointingTestTour.cs`: per monitor, top-left (40 px inset), centre and
  bottom-right of a fake 1280-longest-side screenshot, mapped via `ScreenshotPixelsToPhysicalPixels`; shows the buddy
  transiently, waits for each full flight (poll `CursorOverlayManager.IsPointing`), then `ScheduleTransientHide`.
  Ignores clicks while running; stops if Show Clicky hides the overlay. Target math tested; the tour itself is manual.

- T3.1 done: `Input/GlobalPushToTalkKeyboardHook.cs` — WH_KEYBOARD_LL on a dedicated background thread with a
  GetMessage loop (so a busy WPF dispatcher can't stall system typing); `Start()` waits for install and returns success;
  `KeyboardEventObserved(LowLevelKeyboardEvent(vk, isKeyDown))` fires on the hook thread for KEY*/SYSKEY* down/up
  (left/right-specific vk, auto-repeat not filtered yet); handler exceptions swallowed; always CallNextHookEx;
  Dispose posts WM_QUIT, joins, unhooks. Not wired into App yet. Native test injects F24 via SendInput.
- T3.2 done: `Input/PushToTalkShortcut.cs` — record (`PushToTalkModifierKeys` flags Control/Alt/Shift/Windows + optional
  trigger vk). `TryParse`: '+'-joined, case/space-insensitive, ctrl/control, alt/option, shift, win/windows, and at most one
  of space / a-z / 0-9 / f1-f24; needs >=1 modifier; rejects duplicates/unknowns and returns `Default` (ctrl+alt).
  Config default now uses `PushToTalkShortcut.DefaultShortcutText`. Tests in `PushToTalkShortcutTests.cs`.
- T3.3 done (T3.3.1-T3.3.4): `Input/PushToTalkShortcutStateMachine.cs` — pure port of Mac `shortcutTransition`, tracking held
  modifier vks (hook gives no flags). Modifier-only: pressed while required modifiers held by either side (extras allowed),
  released when one lets go. With a key: pressed on non-repeat key-down with modifiers held, released on that key's up.
  Repeats (down for an already-held key) ignored. `ResetHeldKeys()` releases + forgets held keys. The hook now takes a
  `PushToTalkShortcut`, owns the state machine, raises `ShortcutTransitioned` on the hook thread, and installs an
  EVENT_SYSTEM_FOREGROUND WinEvent on the same thread that calls `ResetHeldKeys` (no stuck press). Tests:
  `PushToTalkShortcutStateMachineTests.cs` + native ctrl+alt injection in `GlobalPushToTalkKeyboardHookTests.cs`.
- T3.4 done: hook always returns CallNextHookEx (handler exceptions caught). Windows drops timed-out LL hooks silently, so a
  thread timer (WM_TIMER in the hook's own loop, every 10 s) installs a fresh hook then unhooks the old one; if the old
  unhook fails (already dropped) held keys are reset. Internal ctor takes the interval; test checks reinstalls happen and
  each key is still seen exactly once.
- T3.5 done: App creates/starts the hook with the parsed config shortcut (invalid -> ctrl+alt) and calls
  `ChangeShortcut` on ConfigurationChanged (posted to the hook thread as WM_APP+1; releases a press in progress and starts
  with no held keys). Note: ConfigurationChanged only fires on in-app saves; hand edits to the file need a restart (the
  panel note says so). Panel shows the effective shortcut plus `CompanionPanelText.GetShortcutNote` (invalid shortcut, or
  the AltGr = Ctrl+Alt clash). AGENTS.md Windows section documents push-to-talk + AltGr (there's no windows/README).
  Smoke: app starts with the hook installed. Hook transitions aren't consumed yet (T3.6).
- T3.6 done: App marshals `ShortcutTransitioned` to the dispatcher: Pressed -> `ShowTransiently` + voice state Listening
  (waveform; panel status "Listening" via `CompanionStatusChanged`); Released -> Idle + `ScheduleTransientHide` (no voice
  pipeline yet). Smoke: app running, injected ctrl+alt hold/release, app stays up (overlay is capture-excluded, so the
  visual check is T3.7).

- E4 done (T4.1-T4.7.1): `ScreenCapture/` — WGC per monitor in parallel (cursor off, border on), GDI CopyFromScreen
  fallback per monitor, one WinRT JPEG encoder (Mac sizing, quality 0.8), cursor screen first + verbatim Mac labels,
  panel now also WDA_EXCLUDEFROMCAPTURE, tray "Save debug screenshots" (+ capture-log.txt with method/timing).
  TFM is now net8.0-windows10.0.19041.0. Single 2560x1600 monitor here: WGC ~210 ms; real-capture tests in xUnit.

- T5.1 done: `AI/ClaudeCodeCliChatBackend.cs` + pure `AI/ClaudeCodeCliProtocol.cs` + `AI/CliChatProcess.cs`. `claude -p`
  stream-json in/out, labeled base64 JPEG blocks, `--system-prompt`, optional `--model`, `--tools ""`, `--safe-mode`
  (keeps login, unlike `--bare`), `--strict-mcp-config`, `--disable-slash-commands`, `--no-session-persistence`.
  History = prior user messages with `shouldQuery:false` + synthetic assistant envelopes; `client_composed:true` keeps
  @paths and /commands literal (checked against claude-agent-sdk 0.3.318, CLI 2.1.318). Final `result` parsed; clear
  errors for not installed / not logged in / timeout; timeout+cancel kill the process tree. 27 tests, no real CLI calls.
  Codex's sandbox desktop failed the 7 SendInput/capture tests; on the interactive desktop all 155 pass.
- T5.2 done: `AI/CodexCliChatBackend.cs` + pure `AI/CodexCliProtocol.cs` (reuses `CliChatProcess`). Per request a
  `%TEMP%/clicky-codex-*` folder holds screen-N.jpg (one `--image` each) and the `-o` file; it is the working directory
  and is deleted in `finally`. `exec --skip-git-repo-check --sandbox read-only --ephemeral --color never`, optional
  `--model`, then `-- -` (prompt on stdin; `--` stops variadic `--image` eating `-`). System prompt, history and image
  labels folded into the prompt. Codex 0.163.0: real run confirmed the argument shape parses and stdin is read (account
  was at its usage limit, so no answer). Note: the user's Codex hooks (UserPromptSubmit) run on every request;
  `--ignore-user-config` would stop that but also drop their config.toml model/provider, so it's not used. 10 tests.
  (Reversed 2026-11-05: now used, see the T5.8 entry.)

- T5.3 done: `AI/CloudflareWorkerChatBackend.cs` + pure `AI/CloudflareWorkerProtocol.cs`. POST `{workerBaseUrl}/chat` with the
  Mac's Messages body (model, max_tokens 1024, stream, system, history as text turns, images+labels+question; content blocks
  shared with Claude Code via `ClaudeCodeCliProtocol.BuildCurrentUserContentBlocks`). SSE: joins `text_delta`s, stops on
  `message_stop`/`[DONE]`, skips bad lines, in-stream `error` event throws. Empty/non-http base URL = "not set up" error;
  HTTP errors show status + Anthropic `error.type: message` (+ hints for 401/403/404); unreachable / timeout / caller-cancel
  distinguished. One shared HttpClient (infinite timeout, our own token) keeps TLS warm. 15 tests via a fake handler, no network.
- T5.4 done: `AI/ChatBackendRouter.cs` — `(Func<JobBackendRouting>, backends)`; routing read per request (live tray changes);
  `GetResponseAsync(job, request, ct)` -> `RoutedChatResponse(text, answeringBackendKind)`. Routed backend fails/times out
  -> fallback once (skipped if null, same backend, or caller cancelled); both fail -> one message with both reasons
  (inner AggregateException). Unknown backend kind = error that can fall back. 9 fake-backend tests (T5.4.1).
- T5.5 done: `Companion/CompanionConversationHistory.cs` — `Record(transcript, fullResponse)` stores the reply with the [POINT]
  tag stripped (via `PointTagParser`), keeps the last 10; `GetExchanges()` returns a snapshot for `CompanionChatRequest`.
  UI-thread only. 3 tests.
- T5.6 done: tray "Ask about my screen (typed)" -> `Companion/TypedScreenQuestionRunner.cs`: VB InputBox (debug-only shortcut),
  buddy shown transiently + spinner, 150 ms for the box to leave the compositor, capture all screens, route job Answer with
  history, record exchange, Idle, reply (tag stripped) in the response bubble, PointAt the resolved target with the label.
  Errors show "Clicky couldn't answer: ..." in the bubble. Pure `Companion/CompanionScreenQuestion.cs`: labels get the Mac's
  " (image dimensions: WxH pixels)"; `:screenN` picks that capture, else the cursor screen (6 tests). `CreateRouter` shares the
  live settings objects. Smoke: app starts. Known: in transient mode the hide (+1 s after pointing) can cut the 6 s bubble short.
- T5.7 done (measured 2026-11-05, one 2560x1600 monitor, real backend classes, question "what app is in front?"):
  capture 290-530 ms. Claude Code CLI (sonnet): answer 4.1-5.0 s, one outlier 8.2 s; text-only `claude -p` with the same
  flags is 3.4-5.3 s and `claude --version` is ~0.1 s, so images add little and the cost is CLI session setup + model time.
  Codex CLI: could not answer (account at its usage limit until 5:54 PM); its runs still took 6.4-8.7 s just to start, run
  the user's SessionStart/UserPromptSubmit hooks and fail. Cloudflare Worker: not set up here (no `workerBaseUrl`), so no
  number. Found + fixed while measuring: `codex` is an npm `codex.cmd` shim, which `Process.Start` can't find from a bare
  name; `CliChatProcess.ResolveExecutablePath` now searches PATH for .exe/.cmd/.bat (risk noted in code: a .cmd shim goes
  through cmd.exe, so an npm-installed *Claude* would mangle the multi-line `--system-prompt`; Claude here is a native exe).
  Codex errors now show only its `ERROR:` lines instead of 1500 chars of config/hook noise.
  **Recommendation: don't build warm processes now.** Startup is ~0.1 s; the 3-5 s is per-session setup and model
  first-token time, and a warm `claude` process would need multi-turn stream-json plumbing for maybe 1-2 s. Bigger wins
  first, when the voice pipeline lands: stream text (Worker SSE / Claude stream-json partials) so TTS starts on the first
  sentence. Revisit warm processes if end-to-end voice latency is still > ~6 s.

- E5 follow-up: transient hide now also waits for the reply bubble (6 s hold + fade) via `IsShowingResponse`, so the
  T5.6 "bubble cut short" issue is fixed. Claude's system prompt now goes by `--system-prompt-file` (temp file, deleted after),
  so an npm `claude.cmd` shim can't mangle it; a test runs the real argument list through a `.cmd` echo shim. Real CLI smoke OK.

- T5.8 automated (2026-11-05): `Clicky.Windows.Tests/LiveBackendVerificationTests.cs` (opt-in: `$env:CLICKY_LIVE_BACKENDS=1;
  dotnet test --filter Category=LiveBackend`) shows an orange "CLICKY TARGET" box, captures, asks each real backend to point
  at it, checks the [POINT] lands on the box (60 px margin) and the overlay starts pointing. Results: Claude Code CLI pass
  (5.0 s, [POINT:640,376] = box centre); Codex CLI pass (25.3 s, same point); Cloudflare Worker not set up (no workerBaseUrl).
  Not automated: watching the flight/bubble on screen (overlay is capture-excluded).

- Codex speed (2026-11-05): benchmarked `codex exec` on one 1280x800 screenshot, 2 runs each: as-is 19.6/27.6 s,
  `-c model_reasoning_effort="low"` 15.7/16.2 s, `--ignore-user-config` 6.7/6.0 s, same answers. Most of the cost is the user's
  config.toml (hooks, notify, `caveman` provider, xhigh effort), so `CodexCliProtocol` now passes `--ignore-user-config`
  (login still from CODEX_HOME; `codexCli.model` picks the model). Live test: Codex 25.3 s -> 7.6 s, still on target.

- T5.8 full run (2026-11-05): live test now drives `TypedScreenQuestionRunner.AnswerAsync` (Run = input box + AnswerAsync) per
  backend: turn 1 "remember PELICAN + point at the orange box" -> reply without tag, Processing -> Idle, overlay pointing, bubble
  shown, [POINT] on the box; Show Clicky off -> overlay still up at 5 s, hides after; turn 2 recalls PELICAN from history.
  Claude Code CLI pass (5.9 s), Codex CLI pass (9.6 s). Worker: not set up -> error bubble shown (expected). Router: Worker
  failing -> fell back to Claude. App launch smoke OK (403 MB working set). Not automatable: seeing the overlay (capture-excluded).

- E6 (2026-11-05): `Voice/` — `MicrophoneRecorder` (NAudio.WinMM 2.2.1 WaveIn, 16 kHz mono PCM16; NAudio 3.x needs net9),
  `SpeechClip` (pure: 0.3 s minimum, -40 dBFS silence, all-zero = blocked mic, pad to 1.1 s, drop [BLANK_AUDIO]/(noise) markers,
  Mac level formula), `WhisperModelFile` (own HTTP download from huggingface ggerganov/whisper.cpp for real % progress, .part then
  rename, model name validated), `WhisperLocalTranscriber` + `SpeechToTextTranscriberFactory` (Whisper only), `MicrophonePrivacy`
  (ConsentStore keys), `PushToTalkDictation` (replaces App.ApplyPushToTalkTransition). Panel got a voice-note line (transcript,
  download %, mic errors). T6.2.4 measured with `LiveWhisperTranscriptionTests` (JFK 11 s sample; 3 s slice): CPU base.en 3.9-5.0 s
  (3 s clip 4.0 s), small.en 18-26 s; Vulkan base.en 0.45-0.59 s (3 s clip 0.36 s), small.en 1.3-1.8 s (3 s clip 1.0 s), first
  Vulkan decode 11.5 s for small.en -> warm-up decode on silence after load. Decision: ship Whisper.net.Runtime.Vulkan (CPU fallback
  is automatic). Publish grows to ~116 MB.

- E6 verification (2026-11-05): tests for every `Voice/` module, 100% line + branch coverage (coverlet), 261/261 green over
  12 repeated runs. Seams added: `MicrophoneRecorder(createWaveIn, getInputDeviceCount)`, `MicrophonePrivacy.GetBlockedMessage(roots)`,
  `WhisperModelFile.EnsureDownloadedAsync(..., httpClient, folder)`, `WhisperLocalTranscriber(ensureModelDownloadedAsync)`,
  `PushToTalkDictation(..., microphoneRecorder)` whose handler now returns a Task. Real-Whisper tests use offline System.Speech audio
  (Whisper hallucinates "you" on pure silence; the app never sends that, the -40 dBFS gate rejects it). Hardened the old flaky
  `OverlayWindow_IsAbsentFromCaptures_AndPresentWhenExclusionIsLifted` (failed ~1 in 6 with or without E6 tests: one 300 ms wait for
  composition) to retry up to ~3 s. Live app: debug build + injected Ctrl+Alt while playing speech through the speakers ->
  "Transcribed 3.3 s in 419 ms". Published exe: bundled natives failed ("Native Library not found in default paths"), so the csproj
  now publishes them to `runtimes\` beside the exe (78 MB exe + ~62 MB natives); then "Please open the settings menu." in 589 ms,
  load + warm-up 1.6 s on Vulkan; with `runtimes\` hidden, push-to-talk reports "Couldn't transcribe: ... failed to load" (no hang).
  Load failures are now also written to the log.

- E7 (2026-11-05): `Voice/KokoroLocalSpeaker.cs` (`ITextToSpeechSpeaker` + factory, Kokoro only), `Voice/WindowsBuiltInSpeaker.cs`
  (T7.5 fallback: WinRT SpeechSynthesizer -> WAV -> NAudio WaveOutEvent, no new package), `Voice/ModelFileDownload.cs` (download loop
  moved out of `WhisperModelFile`; KokoroSharp's own downloader writes `kokoro.onnx` to the current directory, so ours puts
  `kokoro-v1.0.onnx` in %LOCALAPPDATA%\Clicky\models). Config: `textToSpeech` default `kokoro-local`, new `kokoroVoice` (`af_heart`),
  `edge-tts` kept as a legacy value (the user's file has it; an unknown enum would reject the whole file), `edgeTtsVoice` dropped.
  Packages: KokoroSharp.CPU 0.8.4 + KokoroSharp 0.8.4 directly (`$(PkgKokoroSharp)` for the voices; its copy target excluded).
  Voices: top-level v1.0 `.npy` files copied to `voices\` beside the exe (build and publish, not bundled). Tray "Speak test line";
  push-to-talk press stops speech. Publish: Clicky.exe 78 -> 147 MB (MisakiSharp.dll 66 MB of English G2P dictionaries + ONNX
  Runtime 12 MB) + voices\ 28 MB; `.lib` files dropped from publish. Published exe: "Kokoro loaded and warmed up in 7746 ms"
  (alongside Whisper small.en), working set ~1.48 GB with both models loaded.
- T7.3 decision (2026-11-05, Core Ultra 5 125H / Arc): **CPU**. DirectML (KokoroSharp.DirectML 0.8.4, ORT 1.22) throws in
  `/N.1/pool/ConvTranspose` ("The parameter is incorrect") on the Arc GPU. CPU, Release scratch benchmark, 5.2 s reply: RTF 0.40-0.48
  for 4/8/14 intra-op threads (no difference) and default; first sound with `SpeakFast`: cold 3.5-5.6 s (+0.5-1.3 s first tokenize),
  warm ~2 s at KokoroSharp's default first segment (200 tokens), 1.3-1.6 s at 60, **0.77-0.90 s at 30** (whole reply ~1 s longer).
  So: first segment capped at 30 tokens, plus one silent warm-up synthesis at load. EcoQoS (power throttling) forced on: first
  sound 1.1-1.3 s vs 0.8-0.9 s. Under `dotnet test` the same code measured RTF 0.8-2.1 and 2.6-8 s first sound (same onnxruntime.dll
  1.22 loaded; cause not found) — the app's own "Kokoro first sound after N ms" log line is the number to trust (T7.6).
  Interrupt: `SpeakAsync` returns 0 ms after `StopSpeaking` (live test), Windows voice stops on cancel.

- E7 hardening (2026-11-06): `KokoroLocalSpeaker` no longer uses KokoroSharp's `KokoroTTS.SpeakFast`. Its source showed
  `KokoroJob.Progress` calls `model.Infer` on KokoroSharp's own thread with no try/catch (an ONNX error would end Clicky) and
  speech completion is callback-only (a mid-reply failure left `SpeakAsync` hanging). Now: `KokoroModel.Infer` per chunk in our
  task (behind `KokoroInference`, one run at a time), segmentation/pauses/post-processing reused from KokoroSharp, playback via NAudio
  `BufferedWaveProvider` + `WaveOutEvent` with 0.5 s trailing silence so the end isn't clipped. Error before the first sound ->
  Windows voice says the reply; mid-reply -> stops. Stop returns at once (live: 33 ms, was >2 s waiting for the ONNX run). 7 new
  tests with a fake model + fake sound output (stable over 8-10 repeats), 289/289 green; live test passes.
  Config: `TextToSpeechProviderKind.EdgeTts` removed; `ClickyConfigurationStore` migrates `"edge-tts"` -> `"kokoro-local"` on load and
  saves (an unknown value is still a load error, file untouched). Published exe run: migrated the user's file (routing kept),
  "Kokoro loaded and warmed up in 6227 ms".

- Kokoro load retry (2026-11-06): a failed load (download or model file) is retried automatically after 30 s, 2 min, then every
  10 min (status: "Voice model failed to load: ... Trying again in 30 s."); a config save retries at once. A model that loads but
  fails its warm-up is released before the retry. Replies use the Windows voice meanwhile. 6 new tests (295/295, stable over 8
  repeats). Live: model hidden + dead proxy -> "Kokoro failed to load", model restored, loaded by the 30 s retry.

## Tracker sync pending

(none; E7 T7.1, T7.1.1, T7.1.2, T7.2, T7.3, T7.5 synced to `testing` on 2026-11-05)

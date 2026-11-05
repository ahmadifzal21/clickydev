# Agent handoff — Clicky Windows port

RELAY-STATUS: complete

<!-- The relay (agent-relay/relay.ps1) stops when the line above reads `RELAY-STATUS: complete`.
     Set it to complete only when every ticket in "Scope" is committed. -->

## Scope

Epic **E2 — Cursor overlay and pointing** (see `PORTING-PLAN.md` step 2 and "Coordinates").
Checkpoint: the blue cursor follows the mouse on every monitor; tray "Test pointing" flies to
known spots on each monitor at the user's scaling settings.
Port from the Mac: `leanring-buddy/OverlayWindow.swift` (BlueCursorView look, mouse offset, bezier
arc flight, bubble) and `CompanionResponseOverlay.swift` (read only — never edit `leanring-buddy/`).

Tickets, in order:
T2.1 Monitor enumeration (physical bounds + DPI per monitor; rebuild overlays on display/DPI change) ·
T2.2 `Overlay/CursorOverlayWindow`, one per monitor (transparent, click-through, toolwindow,
no-activate, topmost above taskbar/fullscreen, SetWindowPos in physical px, WDA_EXCLUDEFROMCAPTURE) ·
T2.3 Blue triangle cursor (BlueCursorView look, follows mouse every frame with the Mac offset,
drawn only on the monitor containing the mouse) ·
T2.4 Pointing flight (bezier arc, speech bubble with label, hold, fly back, clear) ·
T2.5 Coordinate mapper screenshot px → global px → overlay DIPs (+ xUnit tests: mixed DPI,
negative-origin monitors, clamping) ·
T2.6 Response bubble, waveform and spinner states ·
T2.7 Fade in/out + transient cursor mode hooks (`IsClickyCursorAlwaysVisible`; replace the
temporary status wiring in `App.xaml.cs`) ·
T2.8 Tray "Test pointing" debug item.
T2.9 (verify on the user's machine) is the user's job — never mark it.

## Rules for whichever agent is working

1. Work one ticket at a time, in order. Before starting, read this file, `PORTING-PLAN.md`, and
   `git log --oneline -10` / `git status` (a previous agent may have left uncommitted work).
2. Code style: follow the existing files in `Clicky.Windows/` (long descriptive names, XML doc
   comments explaining *why*, WinForms types fully qualified — implicit WinForms usings are off).
3. Proof per ticket: `dotnet build Clicky.Windows.sln -c Debug` and `dotnet test` from `windows/`
   must pass. Add xUnit tests for any pure logic (e.g. panel positioning math).
4. When a ticket is done: append a line to "Ticket log" and to "Tracker sync pending" below, and
   update "Current ticket". Then commit on branch `windows-port` (message `E2 T2.x: <what>`, no push).
   **Codex** can't write `.git` (sandbox): instead write the full commit message to
   `windows/agent-relay/commit-message.txt` and **exit** — the relay commits everything under
   `windows/` plus `AGENTS.md` and starts you again for the next ticket. One ticket per run.
5. Mid-ticket, if you sense you are running low on context or quota, stop coding, write what is
   half-done under "Current ticket" (files touched, what remains), and exit. Don't commit broken
   builds; leave the work uncommitted and say so.
6. Tracker: https://claude.ai/artifact/MDLM6aQJTQdjsDWwsPb62X, collection `epics`, doc `E2`.
   Claude: if the ArtifactData tool is available, apply every "Tracker sync pending" entry
   (status `testing` for code-complete tasks/subtasks; pin `if_version`), then clear the list.
   Codex: you can't reach the tracker; just append entries there.
7. Never push, never touch `leanring-buddy/` or `worker/`.

## Current ticket

None — T2.1-T2.8 are committed. T2.9 is the user's manual check: with "Show Clicky" on and off, use tray
"Test pointing" on every monitor at your real scaling (mixed DPI, a monitor left of / above the primary) and confirm
the buddy lands just right of and below each spot, the label bubble reads e.g. "screen2 centre", and it flies back.

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

## Tracker sync pending

- E2 T2.8 -> testing

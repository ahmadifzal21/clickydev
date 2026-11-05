# Agent handoff — Clicky Windows port

RELAY-STATUS: active

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
4. When a ticket is done: commit on branch `windows-port` (message `E2 T2.x: <what>`, no push),
   then append a line to "Ticket log" and to "Tracker sync pending" below, and update
   "Current ticket". Do this **before** starting the next ticket so a cut-off loses little.
5. Mid-ticket, if you sense you are running low on context or quota, stop coding, write what is
   half-done under "Current ticket" (files touched, what remains), and exit. Don't commit broken
   builds; leave the work uncommitted and say so.
6. Tracker: https://claude.ai/artifact/MDLM6aQJTQdjsDWwsPb62X, collection `epics`, doc `E2`.
   Claude: if the ArtifactData tool is available, apply every "Tracker sync pending" entry
   (status `testing` for code-complete tasks/subtasks; pin `if_version`), then clear the list.
   Codex: you can't reach the tracker; just append entries there.
7. Never push, never touch `leanring-buddy/` or `worker/`.

## Current ticket

T2.2 CursorOverlayWindow — not started. T2.2 must subscribe to `DisplayMonitorCatalog.MonitorsChanged`
to rebuild its windows and call `RequestRefresh` on WM_DPICHANGED.

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

## Tracker sync pending

- (none)

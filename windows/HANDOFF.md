# Agent handoff — Clicky Windows port

RELAY-STATUS: active

<!-- The relay (agent-relay/relay.ps1) stops when the line above reads `RELAY-STATUS: complete`.
     Set it to complete only when every ticket in "Scope" is committed. -->

## Scope

Epic **E1 — Tray icon and floating panel** (see `PORTING-PLAN.md` step 1).
Checkpoint: left-click the tray icon → dark panel opens near the tray, closes on outside click;
changing a backend picker updates `clicky.config.json`.

Tickets, in order: T1.1 TrayIconManager · T1.2 CompanionPanelWindow shell · T1.3 Panel content ·
T1.4 Backend routing pickers · T1.5 Replace placeholder tray code in App.xaml.cs + update AGENTS.md.
T1.6 (verify on the user's machine) is the user's job — never mark it.

## Rules for whichever agent is working

1. Work one ticket at a time, in order. Before starting, read this file, `PORTING-PLAN.md`, and
   `git log --oneline -10` / `git status` (a previous agent may have left uncommitted work).
2. Code style: follow the existing files in `Clicky.Windows/` (long descriptive names, XML doc
   comments explaining *why*, WinForms types fully qualified — implicit WinForms usings are off).
3. Proof per ticket: `dotnet build Clicky.Windows.sln -c Debug` and `dotnet test` from `windows/`
   must pass. Add xUnit tests for any pure logic (e.g. panel positioning math).
4. When a ticket is done: commit on branch `windows-port` (message `E1 T1.x: <what>`, no push),
   then append a line to "Ticket log" and to "Tracker sync pending" below, and update
   "Current ticket". Do this **before** starting the next ticket so a cut-off loses little.
5. Mid-ticket, if you sense you are running low on context or quota, stop coding, write what is
   half-done under "Current ticket" (files touched, what remains), and exit. Don't commit broken
   builds; leave the work uncommitted and say so.
6. Tracker: https://claude.ai/artifact/MDLM6aQJTQdjsDWwsPb62X, collection `epics`, doc `E1`.
   Claude: if the ArtifactData tool is available, apply every "Tracker sync pending" entry
   (status `testing` for code-complete tasks/subtasks; pin `if_version`), then clear the list.
   Codex: you can't reach the tracker; just append entries there.
7. Never push, never touch `leanring-buddy/` or `worker/`.

## Current ticket

T1.2 — CompanionPanelWindow shell (not started).

## Ticket log (newest last)

- T1.1 done: `Tray/TrayIconManager.cs` — NotifyIcon with a code-drawn blue cursor icon (white
  outline), `TrayIconLeftClicked(Point physicalCursorPos)` event, context menu passed in by the
  caller, hide+dispose+DestroyIcon on Dispose. Not wired into App yet (T1.5 does that).

## Tracker sync pending

- (none)

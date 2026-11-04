# Claude Code StopFailure hook (matcher: rate_limit). When an interactive Claude session stops
# because it ran out of usage, start the relay detached with Codex so the HANDOFF.md work keeps
# going. relay.ps1 exits immediately if one is already running or the work is complete.
$ErrorActionPreference = 'SilentlyContinue'
$null = [Console]::In.ReadToEnd()  # hook JSON on stdin; not needed
$handoffPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'HANDOFF.md'
if ((Get-Content $handoffPath -Raw) -match 'RELAY-STATUS:\s*complete') { exit 0 }
$relayScript = Join-Path $PSScriptRoot 'relay.ps1'
Start-Process powershell -WindowStyle Minimized -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$relayScript`"", '-StartWith', 'codex')
exit 0

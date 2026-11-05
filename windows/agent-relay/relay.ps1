<#
.SYNOPSIS
  Keeps the work in windows/HANDOFF.md going by alternating Claude Code and Codex CLI.

.DESCRIPTION
  Runs one headless agent at a time. Each run reads HANDOFF.md, does tickets, and updates the
  file after every ticket. When a run ends because the agent hit its usage/rate limit, the relay
  hands over to the other agent. If both are limited back to back it sleeps and retries.
  Stops when HANDOFF.md says `RELAY-STATUS: complete` or after -MaxRuns runs.

  Started automatically by on-claude-rate-limit.ps1 (Claude Code StopFailure hook), or by hand:
    powershell -ExecutionPolicy Bypass -File windows\agent-relay\relay.ps1 -StartWith codex
#>
param(
    [ValidateSet('claude', 'codex')] [string] $StartWith = 'codex',
    [int] $MaxRuns = 30,
    [int] $BothLimitedSleepMinutes = 20,
    # Codex stays sandboxed: it may write only inside the repo plus the NuGet package cache
    # (which `dotnet restore` needs), with network on for package restore. Full access is
    # deliberately not offered; run the relay in a VM/container if you need more.
    [ValidateSet('read-only', 'workspace-write')] [string] $CodexSandbox = 'workspace-write'
)

$ErrorActionPreference = 'Stop'
$windowsDir = Split-Path -Parent $PSScriptRoot
$repoDir = Split-Path -Parent $windowsDir
$handoffPath = Join-Path $windowsDir 'HANDOFF.md'
$logDir = Join-Path $PSScriptRoot 'logs'
$lockPath = Join-Path $PSScriptRoot 'relay.lock'
New-Item -ItemType Directory -Force $logDir | Out-Null

# One relay at a time: the hook fires on every rate-limited Claude stop, including the relay's own.
if (Test-Path $lockPath) {
    $lockPid = [int](Get-Content $lockPath -ErrorAction SilentlyContinue)
    if ($lockPid -and (Get-Process -Id $lockPid -ErrorAction SilentlyContinue)) { exit 0 }
}
Set-Content $lockPath $PID

# Matched against the tail of an agent's output only, so code that mentions "rate limit" doesn't count.
$limitPattern = 'usage limit|hit your limit|limit reached|rate[ _-]?limit|quota|too many requests|\b429\b|resets? (at|in)'

$prompt = @"
You are continuing autonomous work on the Clicky Windows port in $windowsDir.
Read windows/HANDOFF.md first and follow its "Rules for whichever agent is working" exactly.
Pick up at "Current ticket" (check git status for uncommitted work from the previous agent),
finish tickets one at a time, and update HANDOFF.md after each one. If you are running low on
context or usage quota (about 10% left), stop at a safe point, write the hand-off notes into
HANDOFF.md, and exit so the other agent can take over. When every ticket in Scope is committed,
set RELAY-STATUS: complete.
"@

function Test-RelayComplete { (Get-Content $handoffPath -Raw) -match '(?m)^RELAY-STATUS:\s*complete' }  # line start: the file's comment also mentions it

# Appends each output line to the run's log as it arrives (tail it with Get-Content -Wait) and passes it on.
# Not Tee-Object: in PowerShell 5.1 it writes UTF-16. claude -p in text mode prints only at the end.
function Write-LiveLog([string] $logPath) {
    process { $line = "$_"; Add-Content -Path $logPath -Value $line -Encoding UTF8; $line }
}

function Invoke-Agent([string] $agent, [string] $logPath) {
    Push-Location $repoDir
    # Agent stderr is normal output, not a failure; under 'Stop' PowerShell 5.1 also hangs piping into codex.ps1.
    $ErrorActionPreference = 'Continue'
    try {
        if ($agent -eq 'claude') {
            $output = & claude -p $prompt --permission-mode auto 2>&1 | Write-LiveLog $logPath
        } else {
            $nugetCacheDir = (Join-Path $env:USERPROFILE '.nuget\packages') -replace '\\', '/'
            # TOML literal string ('...'): Windows PowerShell 5.1 strips embedded double quotes
            # from native-command arguments, which makes Codex reject the config and exit.
            $output = $prompt | & codex exec --sandbox $CodexSandbox -C $repoDir `
                -c "sandbox_workspace_write.writable_roots=['$nugetCacheDir']" `
                -c 'sandbox_workspace_write.network_access=true' - 2>&1 | Write-LiveLog $logPath
        }
        $exitCode = $LASTEXITCODE
    } finally { Pop-Location }
    $tail = ($output | Select-Object -Last 40) -join "`n"
    [pscustomobject]@{ ExitCode = $exitCode; HitLimit = ($tail -match $limitPattern) }
}

try {
    $agent = $StartWith
    $consecutiveLimited = 0
    for ($run = 1; $run -le $MaxRuns; $run++) {
        if (Test-RelayComplete) { Write-Host 'HANDOFF.md says complete; relay done.'; break }

        $logPath = Join-Path $logDir ('{0:yyyyMMdd-HHmmss}-{1}.log' -f (Get-Date), $agent)
        Write-Host "[$(Get-Date -Format T)] run $run -> $agent (log: $logPath)"
        $result = Invoke-Agent $agent $logPath

        # Codex's sandbox keeps .git read-only, so it leaves its commit message here and the relay commits.
        $commitMessagePath = Join-Path $PSScriptRoot 'commit-message.txt'
        if (Test-Path $commitMessagePath) {
            & git -C $repoDir add -A -- windows AGENTS.md
            & git -C $repoDir commit -q -F $commitMessagePath
            if ($LASTEXITCODE -eq 0) { Remove-Item $commitMessagePath } else { Write-Host 'Relay commit failed; leaving commit-message.txt.' }
        }

        if ($result.HitLimit) {
            $consecutiveLimited++
            if ($consecutiveLimited -ge 2) {
                Write-Host "Both agents limited; sleeping $BothLimitedSleepMinutes min."
                Start-Sleep -Seconds ($BothLimitedSleepMinutes * 60)
                $consecutiveLimited = 0
            }
            $agent = if ($agent -eq 'claude') { 'codex' } else { 'claude' }
        } elseif ($result.ExitCode -ne 0) {
            # Crash or unknown failure: let the other agent try rather than looping on the same error.
            $consecutiveLimited = 0
            $agent = if ($agent -eq 'claude') { 'codex' } else { 'claude' }
        } else {
            $consecutiveLimited = 0  # clean exit: same agent continues with the next ticket
        }
    }
} finally {
    Remove-Item $lockPath -ErrorAction SilentlyContinue
}

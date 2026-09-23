$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Files = @(
    "CURRENT_HANDOFF.md",
    "PROJECT_STATE.md",
    "tasks/TASK_QUEUE.md",
    "tasks/FINAL_PRODUCT_AUDIT_FIX_QUEUE.md"
)

$Texts = @{}
foreach ($File in $Files) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "Missing state file: $File"
    }
    $Texts[$File] = Get-Content -LiteralPath $File -Raw
}

$Combined = ($Files | ForEach-Object { $Texts[$_] }) -join "`n"

$ForbiddenHeadPatterns = @(
    '(?m)^HEAD:\s*[0-9a-f]{7,40}\s*$',
    '(?m)^Current branch HEAD:\s*[0-9a-f]{7,40}\s*$',
    '(?m)^Baseline main HEAD:\s*[0-9a-f]{7,40}\s*$'
)
foreach ($Pattern in $ForbiddenHeadPatterns) {
    if ([regex]::IsMatch($Combined, $Pattern)) {
        throw "State files contain a self-invalidating hardcoded Git HEAD marker: $Pattern"
    }
}

$ActualBranch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ActualBranch)) {
    throw "Could not resolve the current Git branch."
}

$Tick = [char]96
$ExpectedProjectBranch = "Active branch: " + $Tick + $ActualBranch + $Tick
$ProjectText = [string]$Texts["PROJECT_STATE.md"]
if ($ProjectText.IndexOf($ExpectedProjectBranch, [StringComparison]::Ordinal) -lt 0) {
    throw "PROJECT_STATE.md does not match the current Git branch '$ActualBranch'."
}

$ExpectedHandoffBranch = "Branch: " + $Tick + $ActualBranch + $Tick
$HandoffText = [string]$Texts["CURRENT_HANDOFF.md"]
if ($HandoffText.IndexOf($ExpectedHandoffBranch, [StringComparison]::Ordinal) -lt 0) {
    throw "CURRENT_HANDOFF.md does not match the current Git branch '$ActualBranch'."
}

$NextMatches = [regex]::Matches($Combined, '(?m)^AUTHORITATIVE NEXT_EXACT_ACTION:')
if ($NextMatches.Count -ne 1) {
    throw "Expected exactly one AUTHORITATIVE NEXT_EXACT_ACTION across state files, found $($NextMatches.Count)."
}

$HistoricalQueue = [string]$Texts["tasks/TASK_QUEUE.md"]
if ([regex]::IsMatch($HistoricalQueue, '(?m)^NEXT_EXACT_ACTION:')) {
    throw "Historical OBS task queue must not advertise a current NEXT_EXACT_ACTION."
}

$FinalQueue = [string]$Texts["tasks/FINAL_PRODUCT_AUDIT_FIX_QUEUE.md"]
if ($FinalQueue.IndexOf("AUTHORITATIVE NEXT_EXACT_ACTION:", [StringComparison]::Ordinal) -lt 0) {
    throw "Final-product queue must own the authoritative next action."
}

Write-Host "project-state-contract: ok (branch=$ActualBranch, authoritative-next=1)"

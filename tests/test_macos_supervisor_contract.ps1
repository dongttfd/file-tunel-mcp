$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Supervisor = "macos/TunnelSupervisor.swift"
if (-not (Test-Path -LiteralPath $Supervisor -PathType Leaf)) {
    throw "Missing macOS tunnel supervisor source."
}

$CompileFiles = @(
    "build_macos_app.sh",
    "run_macos_dev.sh",
    ".github/workflows/verify.yml",
    "tests/test_swift_runtime.sh"
)
foreach ($File in $CompileFiles) {
    $Text = Get-Content -LiteralPath $File -Raw
    if ($Text.IndexOf("TunnelSupervisor.swift", [StringComparison]::Ordinal) -lt 0) {
        throw "$File does not compile/wire TunnelSupervisor.swift."
    }
}

$Runtime = Get-Content "macos/LocalMCPRuntime.swift" -Raw
$RequiredRuntimeMarkers = @(
    "case restarting(String)",
    "case cooldown(String)",
    "TunnelRestartPolicy",
    "restartScheduleGeneration",
    "guard generation == tunnelGeneration",
    "scheduleRestart(",
    "cancelPendingRestart()"
)
foreach ($Marker in $RequiredRuntimeMarkers) {
    if ($Runtime.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "macOS runtime is missing supervisor marker: $Marker"
    }
}

$ExitStart = $Runtime.IndexOf("private func tunnelDidExit", [StringComparison]::Ordinal)
$ScheduleStart = $Runtime.IndexOf("private func scheduleRestart", [StringComparison]::Ordinal)
if ($ExitStart -lt 0 -or $ScheduleStart -le $ExitStart) {
    throw "Could not locate tunnelDidExit/scheduleRestart lifecycle region."
}
$ExitRegion = $Runtime.Substring($ExitStart, $ScheduleStart - $ExitStart)
foreach ($Forbidden in @("server?.stop()", "profileLock?.release()")) {
    if ($ExitRegion.IndexOf($Forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw "Unexpected full-runtime teardown inside tunnelDidExit: $Forbidden"
    }
}

$App = Get-Content "macos/FileMCPApp.swift" -Raw
foreach ($Marker in @(".restarting", ".cooldown")) {
    if ($App.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "macOS app UI is missing runtime state handling: $Marker"
    }
}

$SwiftTests = Get-Content "tests/test_swift_runtime.sh" -Raw
foreach ($Marker in @(
    "tunnel-supervisor-policy: ok",
    "runtime-tunnel-auto-restart: ok",
    "runtime-tunnel-restart-cancel: ok"
    "runtime-tunnel-restart-cooldown: ok"
)) {
    if ($SwiftTests.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Swift runtime suite is missing supervisor acceptance marker: $Marker"
    }
}

Write-Host "macos-supervisor-contract: ok"

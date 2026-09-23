$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Workflow = Get-Content ".github/workflows/verify.yml" -Raw
$Smoke = Get-Content "tests/test_windows_app.ps1" -Raw

$RequiredWorkflowMarkers = @(
    "verify-windows-arm64:",
    "runs-on: windows-11-vs2026-arm",
    '[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()',
    'if ($architecture -ne "Arm64")',
    'vendor/tunnel-client/windows-arm64/tunnel-client.exe --version',
    'build_windows_app.ps1 -Architecture arm64 -StagingName $env:FILEMCP_WINDOWS_STAGING_NAME',
    'test_windows_app.ps1 -Architecture arm64',
    'dist/windows-arm64/$env:FILEMCP_WINDOWS_STAGING_NAME',
    "name: FileMCP-windows-arm64",
    "path: dist/FileMCP-v0.4.0-windows-arm64.zip"
)

foreach ($Marker in $RequiredWorkflowMarkers) {
    if ($Workflow.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Windows ARM64 workflow assurance marker missing: $Marker"
    }
}

$ArmJobIndex = $Workflow.IndexOf("  verify-windows-arm64:", [StringComparison]::Ordinal)
if ($ArmJobIndex -lt 0) { throw "Native Windows ARM64 job not found." }
$ArmJob = $Workflow.Substring($ArmJobIndex)

if ([regex]::Matches($Workflow, '(?m)^\s*name:\s*FileMCP-windows-arm64\s*$').Count -ne 1) {
    throw "ARM64 artifact upload must be owned by exactly one job."
}

$RequiredSmokeMarkers = @(
    '[ValidateSet("x64", "arm64")]',
    '[string]$Architecture = "x64"',
    'dist/FileMCP-v0.4.0-windows-$Architecture.zip',
    'windows-app-startup-${Architecture}: ok',
    'windows-close-to-tray-${Architecture}: ok',
    'windows-packaged-tunnel-client-${Architecture}: ok'
)
foreach ($Marker in $RequiredSmokeMarkers) {
    if ($Smoke.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Parameterized Windows app smoke marker missing: $Marker"
    }
}

if ($ArmJob.IndexOf('OpenTelemetry-THIRD-PARTY-NOTICES.txt', [StringComparison]::Ordinal) -lt 0) {
    throw "Native ARM64 resource verification must include OpenTelemetry redistribution notices."
}

Write-Host "windows-arm64-assurance-contract: ok (runner=windows-11-vs2026-arm)"

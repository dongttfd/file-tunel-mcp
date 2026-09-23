$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Coordinator = Get-Content "windows/src/FileMCP.Core/DesktopSingleInstanceCoordinator.cs" -Raw
$App = Get-Content "windows/src/FileMCP.App/App.xaml.cs" -Raw
$Window = Get-Content "windows/src/FileMCP.App/MainWindow.xaml.cs" -Raw
$Smoke = Get-Content "tests/test_windows_app.ps1" -Raw
$CoreTests = Get-Content "windows/tests/FileMCP.Core.Tests/Program.cs" -Raw

$CoordinatorMarkers = @(
    "PipeOptions.FirstPipeInstance",
    "PipeOptions.CurrentUserOnly",
    "MaxCommandBytes = 64",
    "SignalPrimaryAsync",
    "StartActivationListener",
    "WindowsIdentity.GetCurrent().User",
    "SHA256.HashData"
)
foreach ($Marker in $CoordinatorMarkers) {
    if ($Coordinator.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { throw "Single-instance coordinator marker missing: $Marker" }
}

$AppMarkers = @(
    'new DesktopSingleInstanceCoordinator("FileMCP.Desktop.v1")',
    '_singleInstance.SignalPrimaryAsync(TimeSpan.FromSeconds(2))',
    '_singleInstance.StartActivationListener',
    '_window?.ActivateFromSecondaryLaunch()'
)
foreach ($Marker in $AppMarkers) {
    if ($App.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { throw "App single-instance wiring marker missing: $Marker" }
}

foreach ($Marker in @("ActivateFromSecondaryLaunch", "FILEMCP_SINGLE_INSTANCE_SMOKE_MARKER")) {
    if ($Window.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { throw "MainWindow activation marker missing: $Marker" }
}

$SmokeMarkers = @(
    '$SecondaryProcess = Start-Process -FilePath $Exe -PassThru',
    "Primary FileMCP instance did not receive the second-launch activation signal.",
    'windows-single-instance-activation-${Architecture}: ok'
)
foreach ($Marker in $SmokeMarkers) {
    if ($Smoke.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { throw "Packaged single-instance smoke marker missing: $Marker" }
}

if ($CoreTests.IndexOf("windows-desktop-single-instance: ok", [StringComparison]::Ordinal) -lt 0) {
    throw "Core single-instance test marker missing."
}

Write-Host "windows-single-instance-contract: ok"

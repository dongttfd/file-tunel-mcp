$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Runtime = Get-Content "windows/src/FileMCP.Core/LocalMcpRuntime.cs" -Raw
$Tests = Get-Content "windows/tests/FileMCP.Core.Tests/Program.cs" -Raw
$Ui = Get-Content "windows/src/FileMCP.App/MainWindow.xaml.cs" -Raw

foreach ($Marker in @(
    '"--health.url-file", healthUrlFilePath',
    'TryReadResolvedHealthEndpoint',
    'PrepareHealthUrlFileForLaunch',
    'BestEffortDeleteHealthUrlFile',
    '_resolvedHealthAddress'
)) {
    if ($Runtime.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { throw "Dynamic health runtime marker missing: $Marker" }
}

foreach ($Marker in @(
    'dynamic :0 tunnel health resolves and probes reachable endpoint',
    'stale health URL file is replaced by current tunnel launch',
    'dynamic health URL file is removed on runtime stop',
    'MCP_TEST_HEALTH_REQUIRE_FRESH',
    '--health.url-file'
)) {
    if ($Tests.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) { throw "Dynamic health test marker missing: $Marker" }
}

if ($Ui.IndexOf('dynamic/pending', [StringComparison]::Ordinal) -lt 0) { throw 'Dynamic health UI state marker missing.' }

Write-Host 'dynamic-health-discovery-contract: ok (official tunnel-client health.url-file)'

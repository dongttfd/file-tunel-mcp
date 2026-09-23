$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$Windows = Get-Content "windows/src/FileMCP.Core/LocalMcpServer.cs" -Raw
$Mac = Get-Content "macos/LocalMCPServer.swift" -Raw
$SwiftTests = Get-Content "tests/test_swift_runtime.sh" -Raw
$WindowsTests = Get-Content "windows/tests/FileMCP.Core.Tests/Program.cs" -Raw

$WindowsMarkers = @(
    "LocalMcpServerLimits",
    "new(64, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30))",
    "SemaphoreSlim _connectionSlots",
    "_connectionSlots.Wait(0)",
    "HeaderReadTimeout",
    "ReadIdleTimeout",
    "Stopwatch.GetElapsedTime(headerStartedAt)"
)
foreach ($Marker in $WindowsMarkers) {
    if ($Windows.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Windows MCP connection-bounds marker missing: $Marker"
    }
}

$MacMarkers = @(
    "LocalMCPServerLimits",
    "maxConcurrentConnections: 64",
    "readIdleTimeout: 15",
    "headerReadTimeout: 30",
    "MCPConnectionLease",
    "DispatchSemaphore",
    "connectionSlots.wait(timeout: .now())",
    "headerDeadline"
)
foreach ($Marker in $MacMarkers) {
    if ($Mac.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "macOS MCP connection-bounds marker missing: $Marker"
    }
}

foreach ($Marker in @(
    "windows-http-connection-bounds: ok",
    "HTTP connection cap rejects excess client",
    "HTTP idle connection times out",
    "HTTP absolute header deadline stops trickle client"
)) {
    if ($WindowsTests.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Windows bounds test marker missing: $Marker"
    }
}

foreach ($Marker in @(
    "macos-http-connection-bounds: ok",
    "macOS connection cap did not reject excess client",
    "macOS idle connection did not time out",
    "macOS absolute header deadline did not stop trickle client"
)) {
    if ($SwiftTests.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "macOS bounds test marker missing: $Marker"
    }
}

Write-Host "http-connection-bounds-contract: ok (max=64 idle=15s header=30s)"

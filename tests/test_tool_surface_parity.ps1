$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$WindowsSource = @(
    Get-Content "windows/src/FileMCP.Core/LocalTools.cs" -Raw
    Get-Content "windows/src/FileMCP.Core/CodexSkillRegistry.cs" -Raw
    Get-Content "windows/src/FileMCP.Core/LocalMcpServer.cs" -Raw
) -join "`n"

$MacSource = Get-Content "macos/LocalMCPServer.swift" -Raw

$RequiredTools = @(
    "list_files",
    "read_file",
    "read_file_range",
    "search_content",
    "search_filenames",
    "write_file",
    "delete_file",
    "delete_directory",
    "git_init",
    "git_status",
    "git_log",
    "git_diff",
    "git_add",
    "git_commit",
    "git_push",
    "run_command",
    "list_codex_skills",
    "load_codex_skill",
    "filemcp_observability_connect"
)

foreach ($Tool in $RequiredTools) {
    $Quoted = '"' + $Tool + '"'
    if ($WindowsSource.IndexOf($Quoted, [StringComparison]::Ordinal) -lt 0) {
        throw "Windows source is missing public tool name '$Tool'."
    }
    if ($MacSource.IndexOf($Quoted, [StringComparison]::Ordinal) -lt 0) {
        throw "macOS source is missing public tool name '$Tool'."
    }
}

if ($WindowsSource.IndexOf('result.Add(ObservabilityConnectToolDefinition())', [StringComparison]::Ordinal) -lt 0) {
    throw "Windows tools/list wiring is missing observability connect."
}
if ($MacSource.IndexOf('result.append(observabilityConnectToolDefinition())', [StringComparison]::Ordinal) -lt 0) {
    throw "macOS tools/list wiring is missing observability connect."
}
foreach ($Source in @($WindowsSource, $MacSource)) {
    if ($Source.IndexOf('"_filemcp_chat"', [StringComparison]::Ordinal) -lt 0) {
        throw "A platform is missing the reserved _filemcp_chat facade."
    }
}

Write-Host "tool-surface-parity: ok (required=$($RequiredTools.Count))"

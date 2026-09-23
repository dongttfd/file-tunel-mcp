$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$BuildScript = Get-Content "build_windows_app.ps1" -Raw
$Workflow = Get-Content ".github/workflows/verify.yml" -Raw

$DefaultMatch = [regex]::Match($BuildScript, '\[string\]\$StagingName\s*=\s*"([^"]+)"')
if (-not $DefaultMatch.Success) {
    throw "Could not find the canonical Windows staging default in build_windows_app.ps1."
}
$DefaultStaging = $DefaultMatch.Groups[1].Value

$WorkflowMatch = [regex]::Match($Workflow, '(?m)^\s*FILEMCP_WINDOWS_STAGING_NAME:\s*([A-Za-z0-9._-]+)\s*$')
if (-not $WorkflowMatch.Success) {
    throw "GitHub workflow does not declare FILEMCP_WINDOWS_STAGING_NAME."
}
$WorkflowStaging = $WorkflowMatch.Groups[1].Value

if ($DefaultStaging -ne $WorkflowStaging) {
    throw "Windows staging mismatch: build default '$DefaultStaging' != workflow '$WorkflowStaging'."
}

foreach ($Architecture in @("x64", "arm64")) {
    $ExpectedBuild = "run: ./build_windows_app.ps1 -Architecture $Architecture -StagingName `$env:FILEMCP_WINDOWS_STAGING_NAME"
    if ($Workflow.IndexOf($ExpectedBuild, [StringComparison]::Ordinal) -lt 0) {
        throw "GitHub workflow does not pass the canonical staging name to the $Architecture package build."
    }
}

$ExpectedVerifyRoot = '$root = "dist/windows-$arch/$env:FILEMCP_WINDOWS_STAGING_NAME"'
if ($Workflow.IndexOf($ExpectedVerifyRoot, [StringComparison]::Ordinal) -lt 0) {
    throw "GitHub workflow release-resource verification does not use the canonical staging name."
}

$ObsoleteVerifyRoot = '$root = "dist/windows-$arch/FileMCP"'
if ($Workflow.IndexOf($ObsoleteVerifyRoot, [StringComparison]::Ordinal) -ge 0) {
    throw "GitHub workflow still contains the obsolete Windows staging path."
}

Write-Host "windows-release-contract: ok (staging=$DefaultStaging)"

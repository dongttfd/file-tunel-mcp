param(
    [string]$Repository = "nguyenkhactang922-bot/file-tunel-mcp",
    [string]$Environment = "production-release",
    [string]$Version = "0.4.0",
    [switch]$Dispatch
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RequiredSecrets = @(
    "WINDOWS_CODESIGN_PFX_BASE64",
    "WINDOWS_CODESIGN_PFX_PASSWORD",
    "MACOS_DEVELOPER_ID_P12_BASE64",
    "MACOS_DEVELOPER_ID_P12_PASSWORD",
    "APPLE_NOTARY_KEY_P8_BASE64",
    "APPLE_NOTARY_KEY_ID",
    "APPLE_NOTARY_ISSUER_ID"
)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required."
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated."
}

$defaultBranch = gh repo view $Repository --json defaultBranchRef --jq ".defaultBranchRef.name"
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve the repository default branch."
}
$defaultBranch = $defaultBranch.Trim()
if ($defaultBranch -ne "main") {
    throw "Production Release is expected on default branch main; found '$defaultBranch'."
}

$workflowJson = gh workflow list --repo $Repository --json name,state
if ($LASTEXITCODE -ne 0) {
    throw "Could not list GitHub workflows."
}
$workflows = @($workflowJson | ConvertFrom-Json)
$releaseWorkflow = $workflows | Where-Object { $_.name -eq "Production Release" -and $_.state -eq "active" }
if (-not $releaseWorkflow) {
    throw "Production Release workflow is not registered and active on the default branch."
}

$configuredSecrets = @{}
foreach ($line in @(gh secret list --repo $Repository --env $Environment)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $name = ($line -split "`t")[0]
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        $configuredSecrets[$name] = $true
    }
}
if ($LASTEXITCODE -ne 0) {
    throw "Could not list Environment secret names."
}

$missing = @($RequiredSecrets | Where-Object { -not $configuredSecrets.ContainsKey($_) })
Write-Host "Production Release workflow: active"
Write-Host "Default branch: $defaultBranch"
Write-Host "Release version: $Version"
Write-Host "Configured required Environment secrets: $($RequiredSecrets.Count - $missing.Count)/$($RequiredSecrets.Count)"

if ($missing.Count -gt 0) {
    Write-Host "Missing Environment secret names:"
    foreach ($name in $missing) {
        Write-Host " - $name"
    }
    exit 2
}

Write-Host "production-release-readiness: READY"

if ($Dispatch) {
    gh workflow run release.yml --repo $Repository --ref main -f "version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not dispatch Production Release."
    }
    Write-Host "Production Release dispatched."
}
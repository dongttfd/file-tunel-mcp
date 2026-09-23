$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$WorkflowPath = ".github/workflows/release.yml"
$WindowsScriptPath = "release/sign_windows_release.ps1"
$MacScriptPath = "release/sign_notarize_macos.sh"
$VerifyPath = ".github/workflows/verify.yml"
$ReadinessPath = "release/check_production_release_readiness.ps1"

foreach ($Path in @($WorkflowPath, $WindowsScriptPath, $MacScriptPath, $VerifyPath, $ReadinessPath)) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Production release contract file is missing: $Path"
    }
}

$Workflow = Get-Content $WorkflowPath -Raw
$WindowsScript = Get-Content $WindowsScriptPath -Raw
$MacScript = Get-Content $MacScriptPath -Raw
$Verify = Get-Content $VerifyPath -Raw
$Readiness = Get-Content $ReadinessPath -Raw
$GitIgnore = Get-Content ".gitignore" -Raw
$WindowsBuild = Get-Content "build_windows_app.ps1" -Raw
$MacBuild = Get-Content "build_macos_app.sh" -Raw

foreach ($Marker in @(
    "workflow_dispatch:",
    "environment: production-release",
    "permissions:",
    "contents: read",
    "release-preflight:",
    "production-release-preflight: ready",
    "release-windows-x64:",
    "release-windows-arm64:",
    "release-macos-arm64:",
    "WINDOWS_CODESIGN_PFX_BASE64",
    "WINDOWS_CODESIGN_PFX_PASSWORD",
    "MACOS_DEVELOPER_ID_P12_BASE64",
    "MACOS_DEVELOPER_ID_P12_PASSWORD",
    "APPLE_NOTARY_KEY_P8_BASE64",
    "APPLE_NOTARY_KEY_ID",
    "APPLE_NOTARY_ISSUER_ID"
)) {
    if ($Workflow.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Production release workflow marker missing: $Marker"
    }
}

$preflightNeedsCount = ([regex]::Matches($Workflow, '(?m)^\s{4}needs: release-preflight\s*$')).Count
if ($preflightNeedsCount -ne 3) {
    throw "Expected exactly three release jobs to depend on release-preflight; found $preflightNeedsCount."
}

$OnIndex = $Workflow.IndexOf("on:", [StringComparison]::Ordinal)
$PermissionsIndex = $Workflow.IndexOf("permissions:", [StringComparison]::Ordinal)
if ($OnIndex -lt 0 -or $PermissionsIndex -le $OnIndex) {
    throw "Could not bound release workflow trigger section."
}
$TriggerSection = $Workflow.Substring($OnIndex, $PermissionsIndex - $OnIndex)
if ($TriggerSection.IndexOf("push:", [StringComparison]::Ordinal) -ge 0 -or
    $TriggerSection.IndexOf("pull_request:", [StringComparison]::Ordinal) -ge 0 -or
    $TriggerSection.IndexOf("schedule:", [StringComparison]::Ordinal) -ge 0) {
    throw "Production release workflow must remain workflow_dispatch-only."
}

foreach ($Marker in @(
    "Import-PfxCertificate",
    "1.3.6.1.5.5.7.3.3",
    "/fd SHA256",
    "/td SHA256",
    "/tr ",
    "Get-AuthenticodeSignature",
    "TimeStamperCertificate",
    "Compress-Archive",
    "Expand-Archive",
    "Cert:\CurrentUser\My"
)) {
    if ($WindowsScript.IndexOf($Marker, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Windows signing marker missing: $Marker"
    }
}

foreach ($Marker in @(
    "Developer ID Application:",
    "--options runtime",
    "--timestamp",
    "notarytool submit",
    "Accepted",
    "stapler staple",
    "stapler validate",
    "spctl --assess",
    "trap cleanup EXIT",
    "security delete-keychain"
)) {
    if ($MacScript.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "macOS signing/notarization marker missing: $Marker"
    }
}

foreach ($SecretName in @(
    "WINDOWS_CODESIGN_PFX_BASE64",
    "WINDOWS_CODESIGN_PFX_PASSWORD",
    "MACOS_DEVELOPER_ID_P12_BASE64",
    "MACOS_DEVELOPER_ID_P12_PASSWORD",
    "APPLE_NOTARY_KEY_P8_BASE64",
    "APPLE_NOTARY_KEY_ID",
    "APPLE_NOTARY_ISSUER_ID"
)) {
    if ($Verify.IndexOf($SecretName, [StringComparison]::Ordinal) -ge 0) {
        throw "Normal Verify workflow must not reference production secret: $SecretName"
    }
}

foreach ($Marker in @("*.pfx", "*.p12", "*.p8")) {
    if ($GitIgnore.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Signing-key gitignore marker missing: $Marker"
    }
}

if ($WindowsBuild.IndexOf("signtool", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $WindowsBuild.IndexOf("Import-PfxCertificate", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Normal Windows build script must remain unsigned."
}
if ($MacBuild.IndexOf("codesign", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $MacBuild.IndexOf("notarytool", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Normal macOS build script must remain unsigned."
}

$trackedKeyFiles = @(@(& git ls-files -- "*.pfx" "*.p12" "*.p8") |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($trackedKeyFiles.Count -gt 0) {
    throw "Tracked production key material detected: $($trackedKeyFiles -join ', ')"
}

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $WindowsScriptPath),
    [ref]$tokens,
    [ref]$parseErrors
) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw "Windows release signing script has PowerShell syntax errors: $($parseErrors[0].Message)"
}

foreach ($Marker in @(
    "Production Release workflow: active",
    "Configured required Environment secrets:",
    "production-release-readiness: READY",
    "gh workflow run release.yml",
    "WINDOWS_CODESIGN_PFX_BASE64",
    "WINDOWS_CODESIGN_PFX_PASSWORD",
    "MACOS_DEVELOPER_ID_P12_BASE64",
    "MACOS_DEVELOPER_ID_P12_PASSWORD",
    "APPLE_NOTARY_KEY_P8_BASE64",
    "APPLE_NOTARY_KEY_ID",
    "APPLE_NOTARY_ISSUER_ID"
)) {
    if ($Readiness.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Production release readiness checker marker missing: $Marker"
    }
}

Write-Host "production-release-signing-contract: ok"

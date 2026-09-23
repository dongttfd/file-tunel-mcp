$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

$HelperPath = "release/configure_production_release_secrets.ps1"
if (-not (Test-Path -LiteralPath $HelperPath -PathType Leaf)) {
    throw "Release secret provisioning helper is missing."
}

$Helper = Get-Content $HelperPath -Raw
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $HelperPath),
    [ref]$tokens,
    [ref]$errors
) | Out-Null
if ($errors.Count -gt 0) {
    throw "Release secret provisioning helper has PowerShell syntax errors: $($errors[0].Message)"
}

foreach ($Marker in @(
    'Resolve-CredentialFile',
    'Invoke-GhSecretSet',
    'Set-SecureStringSecret',
    'Read-Host "Windows PFX password" -AsSecureString',
    'Read-Host "macOS Developer ID P12 password" -AsSecureString',
    'RedirectStandardInput = $true',
    'ZeroFreeBSTR',
    'must be stored outside the FileMCP repository',
    'Repository must use owner/name format with GitHub-safe characters.',
    'Environment contains unsupported characters.',
    'check_production_release_readiness.ps1',
    'WINDOWS_CODESIGN_PFX_BASE64',
    'WINDOWS_CODESIGN_PFX_PASSWORD',
    'MACOS_DEVELOPER_ID_P12_BASE64',
    'MACOS_DEVELOPER_ID_P12_PASSWORD',
    'APPLE_NOTARY_KEY_P8_BASE64',
    'APPLE_NOTARY_KEY_ID',
    'APPLE_NOTARY_ISSUER_ID'
)) {
    if ($Helper.IndexOf($Marker, [StringComparison]::Ordinal) -lt 0) {
        throw "Release secret provisioning marker missing: $Marker"
    }
}

if ($Helper.IndexOf("--body", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Provisioning helper must not place secret values in gh --body command arguments."
}
if ($Helper.IndexOf("ArgumentList", [StringComparison]::Ordinal) -ge 0) {
    throw "Provisioning helper must remain compatible with Windows PowerShell 5.1."
}
if ($Helper.IndexOf("Out-File", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $Helper.IndexOf("Set-Content", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Provisioning helper must not persist plaintext secret values."
}

Write-Host "release-secret-provisioning-contract: ok"

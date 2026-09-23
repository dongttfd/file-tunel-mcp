param(
    [Parameter(Mandatory = $true)]
    [string]$WindowsPfxPath,

    [Parameter(Mandatory = $true)]
    [string]$MacDeveloperIdP12Path,

    [Parameter(Mandatory = $true)]
    [string]$AppleNotaryP8Path,

    [string]$AppleNotaryKeyId,
    [string]$AppleNotaryIssuerId,
    [string]$Repository = "nguyenkhactang922-bot/file-tunel-mcp",
    [string]$Environment = "production-release",
    [string]$Version = "0.4.0",
    [switch]$Dispatch
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$RootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'

function Resolve-CredentialFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedExtension,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $full = [IO.Path]::GetFullPath($resolved)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label must be a regular file."
    }
    if (-not [string]::Equals([IO.Path]::GetExtension($full), $ExpectedExtension, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must use the $ExpectedExtension extension."
    }
    if ($full.StartsWith($RootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be stored outside the FileMCP repository."
    }
    $length = (Get-Item -LiteralPath $full).Length
    if ($length -le 0 -or $length -gt 10MB) {
        throw "$Label has an invalid size."
    }
    return $full
}

function Invoke-GhSecretSet {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $gh = (Get-Command gh -ErrorAction Stop).Source
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $gh
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.Arguments = "secret set $Name --repo $Repository --env $Environment"

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Could not start gh to configure secret $Name."
    }

    try {
        $process.StandardInput.Write($Value)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "GitHub rejected secret $Name. $($stderr.Trim())"
        }
    }
    finally {
        $process.Dispose()
    }

    Write-Host "Configured Environment secret: $Name"
}

function Set-SecureStringSecret {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][Security.SecureString]$SecureValue
    )

    $pointer = [IntPtr]::Zero
    $plain = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        if ([string]::IsNullOrEmpty($plain)) {
            throw "$Name cannot be empty."
        }
        Invoke-GhSecretSet -Name $Name -Value $plain
    }
    finally {
        $plain = $null
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must use owner/name format with GitHub-safe characters."
}
if ($Environment -notmatch '^[A-Za-z0-9_.-]+$') {
    throw "Environment contains unsupported characters."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is required."
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not authenticated."
}

gh api "repos/$Repository/environments/$Environment" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "GitHub Environment '$Environment' does not exist or is not accessible."
}

$windowsPfx = Resolve-CredentialFile -Path $WindowsPfxPath -ExpectedExtension ".pfx" -Label "Windows code-signing PFX"
$macP12 = Resolve-CredentialFile -Path $MacDeveloperIdP12Path -ExpectedExtension ".p12" -Label "macOS Developer ID P12"
$notaryP8 = Resolve-CredentialFile -Path $AppleNotaryP8Path -ExpectedExtension ".p8" -Label "Apple notarization API key"

$windowsPassword = Read-Host "Windows PFX password" -AsSecureString
$macPassword = Read-Host "macOS Developer ID P12 password" -AsSecureString

if ([string]::IsNullOrWhiteSpace($AppleNotaryKeyId)) {
    $AppleNotaryKeyId = Read-Host "Apple notary API key ID"
}
if ([string]::IsNullOrWhiteSpace($AppleNotaryIssuerId)) {
    $AppleNotaryIssuerId = Read-Host "Apple notary issuer ID"
}
if ([string]::IsNullOrWhiteSpace($AppleNotaryKeyId) -or [string]::IsNullOrWhiteSpace($AppleNotaryIssuerId)) {
    throw "Apple notary key ID and issuer ID are required."
}

$windowsBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($windowsPfx))
$macBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($macP12))
$notaryBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($notaryP8))

try {
    Invoke-GhSecretSet -Name "WINDOWS_CODESIGN_PFX_BASE64" -Value $windowsBase64
    Set-SecureStringSecret -Name "WINDOWS_CODESIGN_PFX_PASSWORD" -SecureValue $windowsPassword
    Invoke-GhSecretSet -Name "MACOS_DEVELOPER_ID_P12_BASE64" -Value $macBase64
    Set-SecureStringSecret -Name "MACOS_DEVELOPER_ID_P12_PASSWORD" -SecureValue $macPassword
    Invoke-GhSecretSet -Name "APPLE_NOTARY_KEY_P8_BASE64" -Value $notaryBase64
    Invoke-GhSecretSet -Name "APPLE_NOTARY_KEY_ID" -Value $AppleNotaryKeyId.Trim()
    Invoke-GhSecretSet -Name "APPLE_NOTARY_ISSUER_ID" -Value $AppleNotaryIssuerId.Trim()
}
finally {
    $windowsBase64 = $null
    $macBase64 = $null
    $notaryBase64 = $null
    $windowsPassword.Dispose()
    $macPassword.Dispose()
}

$readiness = Join-Path $Root "release\check_production_release_readiness.ps1"
if ($Dispatch) {
    & $readiness -Repository $Repository -Environment $Environment -Version $Version -Dispatch
}
else {
    & $readiness -Repository $Repository -Environment $Environment -Version $Version
}
exit $LASTEXITCODE

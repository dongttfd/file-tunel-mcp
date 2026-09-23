param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("x64", "arm64")]
    [string]$Architecture,

    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [ValidatePattern("^[A-Za-z0-9._-]+$")]
    [string]$StagingName = "FileMCP-release",

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version = "0.4.0",

    [ValidatePattern("^https?://")]
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        throw "ProgramFiles(x86) is unavailable."
    }
    $kitsRoot = Join-Path $programFilesX86 "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kitsRoot -PathType Container)) {
        throw "Windows SDK signtool.exe was not found."
    }

    $preferred = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x64" }
    $candidates = @(
        Get-ChildItem -Path $kitsRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq $preferred } |
            Sort-Object FullName -Descending
    )
    if ($candidates.Count -eq 0 -and $preferred -ne "x64") {
        $candidates = @(
            Get-ChildItem -Path $kitsRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Directory.Name -eq "x64" } |
                Sort-Object FullName -Descending
        )
    }
    if ($candidates.Count -eq 0) {
        throw "Windows SDK signtool.exe was not found."
    }
    return $candidates[0].FullName
}

function Test-CodeSigningEku([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $extension.EnhancedKeyUsages) {
                if ($oid.Value -eq "1.3.6.1.5.5.7.3.3") {
                    return $true
                }
            }
        }
    }
    return $false
}

function Assert-ValidSignature([string]$Path, [string]$Label) {
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "$Label Authenticode verification failed: $($signature.Status) $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate) {
        throw "$Label is missing an Authenticode signer certificate."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "$Label is missing a trusted timestamp certificate."
    }
}

if (-not (Test-Path -LiteralPath $PfxPath -PathType Leaf)) {
    throw "Signing PFX file does not exist."
}
$passwordText = [Environment]::GetEnvironmentVariable("WINDOWS_CODESIGN_PFX_PASSWORD")
if ([string]::IsNullOrWhiteSpace($passwordText)) {
    throw "WINDOWS_CODESIGN_PFX_PASSWORD is required."
}

$publishDir = Join-Path $Root "dist/windows-$Architecture/$StagingName"
$appPath = Join-Path $publishDir "FileMCP.exe"
$zipPath = Join-Path $Root "dist/FileMCP-v$Version-windows-$Architecture.zip"
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw "Unsigned FileMCP.exe was not found. Build the release staging directory first."
}

$securePassword = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
$imported = @()
$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("filemcp-signed-package-" + [Guid]::NewGuid().ToString("N"))

try {
    $imported = @(Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation "Cert:\CurrentUser\My" -Password $securePassword)
    $eligible = @(
        $imported | Where-Object {
            $_.HasPrivateKey -and
            (Test-CodeSigningEku $_) -and
            $_.NotBefore.ToUniversalTime() -le [DateTime]::UtcNow -and
            $_.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow
        }
    )
    if ($eligible.Count -ne 1) {
        throw "Expected exactly one currently-valid code-signing certificate with a private key in the supplied PFX."
    }

    $certificate = $eligible[0]
    $signTool = Find-SignTool

    & $signTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $certificate.Thumbprint $appPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool signing failed."
    }

    & $signTool verify /pa /all /v $appPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verification failed for the staged executable."
    }
    Assert-ValidSignature $appPath "Staged FileMCP.exe"

    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

    New-Item -ItemType Directory -Force $extractDir | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
    $packagedApp = Join-Path $extractDir "FileMCP.exe"
    if (-not (Test-Path -LiteralPath $packagedApp -PathType Leaf)) {
        throw "Signed package is missing FileMCP.exe."
    }
    Assert-ValidSignature $packagedApp "Packaged FileMCP.exe"

    Write-Host "windows-production-signing: ok ($Architecture)"
    Get-FileHash -Algorithm SHA256 $zipPath
}
finally {
    foreach ($certificate in $imported) {
        try { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $certificate.Thumbprint) -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    $securePassword.Dispose()
    $passwordText = $null
}

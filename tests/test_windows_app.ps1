param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

trap {
    $Message = $_.Exception.ToString()
    $Message = $Message.Replace("%", "%25")
    $Message = $Message.Replace("`r", "%0D")
    $Message = $Message.Replace("`n", "%0A")
    Write-Host "::error file=tests/test_windows_app.ps1,title=Windows app smoke failure::$Message"
    exit 1
}

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

if ($env:OS -ne "Windows_NT") {
    throw "Windows app smoke test must run on Windows."
}

$Archive = Join-Path $Root "dist/FileMCP-v0.4.0-windows-$Architecture.zip"
if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
    throw "Windows $Architecture release archive is missing: $Archive"
}

$SmokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("filemcp-windows-app-smoke-$Architecture-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $SmokeRoot | Out-Null
Expand-Archive -LiteralPath $Archive -DestinationPath $SmokeRoot -Force
$Exe = Join-Path $SmokeRoot "FileMCP.exe"
$TunnelClient = Join-Path $SmokeRoot "tunnel-client.exe"
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "FileMCP.exe is missing from the Windows $Architecture release archive."
}
if (-not (Test-Path -LiteralPath $TunnelClient -PathType Leaf)) {
    throw "tunnel-client.exe is missing from the Windows $Architecture release archive."
}
$SmokeDatabase = Join-Path $SmokeRoot "observability-package-smoke.sqlite3"
$SmokeMarker = Join-Path $SmokeRoot "observability-package-smoke.txt"
$OtlpSmokeMarker = Join-Path $SmokeRoot "otlp-package-smoke.txt"
$SingleInstanceSmokeMarker = Join-Path $SmokeRoot "single-instance-activation-smoke.txt"
$PreviousSingleInstanceMarker = $env:FILEMCP_SINGLE_INSTANCE_SMOKE_MARKER
$PreviousOtlpMarker = $env:FILEMCP_OTLP_SMOKE_MARKER
$PreviousOtlpEndpoint = $env:FILEMCP_OTLP_SMOKE_ENDPOINT
$PreviousObservabilityDb = $env:FILEMCP_OBSERVABILITY_DB
$PreviousObservabilityMarker = $env:FILEMCP_OBSERVABILITY_SMOKE_MARKER
$env:FILEMCP_OBSERVABILITY_DB = $SmokeDatabase
$env:FILEMCP_OBSERVABILITY_SMOKE_MARKER = $SmokeMarker
$env:FILEMCP_SINGLE_INSTANCE_SMOKE_MARKER = $SingleInstanceSmokeMarker
$env:FILEMCP_OTLP_SMOKE_MARKER = $OtlpSmokeMarker
$env:FILEMCP_OTLP_SMOKE_ENDPOINT = "http://127.0.0.1:1"
$TunnelVersion = & $TunnelClient --version
if ($LASTEXITCODE -ne 0 -or $TunnelVersion -notmatch '^0\.0\.12\+881c9a8fed7cccbe6607cd419863bbca506b8215 ') {
    throw "Unexpected packaged tunnel-client version: $TunnelVersion"
}

$StartedAt = Get-Date
$Process = Start-Process -FilePath $Exe -PassThru
try {
    $Deadline = (Get-Date).AddSeconds(12)
    $WindowReady = $false
    while ((Get-Date) -lt $Deadline) {
        Start-Sleep -Milliseconds 500
        $Process.Refresh()
        if ($Process.HasExited) {
            $EventText = ""
            try {
                $Events = Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $StartedAt.AddSeconds(-2) } -ErrorAction Stop |
                    Where-Object { $_.ProviderName -in @(".NET Runtime", "Application Error", "Windows Error Reporting") -and $_.Message -match "FileMCP" } |
                    Select-Object -First 8
                if ($Events) {
                    $EventText = ($Events | ForEach-Object { "[$($_.ProviderName)] $($_.Message)" }) -join "`n---`n"
                }
            } catch {
                $EventText = "Could not read Application event log: $($_.Exception.Message)"
            }
            throw "FileMCP.exe exited during WPF startup with exit code $($Process.ExitCode).`n$EventText"
        }
        if ($Process.MainWindowHandle -ne 0 -and $Process.MainWindowTitle -eq "FileMCP") {
            $WindowReady = $true
            break
        }
    }
    $SqliteDeadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $SqliteDeadline -and -not (Test-Path -LiteralPath $SmokeMarker -PathType Leaf)) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $SmokeMarker -PathType Leaf)) {
        throw "Packaged FileMCP did not complete the native SQLite write/read smoke within 12 seconds."
    }
    $SmokeResult = (Get-Content -LiteralPath $SmokeMarker -Raw).Trim()
    if (-not $SmokeResult.StartsWith("PASS ", [StringComparison]::Ordinal)) {
        throw "Packaged FileMCP SQLite smoke failed: $SmokeResult"
    }
    $OtlpDeadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $OtlpDeadline -and -not (Test-Path -LiteralPath $OtlpSmokeMarker -PathType Leaf)) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Path -LiteralPath $OtlpSmokeMarker -PathType Leaf)) {
        throw "Packaged FileMCP did not complete the OTLP provider smoke within 12 seconds."
    }
    $OtlpSmokeResult = (Get-Content -LiteralPath $OtlpSmokeMarker -Raw).Trim()
    if (-not $OtlpSmokeResult.StartsWith("PASS status=Configured", [StringComparison]::Ordinal)) {
        throw "Packaged FileMCP OTLP smoke failed: $OtlpSmokeResult"
    }
    foreach ($Notice in @("OpenTelemetry-LICENSE.txt", "OpenTelemetry-THIRD-PARTY-NOTICES.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $SmokeRoot $Notice) -PathType Leaf)) {
            throw "Packaged FileMCP is missing OpenTelemetry redistribution notice: $Notice"
        }
    }

    if (-not (Test-Path -LiteralPath $SmokeDatabase -PathType Leaf)) {
        throw "Packaged FileMCP did not create the requested telemetry SQLite database."
    }
    $DatabaseStream = [System.IO.FileStream]::new($SmokeDatabase, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        $DatabaseBytes = New-Object byte[] 16
        $DatabaseRead = $DatabaseStream.Read($DatabaseBytes, 0, $DatabaseBytes.Length)
    } finally {
        $DatabaseStream.Dispose()
    }
    if ($DatabaseRead -lt 16 -or [System.Text.Encoding]::ASCII.GetString($DatabaseBytes, 0, 16) -ne "SQLite format 3`0") {
        throw "Packaged FileMCP telemetry output is not a valid SQLite database header."
    }
    if (-not $WindowReady) {
        throw "FileMCP.exe stayed alive but did not create the FileMCP main window within 12 seconds."
    }

    if (-not $Process.CloseMainWindow()) {
        throw "FileMCP.exe main window did not accept a normal close request."
    }
    Start-Sleep -Seconds 2
    $Process.Refresh()
    if ($Process.HasExited) {
        throw "FileMCP.exe exited after closing the main window instead of remaining active in the system tray."
    }

    $SecondaryProcess = Start-Process -FilePath $Exe -PassThru
    try {
        $SecondaryDeadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $SecondaryDeadline) {
            Start-Sleep -Milliseconds 100
            $SecondaryProcess.Refresh()
            if ($SecondaryProcess.HasExited) { break }
        }
        $SecondaryProcess.Refresh()
        if (-not $SecondaryProcess.HasExited) {
            throw "Second FileMCP launch did not exit after signaling the primary instance."
        }
        if ($SecondaryProcess.ExitCode -ne 0) {
            throw "Second FileMCP launch exited with unexpected code $($SecondaryProcess.ExitCode)."
        }
    }
    finally {
        if (-not $SecondaryProcess.HasExited) {
            Stop-Process -Id $SecondaryProcess.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $SecondaryProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
        $SecondaryProcess.Dispose()
    }

    $ActivationDeadline = (Get-Date).AddSeconds(6)
    while ((Get-Date) -lt $ActivationDeadline -and -not (Test-Path -LiteralPath $SingleInstanceSmokeMarker -PathType Leaf)) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $SingleInstanceSmokeMarker -PathType Leaf)) {
        throw "Primary FileMCP instance did not receive the second-launch activation signal."
    }
    $ActivationResult = (Get-Content -LiteralPath $SingleInstanceSmokeMarker -Raw).Trim()
    if ($ActivationResult -ne "PASS pid=$($Process.Id)") {
        throw "Unexpected single-instance activation marker: $ActivationResult"
    }
    $Process.Refresh()
    if ($Process.HasExited) {
        throw "Primary FileMCP exited during second-launch activation."
    }

    Write-Host "windows-app-startup-${Architecture}: ok"
    Write-Host "windows-close-to-tray-${Architecture}: ok"
    Write-Host "windows-packaged-tunnel-client-${Architecture}: ok"
    Write-Host "windows-single-instance-activation-${Architecture}: ok"
    Write-Host "windows-packaged-sqlite-write-read: ok ($SmokeResult)"
    Write-Host "windows-packaged-otlp-provider: ok ($OtlpSmokeResult)"
    Write-Host "windows-packaged-opentelemetry-notices: ok"
}
finally {
    if ($null -eq $PreviousObservabilityDb) { Remove-Item Env:FILEMCP_OBSERVABILITY_DB -ErrorAction SilentlyContinue } else { $env:FILEMCP_OBSERVABILITY_DB = $PreviousObservabilityDb }
    if ($null -eq $PreviousObservabilityMarker) { Remove-Item Env:FILEMCP_OBSERVABILITY_SMOKE_MARKER -ErrorAction SilentlyContinue } else { $env:FILEMCP_OBSERVABILITY_SMOKE_MARKER = $PreviousObservabilityMarker }
    if ($null -eq $PreviousSingleInstanceMarker) { Remove-Item Env:FILEMCP_SINGLE_INSTANCE_SMOKE_MARKER -ErrorAction SilentlyContinue } else { $env:FILEMCP_SINGLE_INSTANCE_SMOKE_MARKER = $PreviousSingleInstanceMarker }
    if ($null -eq $PreviousOtlpMarker) { Remove-Item Env:FILEMCP_OTLP_SMOKE_MARKER -ErrorAction SilentlyContinue } else { $env:FILEMCP_OTLP_SMOKE_MARKER = $PreviousOtlpMarker }
    if ($null -eq $PreviousOtlpEndpoint) { Remove-Item Env:FILEMCP_OTLP_SMOKE_ENDPOINT -ErrorAction SilentlyContinue } else { $env:FILEMCP_OTLP_SMOKE_ENDPOINT = $PreviousOtlpEndpoint }
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $Process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $Process.Dispose()
    Remove-Item -LiteralPath $SmokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}

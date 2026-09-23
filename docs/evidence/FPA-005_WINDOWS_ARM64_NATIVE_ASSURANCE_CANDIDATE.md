# FPA-005 - Windows ARM64 Native Assurance Candidate Evidence

Date: 2026-09-22
Status: CANDIDATE - NATIVE WINDOWS ARM64 CI REQUIRED

## Frozen architecture

- docs/design/FPA-005_WINDOWS_ARM64_NATIVE_ASSURANCE_FROZEN.md
- docs/design/FPA-005_INDEPENDENT_REVIEW.md
- tasks/FPA-005_TASK_GRAPH.md

## Candidate implementation

- Parameterized tests/test_windows_app.ps1 with -Architecture x64|arm64.
- Preserved x64 as the default for backward compatibility.
- Added native verify-windows-arm64 GitHub job.
- Native runner label: windows-11-vs2026-arm.
- Native job asserts OSArchitecture == Arm64.
- Native job executes vendored windows-arm64 tunnel-client --version and validates checksum.
- Native job builds the Windows solution warnings-as-errors.
- Native job builds the ARM64 package using the canonical FileMCP-release staging contract.
- Native job runs the same packaged app smoke with -Architecture arm64.
- Native job verifies release/legal resources including OpenTelemetry notices.
- Native job owns the ARM64 package upload.
- Added tests/test_windows_arm64_assurance_contract.ps1 to prevent silent regression to cross-build-only assurance.

## Local verification

```text
tests/test_windows_arm64_assurance_contract.ps1
PASS - windows-arm64-assurance-contract: ok

tests/test_windows_release_contract.ps1
PASS - windows-release-contract: ok

tests/test_windows_app.ps1
PASS on x64 default:
- WPF startup
- close-to-tray
- packaged tunnel-client
- native SQLite write/read
- optional OTLP provider
- redistribution notices

Windows Release build -warnaserror
PASS - 0 warnings / 0 errors

tests/test_windows_runtime.ps1
PASS - 414 assertions

git diff --check
PASS
```

## Remaining acceptance

A real GitHub Actions run must prove:

- verify-windows: SUCCESS;
- verify-windows-arm64: SUCCESS;
- ARM64 FileMCP.exe starts natively and creates the WPF window;
- ARM64 close-to-tray succeeds;
- ARM64 vendored tunnel-client executes natively;
- ARM64 SQLite smoke succeeds;
- ARM64 OTLP provider smoke succeeds;
- ARM64 package upload succeeds.

FPA-005 remains ACTIVE until native ARM64 CI is green.

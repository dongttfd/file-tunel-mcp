# FPA-005 - Windows ARM64 Native Assurance Evidence

Date: 2026-09-22
Status: PASS

## Architecture

Frozen before implementation:

- docs/design/FPA-005_WINDOWS_ARM64_NATIVE_ASSURANCE_FROZEN.md
- docs/design/FPA-005_INDEPENDENT_REVIEW.md
- tasks/FPA-005_TASK_GRAPH.md

## Implemented

- Parameterized tests/test_windows_app.ps1 with -Architecture x64|arm64.
- x64 remains the default for backward compatibility.
- Added native GitHub job verify-windows-arm64.
- Native runner: windows-11-vs2026-arm.
- Job asserts RuntimeInformation.OSArchitecture == Arm64.
- ARM64 vendored tunnel-client is checksum-verified and executed natively.
- ARM64 FileMCP package is built with the canonical FileMCP-release staging contract.
- The same packaged app smoke executes natively on ARM64.
- ARM64 release/legal resources are verified.
- ARM64 artifact upload is owned by the native job.
- Added tests/test_windows_arm64_assurance_contract.ps1.

## Local evidence

```text
windows-arm64-assurance-contract: PASS
windows-release-contract: PASS
x64 packaged app smoke: PASS
Windows Release build -warnaserror: PASS, 0 warnings / 0 errors
Windows runtime suite: PASS, 414 assertions
git diff --check: PASS
```

## Native GitHub evidence

Accepted commit: `9316df1`
GitHub Actions run: `35726514963`

```text
verify-macos:          SUCCESS
verify-windows:        SUCCESS
verify-windows-arm64:  SUCCESS
```

Native ARM64 markers:

```text
windows-native-arm64-runner: ok
windows-app-startup-arm64: ok
windows-close-to-tray-arm64: ok
windows-packaged-tunnel-client-arm64: ok
windows-packaged-sqlite-write-read: ok (PASS tool_calls=1 read_calls=1)
windows-packaged-otlp-provider: ok (PASS status=Configured endpoint=http://127.0.0.1:1)
windows-packaged-opentelemetry-notices: ok
```

## Result

FPA-005 PASS. Windows ARM64 is now runtime-verified on a real hosted ARM64 Windows runner rather than only cross-published on x64.

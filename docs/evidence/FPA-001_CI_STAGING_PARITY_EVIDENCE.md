# FPA-001 - WINDOWS CI STAGING PARITY EVIDENCE

Date: 2026-09-22
Status: PASS

## Problem

The Windows release build had moved to the canonical staging directory `FileMCP-release`, while `.github/workflows/verify.yml` still verified `dist/windows-$arch/FileMCP`. A clean GitHub Actions worker could therefore fail the release-resource step after a successful build.

## Fix

- Added job-level `FILEMCP_WINDOWS_STAGING_NAME: FileMCP-release`.
- Both x64 and ARM64 workflow package builds explicitly pass that staging name to `build_windows_app.ps1`.
- The workflow release-resource verification uses the same staging variable.
- Added `tests/test_windows_release_contract.ps1` to detect future drift between the build-script default and workflow staging contract.
- Added the release-contract test to the Windows workflow before the runtime integration suite.

## Verification

```text
tests/test_windows_release_contract.ps1
PASS - windows-release-contract: ok (staging=FileMCP-release)

x64 isolated package build
PASS - dist/windows-x64/FileMCP-fpa001

ARM64 isolated package build
PASS - dist/windows-arm64/FileMCP-fpa001

x64 release resources
PASS

ARM64 release resources
PASS

x64 packaged app smoke
PASS:
- WPF startup
- close-to-tray
- packaged tunnel-client
- native SQLite write/read
- optional OTLP provider
- OpenTelemetry redistribution notices
```

The isolated staging name was used locally so the currently connected `FileMCP-release` bridge was not overwritten.

## Result

The deterministic CI staging-path mismatch is closed. FPA-001 PASS.

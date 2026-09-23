# FPA-005 - Windows ARM64 Native Runtime Assurance - Frozen Design

Date: 2026-09-22
Status: FROZEN BEFORE IMPLEMENTATION

## Problem

FileMCP advertises and packages Windows ARM64, but the existing app smoke test is hardcoded to the x64 ZIP and executes only on an x64 GitHub runner.

Packaging/resource checks alone do not prove that the ARM64 executable, native SQLite path, OTLP provider, tray lifecycle and vendored ARM64 tunnel-client actually run on Windows ARM64.

## Selected architecture

Use GitHub's native hosted Windows ARM64 runner:

`windows-11-vs2026-arm`

Add a dedicated ARM64 verification job instead of emulation.

Parameterize `tests/test_windows_app.ps1`:

- `-Architecture x64|arm64`;
- default remains x64 for backward compatibility;
- archive path and diagnostics use the selected architecture;
- smoke behavior remains identical.

ARM64 job:

1. checkout;
2. setup .NET 8;
3. verify ARM64 tunnel-client version/checksum;
4. build solution warnings-as-errors;
5. build ARM64 release using canonical staging contract;
6. run `test_windows_app.ps1 -Architecture arm64`;
7. verify release resources;
8. upload ARM64 ZIP.

Existing x64 job remains responsible for the full Windows core/runtime suite and x64 app smoke. It may continue cross-building ARM64 if useful, but native ARM64 execution becomes authoritative for ARM64 runtime assurance.

## Acceptance

FPA-005 PASS requires a real GitHub Actions run with:

- verify-windows: SUCCESS;
- verify-windows-arm64: SUCCESS;
- ARM64 FileMCP.exe starts and creates the WPF main window;
- close-to-tray PASS;
- ARM64 vendored tunnel-client executes and reports expected version;
- native SQLite smoke PASS;
- OTLP provider smoke PASS;
- redistribution notices present;
- ARM64 ZIP upload PASS.

## Non-goals

- installer/MSIX work;
- signing/Authenticode (FPA-004);
- emulation fallback when native hosted ARM64 is available.

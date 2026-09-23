# FPA-008 - Cross-Platform Local MCP Connection Bounds Evidence

Date: 2026-09-22
Status: PASS

## Implemented

Windows and macOS now both enforce:
- maximum 64 concurrent local MCP connections by default;
- 15 second per-read idle timeout;
- 30 second absolute request-header completion deadline;
- immediate rejection/cancel of excess accepted clients before MCP request processing;
- exact-once connection-slot release;
- shutdown cancellation of blocked reads/connections.

Regression coverage includes saturation, slot reuse, idle timeout, slow-trickle header timeout and shutdown behavior.

## Local verification

- Windows Release build with warnings-as-errors: PASS, 0 warnings / 0 errors.
- Windows runtime suite: PASS, 428 assertions.
- windows-http-connection-bounds: ok.
- Windows core suite repeated three times after bounded-output fixture hardening: PASS / PASS / PASS.
- Embedded macOS Python socket fixture syntax: PASS.
- project-state-contract: PASS.
- tool-surface-parity: PASS.
- macos-supervisor-contract: PASS.
- http-connection-bounds-contract: PASS.
- git diff --check: PASS.

## Native GitHub verification

Run 35745850301:
- verify-macos: SUCCESS
- verify-windows: SUCCESS
- verify-windows-arm64: SUCCESS

## Result

FPA-008 PASS.

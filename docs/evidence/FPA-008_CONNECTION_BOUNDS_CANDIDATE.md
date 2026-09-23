# FPA-008 - Cross-Platform Local MCP Connection Bounds Candidate Evidence

Date: 2026-09-22
Status: CANDIDATE - NATIVE CI REQUIRED

## Frozen architecture

- docs/design/FPA-008_CONNECTION_BOUNDS_FROZEN.md
- docs/design/FPA-008_INDEPENDENT_REVIEW.md
- docs/design/FPA-008_DECISION_MATRIX.md
- tasks/FPA-008_TASK_GRAPH.md

## Implemented

Windows:
- LocalMcpServerLimits with defaults: 64 concurrent, 15s read-idle, 30s header deadline.
- SemaphoreSlim nonblocking connection slot gate.
- excess accepted clients are immediately disposed.
- every handler releases exactly one slot in finally.
- per-read linked cancellation enforces idle timeout.
- absolute header completion deadline prevents trickle connections.
- server cancellation still wins.
- focused tests cover saturation, slot reuse, idle timeout, header trickle and stop cancellation.

macOS:
- LocalMCPServerLimits with the same defaults.
- DispatchSemaphore connection slot gate.
- idempotent MCPConnectionLease releases one slot exactly once.
- active connection registry allows stop() to close existing leases.
- per-receive timeout work item enforces idle timeout.
- original header deadline is carried across recursive receives.
- excess connections are canceled before MCP parsing.
- native socket tests cover saturation, slot reuse, idle timeout and trickle/header deadline.

Cross-platform:
- tests/test_http_connection_bounds_contract.ps1 prevents default/guard drift.
- contract is wired into verify-windows before runtime integration.

## Local verification

```text
tests/test_http_connection_bounds_contract.ps1: PASS
tests/test_project_state_contract.ps1: PASS
tests/test_tool_surface_parity.ps1: PASS
tests/test_macos_supervisor_contract.ps1: PASS
Git Bash syntax check: PASS
git diff --check: PASS

Windows Release build -warnaserror:
PASS - 0 warnings / 0 errors

Windows runtime suite:
PASS - windows-http-connection-bounds: ok
PASS - 428 assertions
```

## Remaining acceptance

Native GitHub Actions must prove on this exact candidate:
- verify-macos SUCCESS, including full Swift runtime socket tests and macOS app build;
- verify-windows SUCCESS;
- verify-windows-arm64 SUCCESS.

FPA-008 remains ACTIVE until all native jobs succeed.

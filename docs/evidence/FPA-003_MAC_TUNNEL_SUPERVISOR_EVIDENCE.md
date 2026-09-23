# FPA-003 - macOS Tunnel Supervisor Parity Evidence

Date: 2026-09-22
Status: PASS

## Architecture

Frozen before implementation:

- docs/design/FPA-003_MAC_TUNNEL_SUPERVISOR_FROZEN.md
- docs/design/FPA-003_INDEPENDENT_REVIEW.md
- docs/design/FPA-003_DECISION_MATRIX.md
- tasks/FPA-003_TASK_GRAPH.md

## Implemented

- macOS bounded tunnel restart policy with Windows-parity defaults.
- Exponential backoff, jitter, restart-window budget, cooldown and stable-run reset.
- Child-only tunnel restart; local MCP server and profile lock remain alive during recovery.
- Runtime states: restarting and cooldown.
- Cancelable scheduled recovery and tunnel-generation stale-callback guard.
- Stop/shutdown cancel pending recovery and prevent relaunch.
- UI keeps Disconnect available during recovery.
- Build/dev/CI/test wiring for TunnelSupervisor.swift.
- Policy tests cover backoff, clamp, jitter, budget, cooldown, stable reset and 10k bounded decisions.
- Runtime integration tests cover crash-once auto restart, retained profile lock, stop-cancel and budget cooldown.
- Static supervisor contract protects the architecture.

## Local evidence

```text
Git Bash syntax: PASS
tests/test_macos_supervisor_contract.ps1: PASS
tests/test_tool_surface_parity.ps1: PASS
tests/test_project_state_contract.ps1: PASS
Windows Release build -warnaserror: PASS, 0 warnings / 0 errors
Windows runtime suite: PASS, 414 assertions
git diff --check: PASS
```

## Native GitHub evidence

Final accepted candidate commit before closure: `f92dc58`

GitHub Actions run: `35723605790`

```text
verify-macos:   SUCCESS
verify-windows: SUCCESS
```

The macOS job therefore proved the real Swift warnings-as-errors typecheck, full runtime integration suite, native app build and legal-resource package verification.

## Result

FPA-003 PASS. macOS now has bounded tunnel crash recovery parity with Windows without retrying or replaying MCP tool calls.

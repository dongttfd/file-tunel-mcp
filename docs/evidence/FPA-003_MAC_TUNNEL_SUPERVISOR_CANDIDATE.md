# FPA-003 - macOS Tunnel Supervisor Candidate Evidence

Date: 2026-09-22
Status: CANDIDATE - NATIVE macOS CI REQUIRED

## Frozen architecture

- docs/design/FPA-003_MAC_TUNNEL_SUPERVISOR_FROZEN.md
- docs/design/FPA-003_INDEPENDENT_REVIEW.md
- docs/design/FPA-003_DECISION_MATRIX.md
- tasks/FPA-003_TASK_GRAPH.md

## Candidate implementation

- Added macOS TunnelRestartPolicy matching Windows defaults.
- Added exponential backoff, 20% jitter, restart-window budget, cooldown and stable-run reset.
- Extended macOS runtime with restarting/cooldown states.
- Captures validated tunnel run launch context after init/doctor.
- Unexpected tunnel exit restarts only tunnel-client; local MCP server and profile lock remain alive.
- Added tunnel generation guard and cancelable restart schedule generation.
- Stop/shutdown cancel pending recovery and invalidate stale child callbacks.
- UI keeps Disconnect available during Restarting/Cooldown.
- Added build/dev/CI/test wiring for TunnelSupervisor.swift.
- Added deterministic policy tests including 10k bounded decisions.
- Added fake-child runtime tests for automatic restart, retained profile lock, stop-cancel and budget cooldown.
- Added static macOS supervisor contract regression test.

## Local gates

```text
Git Bash syntax check: PASS
tests/test_macos_supervisor_contract.ps1: PASS
tests/test_tool_surface_parity.ps1: PASS
tests/test_project_state_contract.ps1: PASS
Windows Release build -warnaserror: PASS, 0 warnings / 0 errors
Windows runtime suite: PASS, 414 assertions
git diff --check: PASS
```

## Remaining acceptance

A real macOS GitHub runner must still prove:

- Swift warnings-as-errors typecheck;
- full tests/test_swift_runtime.sh including policy/runtime supervisor cases;
- build_macos_app.sh;
- bundled legal-resource verification.

FPA-003 remains ACTIVE until native macOS and Windows jobs succeed on this candidate.

# V11-003 — TUNNEL SUPERVISOR EVIDENCE

Date: 2026-09-22
Status: PASS

## Implemented

- Added dedicated tunnel restart policy/supervisor state.
- Unexpected tunnel exits now trigger bounded exponential restart with jitter instead of immediately tearing down the local MCP server.
- Restart budget is enforced per time window; exhaustion enters an explicit cooldown state.
- Stable run duration resets consecutive backoff/budget state.
- Explicit user Stop/Shutdown cancels pending backoff/cooldown immediately.
- Restart logic operates only on the tunnel process lifecycle; it does not capture, queue, replay, or retry MCP tool calls.
- Runtime exposes supervisor evidence: total restarts, cooldown count, consecutive restarts, attempts in window, next restart, last tunnel start/exit and last exit code.
- WPF connection state recognizes Reconnecting/Cooldown and keeps Disconnect available.

## Verification

`dotnet build windows\FileMCP.Windows.sln -c Release -warnaserror`

PASS — 0 warnings, 0 errors.

Full runtime script including vendored tunnel-client local-auth gate:

`./tests/test_windows_runtime.ps1`

PASS — 332 assertions.

New tests prove:

- initial and doubling exponential backoff
- bounded jitter
- restart budget and cooldown decision
- stable-run reset
- real child-process crash/restart/recovery
- runtime cooldown state after budget exhaustion
- user Stop cancels backoff and cooldown in under one second
- no restart remains pending after Stop

Static source review additionally confirms `LocalMcpRuntime` / `TunnelSupervisor` contain no tool-call execution/replay path.

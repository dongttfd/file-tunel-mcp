# V11-004 — COMPONENT HEALTH EVIDENCE

Date: 2026-09-22
Status: PASS

## Implemented

- Added Core health models for persistent telemetry and per-runtime components.
- Persistent telemetry reports Starting / Ready / Degraded with last success, last failure and failure count.
- Telemetry writer, logical-session writer, maintenance cleanup and exact period/retention calls feed the shared persistence-health tracker.
- Runtime health snapshot reports local MCP server readiness, tunnel process state, tunnel health probe state, last check, last successful health probe, restart/backoff state, total restarts and cooldowns.
- Fixed loopback tunnel health endpoints are probed with a bounded 300 ms TCP check; dynamic port `:0` is explicitly `NotConfigured` rather than guessed.
- During tunnel restart/cooldown the local MCP server remains reported ready while the tunnel is down.
- Dashboard workspace cards now show Connected/Reconnecting/Cooldown plus MCP/tunnel/health/last-OK/restart evidence.
- Dashboard persistent telemetry card now reads the Core health snapshot instead of a UI-only boolean.
- Health-probe disposal waits for an in-flight probe, preventing shutdown/dispose races.

## Verification

`dotnet build windows/FileMCP.Windows.sln -c Release -warnaserror`

PASS — 0 warnings, 0 errors.

`./tests/test_windows_runtime.ps1`

PASS — 343 assertions.

New tests cover persistence Ready -> Degraded -> Ready recovery, dynamic/fixed health endpoint parsing, reachable/unreachable loopback probe, last-success preservation, local-server readiness during restart/cooldown, and readiness clearing after stop.

## Packaged WPF evidence

`./build_windows_app.ps1 -Architecture x64`

PASS.

Current interim package SHA-256: `5DB9C5CBECC07F090AAC383BAF50AB1A1FE80EF7A2978E9DFD570AEF8FE8FC54`.

`./tests/test_windows_app.ps1`

- windows-app-startup: PASS
- windows-close-to-tray: PASS
- windows-packaged-tunnel-client: PASS
- windows-packaged-sqlite-write-read: PASS

This package is interim V1.1 evidence only; final package evidence must be regenerated after V11-008/OBS-013.

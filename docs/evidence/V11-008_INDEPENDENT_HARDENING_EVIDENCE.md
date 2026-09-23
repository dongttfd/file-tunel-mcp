# V11-008 — INDEPENDENT HARDENING EVIDENCE

Date: 2026-09-22
Status: PASS

## Scope

Independent hardening audit of the complete Observability V1.1 implementation before the deferred OBS-013 live ChatGPT proof.

The audit intentionally tried to break the new code under long-lived churn, retention pressure, restart storms, exporter outages and privacy-sensitive traffic.

## Round 1 — 24h correlation churn

Simulation: one new logical chat handle per minute for 24 hours with one-hour retention.

Result:

- collection cardinality remained bounded by TTL rather than process lifetime
- more than 1,300 expired handles were reclaimed during the simulated day
- no unbounded correlation-handle accumulation was observed

## Round 2 — 24h logical-session churn

Simulation: one connect-only logical session per minute for 24 hours.

Finding: the audit found a real implementation gap. Sessions with zero workspace/tool activity had no durable workspace row, and the V11-001 eviction predicate required `Workspaces.Count > 0`. After becoming stale, a connect-only session could therefore remain in memory indefinitely and eventually consume the configured session cap.

Fix:

- connect-only sessions are now eligible for stale/TTL and pressure eviction after their pending state has been drained
- sessions that do contain workspace/tool usage still require the final stale state to have been durably persisted before eviction
- in-flight sessions remain non-evictable

Post-fix result:

- one-day connect-only churn remains bounded
- more than 1,300 old connect-only sessions are reclaimed in the simulation

## Round 3 — Capacity pressure

A registry capped at eight sessions was filled with connect-only sessions, aged beyond the stale threshold but below the normal TTL, and then given a ninth session.

Result:

- exactly one stale connect-only entry was pressure-evicted
- the new session was admitted
- registry cardinality stayed at the configured cap

## Round 4 — High-volume SQLite retention

A synthetic database was seeded with:

- 12,000 expired minute rows
- 120 recent minute rows
- 4,000 expired hour rows
- 72 recent hour rows
- 500 historical day rows

After the production retention routine ran:

- minute rows = 120
- hour rows = 72
- day rows = 500

This proves the minute/hour disk-history bounds under bulk cleanup while long-term day history remains intact.

## Round 5 — Restart storm

The tunnel restart policy was driven through 10,000 crash decisions inside one restart window.

Result:

- attempts-in-window never exceeded the configured restart budget
- consecutive retry state stayed bounded at the configured budget
- all decisions after budget exhaustion remained cooldown decisions instead of growing an unbounded retry loop

The existing real-process runtime tests also continue to prove crash/restart/recovery, cooldown and immediate user-stop cancellation.

## Round 6 — OTLP outage isolation

The V11-007 exporter-down gate remains green:

- unreachable collector does not fail a valid MCP operation
- exporter flush remains timeout-bounded
- exporter is disabled by default and generates zero collector requests while disabled

## Round 7 — OTLP protobuf privacy

A real MCP `read_file` request was executed using:

- a private filename marker
- private file-content marker
- real raw `_filemcp_chat` correlation handle

Traces and metrics were then exported to the local OTLP test collector and the raw protobuf bodies were scanned.

Result:

- private filename marker absent
- private file-content marker absent
- raw logical chat handle absent

## Round 8 — Durable privacy regression

The existing hardening suite still verifies that SQLite/WAL telemetry contains no private file content, private path/search terms or raw chat handles.

## Round 9 — Build / integration regression

`dotnet build windows\FileMCP.Windows.sln -c Release -warnaserror`

PASS — 0 warnings, 0 errors.

`./tests/test_windows_runtime.ps1`

PASS — 414 assertions.

## Round 10 — Release/package regression

`./build_windows_app.ps1 -Architecture x64`

PASS.

`./tests/test_windows_app.ps1`

PASS:

- WPF startup
- close-to-tray
- packaged tunnel-client
- packaged native SQLite create/write/read
- packaged OTLP provider
- packaged OpenTelemetry license/notices

Package SHA-256 at this hardening gate:

`5B511CC07ADC85D19F5F1C5B1E6CBFC0903FEF0AF8A0F8CFEBF8BFEB577DC44E`

## Round 11 — Dependency audit

- no vulnerable NuGet packages reported
- no direct package updates reported

## Round 12 — Architectural invariants

Still preserved after all V1.1 upgrades:

- telemetry failure does not fail valid MCP execution
- no automatic replay/retry of side-effecting tool calls
- raw prompts/tool arguments/results are not persisted as observability data
- logical chat correlation grants no authority
- transport/session semantics are not used as proof of a ChatGPT conversation
- exact AI-chat wording remains disabled pending the live OBS-013 proof

## Final conclusion

V11-008 PASS.

The hardening audit found and fixed one real leak-class bug in connect-only logical-session retention. After the fix, all long-lived churn, retention, restart, exporter, privacy, runtime and packaged-app gates pass.

The next gate is OBS-013: reconnect ChatGPT to this upgraded package, prove `filemcp_observability_connect` and `_filemcp_chat` propagation through the real connector, then close V11-009/final MAIN verification.

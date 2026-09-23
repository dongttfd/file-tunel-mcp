# OBSERVABILITY V1.1 — INDEPENDENT MULTI-ROUND AUDIT

Status: FINAL AUDIT COMPLETE — NO FEATURE CODE CHANGED
Date: 2026-09-22

## Round 1 — Scope sanity

Question: should FileMCP become a full MCP gateway platform?

Result: NO. FileMCP's core role is a local execution bridge for ChatGPT Web. Gateway federation, multi-tenant routing, Kubernetes control planes and backend failover are out of scope unless a future product decision changes that role.

## Round 2 — Hot-path telemetry

Question: is FileMCP's custom atomic meter inferior enough to replace?

Result: NO. The current atomic hot path is appropriate for a local desktop bridge and already has concurrency tests. Replacing it with exporter-owned counters would increase coupling. The correct upgrade is to mirror selected counters/spans into OpenTelemetry while keeping the local meter authoritative for the dashboard.

## Round 3 — Standards interoperability

Question: does FileMCP need OpenTelemetry?

Result: YES, optional. ToolHive and ContextForge demonstrate mature interoperability benefits. Native .NET `ActivitySource`/`Meter` gives a clean integration point and OTLP can remain disabled by default.

Risk: current MCP semantic conventions are still catching up with protocol 2026-07-28. Therefore FileMCP must implement a compatibility/version layer and avoid using `mcp.session.id` as proof of a logical ChatGPT conversation.

## Round 4 — Logical chat correctness

Question: is FileMCP's application-level chat correlation still the right design?

Result: YES for modern MCP. Microsoft/Soth transport-session code solves a different problem. However FileMCP's current registry lifetime is unbounded and must be hardened with TTL/cap/cleanup.

## Round 5 — Memory-growth attack / 24x7 operation

Finding: two unbounded collections exist in the current logical-chat path:

- `LogicalSessionRegistry._sessions`
- `LogicalChatCorrelationService._knownHashes`

Stale sessions are hidden, not deleted.

Conclusion: P0 hardening required before calling long-lived observability production-complete.

## Round 6 — Disk-growth / retention truth

Finding: SQLite retention code exists but no production maintenance loop invokes it.

Conclusion: the documented 35-day / 90-day retention is currently a capability, not an automatically enforced invariant. P0 scheduler required.

## Round 7 — Tunnel 24/7 resilience

Finding: an unexpected `tunnel-client` exit transitions runtime to Failed and tears down the server; no automatic retry occurs.

Conclusion: this conflicts with the user's 24/7 operating goal. Add a dedicated supervisor with bounded exponential backoff and restart budget. Do not use generic retry-on-everything logic.

## Round 8 — Retry safety

Question: can gateway retry middleware be copied directly?

Result: NO. Retrying arbitrary FileMCP tool calls could duplicate side effects (`write_file`, `git_commit`, shell commands). Auto-retry belongs only to the tunnel process lifecycle and explicitly idempotent control operations. Tool calls themselves remain at-most-once from FileMCP's perspective.

## Round 9 — Health model

Finding: Soth's liveness/readiness/component checks are a better conceptual model than a single status. FileMCP should report per-workspace component health and restart state internally.

Security decision: no new remotely exposed unauthenticated health endpoint by default.

## Round 10 — Telemetry privacy

Finding: richer tracing systems often capture request/query/payload attributes. FileMCP currently has stronger default privacy because it persists only counters and hashes.

Conclusion: any OTel implementation must enforce a metadata allowlist. Tool arguments/results remain excluded. W3C baggage is not accepted blindly because it can carry sensitive content.

## Round 11 — Cardinality/DoS

ToolHive explicitly bounds client-controlled metric label values because cumulative metric readers retain distinct attribute sets. This is directly relevant if FileMCP exports tool/client attributes.

Conclusion: add attribute length caps, allowlisted dimensions and bounded cardinality tests before enabling exporters.

## Round 12 — Trace propagation

MCP `_meta` trace context is valuable for correlating ChatGPT/MCP requests when provided. FileMCP should extract W3C `traceparent`/`tracestate` into an `Activity` parent without treating trace identity as chat identity.

## Round 13 — Chart replacement

ScottPlot/LiveCharts2 are technically stronger, but at FileMCP's 1 Hz × 900-point use case the current WPF Polyline is simpler and sufficient. Dependency/native-package cost outweighs benefit today.

Conclusion: no chart replacement in V1.1.

## Round 14 — Prometheus endpoint

A dedicated endpoint improves infrastructure integration but increases listener/security/packaging scope. Optional OTLP export is enough for V1.1.

Conclusion: reject default Prometheus listener.

## Round 15 — Source reuse / maintenance risk

Directly embedding source from Go/Rust/Python projects into a .NET desktop app creates translation and provenance burden. Architecture-level reuse is safer. Where a mature native .NET package exists (OpenTelemetry), use the package rather than copy implementation internals.

## Round 16 — Failure isolation

New maintenance/exporter/supervisor components must never break valid MCP execution:

- retention failure => telemetry degraded, request path continues
- OTLP exporter down => no request failure
- tunnel restart exhaustion => explicit Failed/Cooldown state, no tight loop
- health check timeout => degraded signal, no blocking request thread

## Round 17 — Packaging

OpenTelemetry core instrumentation can remain mostly managed. Any exporter package added must be verified in self-contained single-file x64 packaging. No chart/native renderer dependency is introduced in V1.1.

## Round 18 — Final independent conclusion

The current Observability V1 code should NOT be replaced wholesale. Its local-first counter/persistence/privacy architecture is good.

The strongest V1.1 strategy is additive hardening:

1. bound memory
2. enforce retention automatically
3. supervise tunnel restarts
4. add optional standards telemetry
5. improve component health

These changes make FileMCP materially stronger while preserving its small local-bridge architecture.

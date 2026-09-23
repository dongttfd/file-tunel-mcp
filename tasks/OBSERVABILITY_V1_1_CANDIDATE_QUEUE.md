# OBSERVABILITY V1.1 - TASK QUEUE

Status: IMPLEMENTATION + HARDENING COMPLETE THROUGH V11-008
Rule: OBS-013 live connector proof now runs against the fully upgraded build; V11-009 closes final merge/main verification.

| ID | State | Depends | Purpose / acceptance |
|---|---|---|---|
| V11-001 | PASS | - | Bounded logical session/correlation lifecycle: TTL, cap, cleanup, never evict in-flight, pressure metrics, concurrency tests |
| V11-002 | PASS | V11-001 | Process-wide maintenance worker and automatic SQLite/session/correlation retention |
| V11-003 | PASS | - | Tunnel supervisor: exponential backoff/jitter, restart budget, stable reset, cooldown, Stop cancellation; no tool replay |
| V11-004 | PASS | V11-003 | Component health model and dashboard integration |
| V11-005 | PASS | - | ActivitySource + Meter, versioned MCP semantic adapter, privacy/cardinality policy |
| V11-006 | PASS | V11-005 | W3C trace-context extraction from MCP `_meta` with HTTP fallback and identity separation |
| V11-007 | PASS | V11-005,V11-006 | Optional official OpenTelemetry .NET OTLP exporter/settings, failure isolation, package proof |
| V11-008 | PASS | V11-002,V11-004,V11-007 | 24h-style churn, memory-cardinality/disk-row bounds, restart storm, exporter failure, privacy audit |
| V11-009 | EXTERNAL-BLOCKED | V11-008,OBS-013 | Final review/package/live E2E/merge/main verification |

## Dependency graph

```text
V11-001 --> V11-002 -----------+
V11-003 --> V11-004 -----------+--> V11-008 --> OBS-013 --> V11-009
V11-005 --> V11-006 --> V11-007+
```

## Deliberately excluded

No LiveCharts2/ScottPlot migration, no Prometheus server, no full proxy circuit-breaker/hedging, no distributed session store, no payload trace persistence.

NEXT_EXACT_ACTION: upstream WRITE/MAINTAIN authority merges PR #2; then fetch upstream main and perform final merged-main confirmation.

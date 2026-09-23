# OBSERVABILITY V1 — TECHNOLOGY / SOLUTION MATRIX

Status: FINAL COMPARISON COMPLETE

## 1. Durable telemetry store

| Option | Strength | Weakness | Decision |
|---|---|---|---|
| JSON/JSONL | no dependency, easy inspect | weak concurrent updates, poor range queries, manual recovery/compaction | Reject |
| LiteDB | embedded .NET, simple API | extra datastore semantics, less standard operational tooling | Reject |
| Windows Event Log/ESE | OS-native possibilities | wrong abstraction, difficult aggregation/query UX | Reject |
| SQLite + Microsoft.Data.Sqlite | transactional, WAL, indexes, range queries, schema migrations, standard tooling | adds NuGet/native packaging dependency | SELECT |

## 2. Hot-path collection

| Option | Strength | Weakness | Decision |
|---|---|---|---|
| direct SQLite per request | simple source of truth | IO/lock latency coupled to MCP request | Reject |
| bounded Channel per event | clean producer/consumer | overload needs drop/backpressure policy; many objects/events | Reserve |
| ConcurrentQueue + timer | easy prototype | manual memory/backpressure correctness | Reject |
| Interlocked counters + periodic delta drain | minimum allocation, deterministic, non-blocking request path | careful drain/retry code required | SELECT |

Selected pattern: request path updates hot atomic counters; one writer periodically drains deltas. If DB commit fails, writer retains pending delta and retries while new hot counters continue accumulating.

## 3. Token reporting

| Option | Strength | Weakness | Decision |
|---|---|---|---|
| exact model tokenizer | familiar token unit | unknown model/context, model-specific encodings, still not total ChatGPT usage | Reject for V1 |
| bytes / 4 estimate | deterministic, cheap, matches observed ChatCode implementation approach | approximation | SELECT, explicitly labeled |
| bytes only | exact | user asked for tokens | Always show alongside estimate |

Estimator id: `bytes_div_4_v1`, formula `ceil(utf8_payload_bytes / 4)`.

## 4. Chat/session identity

| Option | Correct for distinct chat? | Notes | Decision |
|---|---|---|---|
| TCP connection | No | HTTP requests are short-lived | Reject |
| tunnel id | No | identifies configured workspace tunnel | Reject |
| clientInfo | No | identifies client implementation | Reject |
| traceparent | No | distributed trace, usually turn/request scoped | Reject as chat identity; useful later for diagnostics |
| MCP Mcp-Session-Id | No for modern protocol | removed in MCP 2026-07-28 | Reject |
| heuristic idle grouping | No | merges concurrent chats | Reject for exact count |
| explicit FileMCP logical handle | Yes when propagated | requires one automatic connect call and handle propagation | SELECT |

Migration-safe rollout:
1. Add optional logical handle support.
2. Track bound and unbound traffic separately.
3. Live-test ChatGPT propagation.
4. Enable exact `AI chats` label only after proof; otherwise display `Observed MCP sessions`.

## 5. UI technology

| Option | Strength | Weakness | Decision |
|---|---|---|---|
| existing WPF | one app, no new server, matches current code | chart components must be implemented | SELECT |
| embedded local web dashboard | flexible charts/UI | extra HTTP surface/security/lifecycle | Reject for V1 |
| external browser dashboard | easy web stack | breaks one-app goal | Reject |

## 6. Realtime chart

Selected: fixed-size in-memory ring buffer sampled from hot snapshots, rendered by lightweight WPF drawing. No second-level SQLite writes.

## 7. Health data

Truth priority:
- Local MCP server ready: exact.
- runtime/tunnel process running: exact process state.
- tool request latency: exact measured locally.
- tunnel ready/health: use tunnel-client local health/metrics only when a resolvable health endpoint is configured.
- Internet RTT: do not invent/ping arbitrary third-party hosts merely to imitate ChatCode.

## 8. Time-series query source

- realtime: RAM ring buffer
- Today/Yesterday: minute rows within local-time UTC bounds
- 7d/30d: hour rows within local-time UTC bounds
- long-term/all-time: day rows, with edge correction when necessary

## 9. Session inactivity policy

Initial UX policy, independently configurable from identity:
- active: in-flight tool call OR idle <= 45s
- idle: >45s and <=30m
- stale: >30m; hidden from default live list

This status is an activity state, not proof that the model is generating text outside FileMCP.

## Final selected stack

.NET 8 + existing WPF
+ Interlocked hot meter
+ periodic delta writer
+ Microsoft.Data.Sqlite / SQLite WAL
+ UTC rollups
+ explicit logical chat handle
+ immutable dashboard snapshots
+ no persisted content.

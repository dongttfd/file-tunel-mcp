# FILEMCP OBSERVABILITY V1 — INDEPENDENT FINAL AUDIT

Status: FINAL AUDIT COMPLETE
Baseline: main @ dd9df8effb5b8ebdf221bcd0cfc88a5b4cec4be3
Audit target: requested ChatCode-style local observability on top of FileMCP multi-drive C/D/E/F.

## Round 1 — Product truth
Goal: prevent a visually impressive but semantically false dashboard.
Finding: FileMCP sees MCP traffic only. It does not see full ChatGPT history, system prompt, memory, reasoning, or ordinary assistant output.
Decision: bytes/calls/categories/time are authoritative; token values are labeled MCP payload estimates.

## Round 2 — Current FileMCP topology
Finding: MainWindow owns four LocalMcpRuntime instances. Each runtime owns a loopback LocalMcpServer and one Secure MCP Tunnel profile. LocalMcpServer is the common request/response choke point.
Decision: observability belongs in Core, not WPF. MainWindow consumes snapshots.

## Round 3 — ChatCode implementation evidence
Installed ChatCode 2.1.5 exposes usage_statistics/live_sessions modules, SQLite telemetry fields, bytes_div_4_estimate, logical chat identity, transport identity and last-seen state.
Decision: reuse architecture ideas only; implement independently in C#/.NET.

## Round 4 — Modern MCP protocol
Finding: MCP 2026-07-28 removes initialize/initialized and Mcp-Session-Id. Each request is stateless and carries protocol/client metadata per request.
Decision: protocol transport cannot provide an exact logical chat count. FileMCP needs explicit application-level correlation if it wants exact chat counts.

## Round 5 — Session/chat identity alternatives
Rejected:
- TCP socket count: invalid because FileMCP closes each HTTP response connection.
- tunnel ID: identifies drive/app tunnel, not chat.
- clientInfo: identifies client implementation, not chat instance.
- traceparent: trace correlation, not durable conversation identity.
- idle-window heuristic alone: can merge two simultaneous chats.
Selected: explicit FileMCP chat handle negotiated by a lightweight connect tool, then optionally propagated on later tool calls. Unbound traffic remains measurable but is not claimed as a distinct chat.

## Round 6 — Hot-path performance
Compared per-request SQLite writes, Channel event queue, ConcurrentQueue+timer, and atomic counters with periodic delta drain.
Decision: atomic/Interlocked hot counters + periodic delta snapshot. Request path never blocks on database IO. Failed database flush retains the pending delta for retry.

## Round 7 — Persistence
Compared JSONL, plain JSON rollups, LiteDB, Windows event storage, and SQLite.
Decision: SQLite WAL through Microsoft.Data.Sqlite. It gives atomic upserts, bounded historical queries, crash resilience, schema versioning, retention and familiar diagnostics.

## Round 8 — Rollup model
Finding: second-level durable rows would create unnecessary write amplification.
Decision: realtime seconds stay in RAM; durable minute/hour/day counters are updated from the same delta in one transaction. Minute retention 48h, hour 90d, day long-term.

## Round 9 — Privacy/security
Threat: MCP arguments/responses may contain source code, secrets, command strings and personal data.
Decision: persist numeric counters, normalized tool category/name, workspace key, status/timestamps and opaque session IDs only. Never persist MCP bodies or tool arguments. Resume/correlation tokens are non-authoritative and must not become permission credentials.

## Round 10 — Multi-drive correctness
Finding: one process contains four independent runtime boundaries. Aggregation is safe only if every metric is tagged by workspace key before aggregation.
Additional P0 finding: SafePathResolver currently mishandles a drive root such as D:\ when building its descendant prefix; nested file-tool paths can be rejected even though root listing succeeds.
Decision: FMR-001 drive-root containment regression fix is the first implementation task after freeze.

## Round 11 — UI semantics
Decision:
- App uptime, per-drive connected uptime and session online time are separate.
- Period usage is separate from realtime activity.
- `Tác vụ thực thi` means mutating/execution tools, not all MCP requests.
- `Tổng lượt gọi` means all tool calls.
- `MCP requests` may be shown separately for discovery/ping/list/call traffic.
- Live table says `Observed MCP sessions` until logical chat handle behavior is live-proven with ChatGPT; only then may UI say `AI chats`.

## Round 12 — Failure isolation
Decision: telemetry failure never fails a valid MCP operation. DB errors are rate-limited in logs, writer retries pending deltas, UI shows telemetry degraded state, and MCP remains operational.

## Round 13 — Time boundaries
Finding: user-local Today/Yesterday cannot be represented safely by UTC-day buckets alone.
Decision: persist UTC minute/hour/day buckets; period query computes user-local boundaries and sums minute/hour buckets for exact short-range totals. Day buckets are used for long-term history.

## Round 14 — Packaging
Finding: FileMCP publishes a self-contained Windows x64 app.
Decision: SQLite native dependency inclusion becomes an explicit packaging gate. Build success alone is insufficient; packaged executable must open database and write/read a smoke record.

## Round 15 — Release proof
Required final evidence:
- existing containment/Git/HTTP/runtime tests unchanged and passing;
- drive-root regression tests;
- concurrency and flush-failure tests;
- no-content persistence audit;
- period query boundary tests;
- session-state tests;
- packaged x64 smoke;
- one live ChatGPT MCP proof for chat-handle propagation before exact chat-count label is enabled.

## Final conclusion
No architectural blocker exists for usage counters, payload/token estimates, task/tool categories, historical periods, uptime, per-drive aggregation, realtime graphs, or observed-session activity.
Exact distinct ChatGPT conversation count is feasible only through explicit application-level correlation and must remain feature-gated until live-proven.

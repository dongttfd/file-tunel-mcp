# FILEMCP OBSERVABILITY V1 — FROZEN ARCHITECTURE

Status: FROZEN FOR IMPLEMENTATION
Freeze date: 2026-09-22
Inputs:
- IDEA_INBOX.md
- OBSERVABILITY_V1_LIVING_DESIGN.md
- INDEPENDENT_OBSERVABILITY_AUDIT_V1.md
- TECHNOLOGY_DECISION_MATRIX_OBSERVABILITY_V1.md

## 1. Product scope

One FileMCP desktop app provides truthful local observability across C/D/E/F:
- MCP token input/output/total estimates
- exact input/output/total payload bytes
- tool calls and execution-task counts
- read/write/command/Git/skill/other/error counts
- app and per-drive runtime uptime
- per-drive + global aggregation
- Today / Yesterday / 7d / 30d
- realtime activity graph
- observed live MCP work contexts
- exact logical AI chat count only after logical-handle live proof

## 2. Hard invariants

1. Telemetry cannot change tool behavior or security decisions.
2. No request/response/tool-argument content is persisted.
3. Multi-drive runtime containment remains independent.
4. Token estimates are never labeled as model/account usage.
5. TCP sockets are never counted as chats.
6. Database failure cannot fail a valid MCP operation.
7. UI never owns the source-of-truth counters.

## 3. Prerequisite correctness fix

FMR-001 fixes SafePathResolver drive-root containment before observability code.
For a root `D:\`, descendants must resolve/contain correctly without producing a `D:\\...` comparison prefix. Existing traversal/junction protections must remain intact.

## 4. Core object model

### UsageEstimator
`EstimateTokenUnits(int utf8Bytes) -> long`
V1 estimator id: `bytes_div_4_v1`.
Formula: ceil(bytes / 4).

### ToolClassifier
Normalizes tool names into:
- Read
- Write
- Command
- Git
- Skill
- Other
and `IsExecutionTask` for mutating/execution tools.

### UsageCounterSet
Atomic long counters:
- mcpRequests
- toolCalls
- executionTasks
- requestBytes
- responseBytes
- tokensInEst
- tokensOutEst
- errors
- readCalls
- writeCalls
- commandCalls
- gitCalls
- skillCalls
- otherCalls
- totalLatencyTicks
- maxLatencyTicks

### WorkspaceUsageMeter
One meter per workspace key. Request path performs only Interlocked-style updates.
Exposes immutable current snapshot and atomic delta drain.

### ObservabilityHub
Process-wide service containing meters for C/D/E/F, app start monotonic timestamp, session registry, realtime ring buffers, store writer and query service.

## 5. MCP instrumentation boundary

LocalMcpServer receives `workspaceKey` + `IObservabilitySink`.

Request lifecycle:
1. HTTP parsing/auth/security validation remains unchanged.
2. Once a valid MCP JSON request is accepted, record incoming body bytes and request type.
3. For tools/call, classify normalized tool name and mark start time.
4. Dispatch existing tool code unchanged.
5. Determine success/error from produced result.
6. Record call/category/error/latency.
7. Record MCP response body bytes before HTTP framing.

Do not count malformed/unauthenticated traffic as AI usage. Security diagnostics may log aggregate rejection counters separately later.

## 6. Metric semantics

`Token vào*`: estimate of accepted MCP JSON request payload bytes / 4.
`Token ra*`: estimate of MCP JSON response payload bytes / 4.
`Tổng token*`: in + out.
`Dữ liệu vào/ra`: exact MCP JSON payload bytes.
`Tổng lượt gọi`: tools/call count.
`Tác vụ thực thi`: mutating/execution tool calls (Write + Command + mutating Git).
`Lỗi`: tool result isError=true or dispatch exception.

UI footnote: `* Ước tính từ dữ liệu MCP; không phải token toàn bộ ChatGPT/model.`

## 7. Persistence architecture

File:
`%LOCALAPPDATA%\FileMCP\observability-v1.sqlite3`

Provider:
`Microsoft.Data.Sqlite`.

SQLite startup pragmas:
- journal_mode=WAL
- synchronous=NORMAL
- busy_timeout=5000
- foreign_keys=ON

Schema version stored in meta.

Tables:
### telemetry_meta
key TEXT PRIMARY KEY, value TEXT NOT NULL

### usage_minute / usage_hour / usage_day
workspace_key TEXT NOT NULL
bucket_epoch INTEGER NOT NULL
mcp_requests INTEGER NOT NULL
... all UsageCounterSet numeric fields except transient latency ticks may be normalized to microseconds ...
PRIMARY KEY(workspace_key, bucket_epoch)

### logical_sessions
session_hash TEXT PRIMARY KEY
created_epoch INTEGER NOT NULL
last_seen_epoch INTEGER NOT NULL
client_name TEXT NULL
state TEXT NOT NULL

### logical_session_workspace
session_hash TEXT NOT NULL
workspace_key TEXT NOT NULL
first_seen_epoch INTEGER NOT NULL
last_seen_epoch INTEGER NOT NULL
... session usage counters ...
PRIMARY KEY(session_hash, workspace_key)

No request/response bodies or arguments.

## 8. Writer algorithm

- Background writer interval target: 1 second.
- Drain each WorkspaceUsageMeter atomically into a pending in-memory batch.
- Merge pending batch into current UTC minute/hour/day buckets in one SQLite transaction.
- Commit success clears pending batch.
- Commit failure keeps pending batch intact and retries; error logging is rate-limited.
- Shutdown performs bounded final flush.
- Dashboard period query adds currently-undrained hot counters to durable totals when range includes now.

No event queue is required for V1.

## 9. Retention

- realtime second samples: RAM ring buffer only, target 15 minutes
- usage_minute: 35 days
- usage_hour: 90 days
- usage_day: long-term

Cleanup runs off request path.

## 10. Time semantics

All storage timestamps are UTC epoch seconds.
UI receives user-local timezone and computes period UTC boundaries.
Today/Yesterday use minute rows for exact local boundaries.
7d/30d use hour rows.
Long-term queries use day rows with boundary correction when necessary.

## 11. Logical chat identity

MCP 2026-07-28 is stateless; therefore logical identity is FileMCP application state.

Add tool: `filemcp_observability_connect`.
It creates or resumes an opaque, non-authoritative `chat_instance_id` and returns it.

Rollout rules:
- `chat_instance_id` is correlation metadata only; it grants no file/command/Git authority.
- All operational tool schemas gain an optional reserved `_filemcp_chat` string field at the server facade; it is stripped before strict LocalTools argument validation.
- server/discover instructions ask the AI to call observability_connect once per chat context and reuse the returned id on later calls.
- traffic without an id is still counted globally/per-drive but placed in `unbound` session traffic.
- session id is hashed before durable storage; raw id exists only in caller context and transient memory lookup as needed.
- exact `AI chats` label remains disabled until a real ChatGPT live proof shows stable propagation across multiple turns.

Session activity:
- active: in-flight call OR idle <=45s
- idle: >45s and <=30m
- stale: >30m, hidden by default

## 12. Runtime uptime

App uptime uses process monotonic start.
Each LocalMcpRuntime records Running-start monotonic timestamp and clears on stop.
Dashboard shows both app uptime and per-drive connected uptime.
These are not called AI working time.

## 13. Realtime

Per workspace and global ring buffer samples normalized snapshots every second.
Graph displays tool-call/token-estimate deltas, not raw content.
No second-resolution DB writes.

## 14. UI architecture

New first tab: Overview.
Existing Connection / Settings / Logs remain.

Overview sections:
1. local date/time + period selector
2. system/runtime status
3. usage top cards: token estimates + bytes
4. activity row: execution tasks, calls, read/write/command/Git/skill/errors
5. C/D/E/F cards: status, uptime, period totals
6. live observed sessions table
7. selected session detail
8. realtime graph

Refresh:
- hot snapshot: 1 second
- durable historical query: on period change and throttled refresh
- UI work is cancellable and never blocks MCP request threads

## 15. Health semantics

Exact:
- LocalMcpServer ready
- runtime/tunnel process state
- connected uptime
- MCP tool latency

Conditional:
- tunnel ready/metrics only if local tunnel-client health endpoint is resolvable

Not shown unless measured:
- arbitrary Internet RTT

## 16. Package impact

Adding Microsoft.Data.Sqlite requires self-contained win-x64 publish verification including native SQLite assets. Packaged smoke must create/open/write/read telemetry DB.

## 17. Acceptance gates

G01 drive-root descendant containment works and escape tests still pass.
G02 estimator/classifier deterministic.
G03 concurrent hot counters exact under load.
G04 delta drain does not lose increments during successful flush.
G05 failed DB flush retains pending delta.
G06 WAL store/query/retention/migration tests pass.
G07 no content/arguments appear in DB.
G08 MCP response bytes/semantics unchanged by instrumentation.
G09 all legacy security tests pass.
G10 C/D/E/F aggregation equals sum of per-drive snapshots.
G11 app/runtime uptime semantics verified.
G12 local-time period boundary tests pass.
G13 unbound traffic is never presented as exact chat.
G14 logical session state machine tests pass.
G15 live ChatGPT proof validates id propagation before exact chat label enabled.
G16 WPF cards match Core snapshots.
G17 realtime graph uses deltas and bounded memory.
G18 packaged x64 app + SQLite smoke passes.

Any change to these frozen semantics requires an ADR update before code changes.

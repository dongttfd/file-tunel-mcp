# OBSERVABILITY V1.1 — DECISION MATRIX

Status: USER-APPROVED / ARCHITECTURE FROZEN FOR POST-OBS-013 IMPLEMENTATION

| Area | Current FileMCP | External reference | Decision |
|---|---|---|---|
| Hot counters | Interlocked custom meter | OTel / Prometheus counters | KEEP current source of truth |
| Local history | SQLite minute/hour/day | ContextForge DB traces | KEEP SQLite counters; do not store payload traces |
| Session cleanup | stale hidden only | Soth TTL/max/cleanup, Microsoft expiring cache | UPGRADE FileMCP |
| Correlation registry | unbounded known hashes | bounded session managers | UPGRADE FileMCP |
| Retention | cleanup API only | scheduled maintenance patterns | UPGRADE FileMCP |
| Tunnel crash recovery | Failed only | retry/backoff/resilience gateways | UPGRADE selected lifecycle subset |
| Distributed tracing | none | ToolHive + OTel .NET | ADD optional native .NET OTel bridge |
| MCP trace propagation | none | ToolHive `_meta` W3C propagation | ADD extraction, version-aware |
| Metrics cardinality | custom fixed fields | ToolHive label bounding | ADD guards for exported attributes |
| Health | dashboard status | Soth liveness/readiness/component health | ADD internal component model |
| Prometheus listener | none | prometheus-net/Soth | DEFER |
| Full gateway resilience | none | josh mcp-proxy | REJECT for current topology |
| Session affinity | app-level chat correlation | Microsoft gateway protocol session affinity | REJECT as chat identity |
| Chart engine | WPF Polyline | LiveCharts2 / ScottPlot | KEEP current graph |
| Protocol diagnostics | custom | MCP Inspector | BORROW concepts only |

## Selected post-OBS-013 architecture

### A. Bounded logical observability state

- configurable maximum live/stale logical sessions
- stale-session eviction after persisted final state
- correlation-hash TTL and cap
- deterministic cleanup via `PeriodicTimer`
- never evict an in-flight session
- counters for evictions/cap pressure

### B. Observability maintenance worker

One cancellable process-wide maintenance service handles:

- SQLite minute/hour retention cleanup
- durable stale logical-session cleanup
- in-memory logical-session/correlation cleanup
- periodic health snapshot refresh

Maintenance failure is isolated and rate-limited in logs.

### C. Tunnel supervisor

Per workspace:

- unexpected exit detection
- exponential backoff with bounded jitter
- restart attempt budget per time window
- stable-run reset
- cooldown/exhausted state
- explicit user Stop cancels pending restart immediately
- no automatic replay/retry of MCP tool calls

### D. Optional OpenTelemetry bridge

Use native .NET primitives:

- `ActivitySource` for accepted MCP operation spans
- `Meter` for MCP request/tool/error/duration metrics
- optional OTLP SDK/exporter behind settings
- zero exporter/network work when disabled
- metadata allowlist only
- no tool args/results/raw correlation handles
- `_filemcp_chat` remains FileMCP-only application correlation and must not be mapped blindly to protocol `mcp.session.id`

### E. 2026-aware MCP trace adapter

- extract W3C context from `params._meta` when valid
- optionally extract HTTP trace context as fallback
- MCP `_meta` takes precedence for MCP-operation parent context
- trace context never grants authority and never defines chat identity
- semantic attribute set is versioned because current OTel MCP conventions are still evolving for 2026-07-28

### F. Component health snapshot

Per workspace:

- local server ready
- tunnel process state
- tunnel health reachable when address is resolvable
- telemetry persistence ready/degraded
- restart attempt/backoff/cooldown state
- last successful tunnel health timestamp

No public remote health listener is enabled by default.

## Non-goals

- multi-backend proxy routing
- Kubernetes control plane
- multi-tenant RBAC gateway
- tool-call automatic retry
- request/response audit-content storage
- replacing WPF chart stack
- Prometheus listener by default

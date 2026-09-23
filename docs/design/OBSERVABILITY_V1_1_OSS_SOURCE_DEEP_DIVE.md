# OBSERVABILITY V1.1 — OSS SOURCE DEEP DIVE

Status: DESIGN RESEARCH COMPLETE — NO FEATURE CODE CHANGED
Date: 2026-09-22
Parent: FileMCP Observability V1 (OBS-013 still awaiting live ChatGPT connector proof)
Research workspace: D:\FileMCP-OSS-Audit

## Goal

Determine whether mature open-source implementations solve the same problems better than FileMCP's custom V1 code and whether selected ideas/code patterns should strengthen FileMCP after OBS-013.

## Repositories audited at source level

| Repository | Audit HEAD | Primary relevant area | License |
|---|---:|---|---|
| stacklok/toolhive | 3c2a235 | MCP OpenTelemetry, MCP semantic attributes, trace propagation, label-cardinality defense | Apache-2.0 |
| open-telemetry/opentelemetry-dotnet | 44d841f | .NET ActivitySource/Meter SDK and exporters | Apache-2.0 |
| IBM/mcp-context-forge | 1950bcb | best-effort observability isolation, trace persistence, redaction/sampling, MCP runtime telemetry | Apache-2.0 |
| microsoft/mcp-gateway | 3594c4e | C# session routing/store, cache TTL, scoped-session defenses | MIT |
| joshrotenberg/mcp-proxy | 965ca25 | retry budgets, circuit breaker/outlier/failover, metrics middleware | dual MIT/Apache-2.0 |
| soth-ai/mcp-proxy | b198e9d | bounded session manager, TTL cleanup, max-session cap, health/readiness, Prometheus | MIT |
| modelcontextprotocol/inspector | 2e90a62 | modern/legacy protocol-era diagnostics and connection history | MIT (package metadata) |
| TheLunarCompany/lunar | d22d2b8 | MCP aggregation/traffic visibility/policy patterns | MIT |
| Live-Charts/LiveCharts2 | 8537b90 | WPF realtime/interactive charts | MIT |
| ScottPlot/ScottPlot | fb3f5aa | high-rate streaming WPF chart/DataStreamer | MIT |
| prometheus-net/prometheus-net | 60e9106 | .NET counters/gauges/histograms | MIT |

## Current FileMCP baseline compared

Current FileMCP already has a strong custom local-first architecture:

- atomic hot counters (`WorkspaceUsageMeter`)
- content-free SQLite rollups (`TelemetryPersistence`)
- exact local period querying
- multi-drive aggregation (`ObservabilityHub`)
- logical correlation handle facade
- live bound/unbound session state
- WPF dashboard and 15-minute bounded realtime ring
- privacy hardening and packaged SQLite proof

This baseline is intentionally dependency-light: Core currently depends only on `Microsoft.Data.Sqlite`.

## Findings by subsystem

### 1. MCP telemetry standardization — external code is stronger

ToolHive implements OpenTelemetry-native MCP instrumentation with:

- `mcp.server.operation.duration`
- request/tool counters and histograms
- active connection gauges
- W3C trace context extraction from HTTP and MCP `params._meta`
- tool/method-aware span names
- bounded metric-label values (128-byte clamp) to avoid metric-cardinality/memory abuse
- sampling/exporter configuration

OpenTelemetry .NET provides the native .NET primitives (`ActivitySource`, `Meter`, `Counter`, `Histogram`) and exporter pipeline. This is substantially stronger for interoperability than FileMCP's local-only custom counters.

Decision direction: ADD an optional standards layer; DO NOT replace FileMCP's local SQLite dashboard source of truth.

Important protocol caveat: the current OpenTelemetry MCP semantic-conventions documentation is still being aligned with MCP 2026-07-28. FileMCP must therefore use a versioned compatibility adapter and must not re-introduce protocol-level session assumptions.

### 2. Logical session lifecycle — FileMCP has a real unbounded-memory gap

FileMCP currently marks a session stale after 30 minutes but does not delete stale entries from `LogicalSessionRegistry`.

`LogicalChatCorrelationService` also keeps every known handle hash in a `ConcurrentDictionary` without TTL or maximum count.

Soth's session manager demonstrates stronger lifecycle controls:

- explicit TTL
- cleanup timer
- idle eviction
- maximum session count
- active/total counters

Microsoft MCP Gateway also uses local + distributed cache expiration and constrains/scopes session identifiers.

Decision direction: ADD bounded retention, max-cap and deterministic cleanup to FileMCP's logical correlation/session registry. Do not import transport-session semantics from those gateways because FileMCP's 2026 logical chat correlation is application-level, not MCP protocol session state.

### 3. SQLite retention — FileMCP has a real scheduling gap

`TelemetrySqliteStore.CleanupRetentionAsync` exists, but current source has no production caller/scheduler. Therefore 35-day minute and 90-day hour retention rules are not automatically enforced during normal 24/7 operation.

Decision direction: ADD a cancellable PeriodicTimer maintenance worker, run cleanup off the request path, and add durable-session retention as well.

### 4. 24/7 tunnel resilience — FileMCP has a real operational gap

`LocalMcpRuntime.TunnelDidExitAsync` currently stops the local server and changes state to Failed when `tunnel-client.exe` exits unexpectedly. It does not automatically restart with backoff.

Gateway repos show mature resilience patterns such as exponential backoff, retry budgets, circuit breaking and passive health detection. FileMCP does not need the whole proxy resilience stack, but its 24/7 desktop goal does need a small process-supervision subset.

Decision direction: ADD bounded exponential restart/backoff with jitter, stable-period reset, restart budget/cooldown and explicit user-stop cancellation. Do not add hedging, backend outlier ejection or generic circuit-breaker middleware because FileMCP has one local tunnel per workspace, not a backend pool.

### 5. Health/readiness — selective improvement worthwhile

Soth separates liveness, readiness and component health with per-check latency. This model is better than a single generic status string.

FileMCP should expose the same concepts internally in the dashboard:

- local MCP server ready
- tunnel process running
- tunnel health endpoint reachable when resolvable
- persistence healthy/degraded
- current restart/backoff state

Do not expose a new public unauthenticated health surface through the Secure MCP Tunnel by default.

### 6. Trace privacy — FileMCP is currently stronger by default

ContextForge has comprehensive trace/span/event persistence, sampling and redaction, but it can store URL/query/error/payload-related metadata under configuration. ToolHive also supports richer span attributes.

FileMCP's V1 rule of storing no MCP arguments/results/raw chat handles is safer for a local coding bridge.

Decision direction: KEEP privacy-by-default. Any OpenTelemetry export must use a strict metadata allowlist and never export tool arguments/results unless a future explicit opt-in feature is designed and audited separately.

### 7. Realtime chart engine — external libraries are stronger, but replacement is not justified yet

LiveCharts2 and ScottPlot are much stronger than FileMCP's WPF `Polyline` for zoom, tooltips, axes, large datasets and interactive historical analysis. ScottPlot additionally has explicit streaming primitives (`DataStreamer`).

However FileMCP V1 displays only ~900 one-second samples. The custom Polyline is sufficient, dependency-free and packaging-safe. Both chart engines bring additional rendering/native dependencies (notably SkiaSharp paths).

Decision direction: KEEP current Polyline for V1.1. Re-evaluate ScottPlot first if future requirements add interactive zoom, multi-hour/high-rate traces or thousands of points per second.

### 8. Prometheus endpoint — not selected for desktop core

`prometheus-net`, Soth and gateway repos demonstrate mature Prometheus metrics. FileMCP can gain interoperable metrics through OpenTelemetry without opening another listener by default.

Decision direction: no Prometheus HTTP endpoint in core V1.1. Optional exporter/plugin can be a later feature.

### 9. MCP Inspector — useful semantics, not code to transplant

Inspector explicitly models both legacy handshake-era MCP and the 2026 sessionless protocol era, and exposes connection diagnostics/history. This validates FileMCP's decision not to count TCP/session headers as distinct chats.

Decision direction: borrow diagnostics concepts only. Do not embed Inspector UI/client architecture into FileMCP.

## High-value integration shortlist

1. P0 — bounded logical session/correlation lifecycle
2. P0 — scheduled SQLite/session retention maintenance
3. P0 — automatic tunnel supervision with exponential restart/backoff
4. P1 — optional OpenTelemetry `ActivitySource` + `Meter` instrumentation
5. P1 — MCP `_meta` W3C trace-context extraction with 2026-aware compatibility layer
6. P1 — label/cardinality guards for exported telemetry
7. P1 — componentized liveness/readiness/degraded health model

## Explicitly rejected for V1.1

- replacing SQLite with external observability storage
- replacing atomic hot meter with Prometheus/OTel as the dashboard source of truth
- copying a gateway's protocol session model
- generic backend circuit breaker / hedging / outlier ejection
- public metrics listener by default
- persisting request/response/tool arguments
- replacing the lightweight WPF graph with LiveCharts2/ScottPlot now
- importing entire gateway/proxy codebases

## Licensing rule

Prefer implementation from principles/interfaces rather than copy-pasting source. If any source is copied or substantially adapted, preserve the originating license/notice obligations and document provenance in the task/commit. For the selected V1.1 work, native C# implementations using public APIs are preferred.

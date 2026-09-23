# V11-006 — W3C MCP TRACE-CONTEXT EVIDENCE

Date: 2026-09-22
Status: PASS

## Implemented

- Added `McpTraceContextAdapter` for W3C `traceparent` / `tracestate` extraction.
- MCP `params._meta.traceparent` takes precedence over HTTP `traceparent` when valid.
- Invalid/missing MCP trace context falls back to HTTP W3C context.
- Extracted parent context is marked remote and passed into FileMCP `ActivitySource` server spans.
- Added bounded parent-source tag (`mcp_meta` / `http`).
- `traceparent` is capped to 128 UTF-8 bytes; `tracestate` is capped to 512 UTF-8 bytes.
- Overlong tracestate is dropped while a valid traceparent remains usable.
- `baggage` is intentionally ignored and never imported into FileMCP Activity baggage.
- Duplicate HTTP `traceparent` / `tracestate` headers are rejected as single-value headers.

## Security / identity separation

- Trace context is observability metadata only.
- A valid W3C traceparent cannot act as `_filemcp_chat`.
- A valid traceparent does not create a bound logical session.
- Trace context cannot bypass filesystem containment or any FileMCP authority check.
- Private baggage markers are absent from Activity tags/baggage.

## OSS reuse / provenance

Reference source:
- `stacklok/toolhive` audit HEAD `3c2a235`, `pkg/telemetry/propagation.go` and integration tests: MCP `_meta` W3C propagation semantics and precedence model.

FileMCP intentionally diverges on baggage: ToolHive can propagate baggage under SEP-414, while FileMCP V1.1 ignores it by policy to preserve the local coding bridge privacy boundary.

No third-party source file was copied; the integration is native C# using `System.Diagnostics.ActivityContext.TryParse`.

## Verification

`dotnet build windows\FileMCP.Windows.sln -c Release -warnaserror`

PASS — 0 warnings, 0 errors.

`./tests/test_windows_runtime.ps1`

PASS — 381 assertions.

New tests prove:
- valid MCP `_meta` parent extraction
- MCP-over-HTTP precedence
- invalid MCP context HTTP fallback
- remote parent trace/span IDs and tracestate
- baggage rejection
- overlong tracestate handling
- runtime Activity parent/source semantics
- traceparent cannot become logical chat identity
- trace context cannot bypass path containment

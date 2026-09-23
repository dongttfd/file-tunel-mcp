# V11-005 — NATIVE .NET MCP TELEMETRY CONTRACT EVIDENCE

Date: 2026-09-22
Status: PASS

## Implemented

- Added process-wide `McpStandardTelemetry` using native .NET `ActivitySource` + `Meter` primitives.
- Added versioned compatibility adapter `filemcp.mcp.compat/2026-07-28/v1`.
- Added MCP/JSON-RPC/gen_ai semantic dimensions aligned with the audited ToolHive implementation where compatible:
  - `mcp.method.name`
  - `rpc.system.name=jsonrpc`
  - `jsonrpc.protocol.version=2.0`
  - `gen_ai.operation.name=execute_tool`
  - bounded `gen_ai.tool.name` / `mcp.tool.name`
- Added counters/histograms for accepted MCP requests, tool calls, errors, duration, request bytes and response bytes.
- LocalMcpServer emits standard telemetry only after local authentication + JSON-RPC parsing; unauthenticated and malformed traffic is not exported.
- Existing FileMCP local atomic counters + SQLite remain the dashboard/source-of-truth; standards telemetry is additive only.

## Privacy / cardinality

- Protocol version is allowlisted into supported version / legacy / other buckets.
- MCP methods are allowlisted; unknown values map to `other`.
- Tool names use FileMCP's known-tool classifier; unknown names map to `unknown`.
- Workspace dimension is restricted to C/D/E/F/other/unknown.
- Attribute values are UTF-8 byte bounded at 128 bytes.
- No tool arguments, file paths, request/response contents, raw chat handles or private marker values are exported.

## OSS reuse / provenance

Reference source audit:
- `stacklok/toolhive` at audit HEAD `3c2a235`: MCP metric/attribute semantics, label-cardinality defense, privacy lessons.
- `open-telemetry/opentelemetry-dotnet` at audit HEAD `44d841f`: native .NET ActivitySource/Meter instrumentation pattern.

No third-party source file was copied into FileMCP for V11-005. FileMCP uses .NET BCL instrumentation APIs and a native C# compatibility adapter. This avoids adding exporter/runtime dependencies before V11-007 while retaining interoperability hooks.

## Verification

`dotnet build windows\FileMCP.Windows.sln -c Release -warnaserror`

PASS — 0 warnings, 0 errors.

`./tests/test_windows_runtime.ps1`

PASS — 362 assertions.

New tests prove:
- versioned semantic profile
- UTF-8 attribute byte bounds
- method/tool/workspace/protocol cardinality guards
- server Activity creation/status/tags
- request/tool/error counters
- operation/request/response histograms
- unauthenticated/malformed traffic exclusion
- unknown-tool fixed bucket behavior
- private marker/arguments never appear in metric attributes

Package references remain unchanged at this task: Core still has only `Microsoft.Data.Sqlite` as a direct NuGet dependency. OTLP SDK/exporter is intentionally deferred to V11-007.

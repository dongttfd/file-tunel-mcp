# V11-007 — OPTIONAL OTLP EXPORTER EVIDENCE

Date: 2026-09-22
Status: PASS

## Implemented

- Added official OpenTelemetry .NET packages:
  - `OpenTelemetry` 1.19.1
  - `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.19.1
- Added optional HTTP/Protobuf OTLP exporter for FileMCP MCP spans and metrics.
- Export is disabled by default and performs zero collector requests while disabled.
- OTLP endpoint must be an absolute base `http://` or `https://` URI with no credentials, query, fragment, or custom path.
- Trace and metric exporters use `/v1/traces` and `/v1/metrics` respectively.
- Export timeout is bounded to 1 second; collector failure is isolated from MCP execution.
- Settings/UI now persist and validate optional OTLP enablement and endpoint.
- Dashboard telemetry status reports OTLP off/on/error separately from SQLite persistence health.
- Packaged app smoke configures the OTLP provider from the single-file executable and restores normal settings afterwards.
- OpenTelemetry Apache-2.0 license and upstream third-party notices are preserved in `vendor/opentelemetry/` and shipped in the release ZIP.

## Verification

`dotnet build windows\FileMCP.Windows.sln -c Release -warnaserror`

PASS — 0 warnings, 0 errors.

`./tests/test_windows_runtime.ps1`

PASS — 397 assertions.

New tests prove:

- disabled-by-default settings
- disabled mode performs zero network requests
- endpoint validation rejects unsafe/non-base collector URIs
- local collector receives non-empty protobuf on `/v1/traces` and `/v1/metrics`
- exporter configuration failure degrades without crashing startup
- unreachable collector does not fail a valid MCP request
- forced export to an unavailable collector remains timeout-bounded
- OTLP settings survive settings-store roundtrip

`./build_windows_app.ps1 -Architecture x64`

PASS — self-contained single-file x64 package built.

`./tests/test_windows_app.ps1`

PASS:

- windows-app-startup
- windows-close-to-tray
- windows-packaged-tunnel-client
- windows-packaged-sqlite-write-read
- windows-packaged-otlp-provider
- windows-packaged-opentelemetry-notices

Package SHA-256 at this gate:

`CC5F021486368471AB20685375495765F79270E4F231651FBF90AECD2074D08D`

Dependency audit:

- no vulnerable NuGet packages reported
- no direct package updates reported

## Privacy invariant

The exporter observes only the bounded standard-telemetry schema from V11-005/V11-006. Tool arguments, file contents, raw logical-chat handles and W3C baggage are not exported.

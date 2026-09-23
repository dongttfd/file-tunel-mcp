# OBS-012 Hardening Evidence

Date: 2026-09-22
Task: OBS-012 - security/privacy/load/recovery hardening audit

## Gates executed

- Release build with warnings as errors: PASS, 0 warnings / 0 errors.
- Windows integration suite: PASS, 293 assertions.
- Extended malformed HTTP fuzz: PASS, 2,000 iterations.
- Existing filesystem containment regressions: PASS.
- Junction/reparse escape regressions: PASS.
- Git safe-mode regressions: PASS.
- Local-auth-before-body / host / origin / protocol validation regressions: PASS.
- Runtime secret-redaction and tunnel environment allowlist regressions: PASS.
- App startup / close-to-tray / packaged tunnel-client harness: PASS.

## New adversarial observability proofs

1. Privacy payload proof
   - Sent a private file-content marker through `write_file`.
   - Read the private marker back through `read_file` so it existed in a real MCP response body.
   - Sent a private search term through `search_content` and received it in a real response.
   - Flushed telemetry/session persistence.
   - Scanned SQLite database/WAL sidecars.
   - PASS: payload content, file path argument, search query, and raw chat correlation handle were absent.

2. Concurrent server load
   - 64 simultaneous authenticated `read_file` MCP calls through LocalMcpServer.
   - PASS: all responses succeeded.
   - PASS: global ToolCalls delta exactly 64.
   - PASS: ReadCalls delta exactly 64.
   - PASS: bound logical-session attribution retained all read calls.

3. Corrupt database isolation/recovery
   - Started observability against an intentionally invalid SQLite file.
   - PASS: persistent telemetry initialization detected the corruption.
   - PASS: a valid MCP `read_file` still succeeded while persistence was unavailable.
   - PASS: in-memory counters continued to work.
   - Removed the corrupt telemetry file and retried initialization.
   - PASS: SQLite database recreated and current schema version verified.

## Result

OBS-012 PASS. No hardening blocker found after adversarial privacy, concurrency, corruption/recovery, fuzz, app, and legacy security gates.

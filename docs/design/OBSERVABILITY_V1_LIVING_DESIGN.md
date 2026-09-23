# OBSERVABILITY V1 — LIVING DESIGN HISTORY

Status: DESIGN HISTORY; not implementation authority after freeze.

## Draft 0 — requested dashboard
Four headline metrics: input token, output token, task count, total token.

## Draft 1 — truthful usage surface
Added exact MCP request/response bytes, call counts, read/write/Git/command/error categories. Token values changed to explicitly estimated values.

## Draft 2 — historical periods
Added hot realtime data plus minute/hour/day rollups for Today, Yesterday, 7d, 30d and later Custom.

## Draft 3 — multi-drive
Telemetry is tagged with workspace key C/D/E/F. One desktop dashboard aggregates all enabled runtimes but never merges their security boundaries.

## Draft 4 — live work
Rejected TCP-connection count as chat count. Added explicit distinction between runtime uptime, observed activity, transport/client identity and logical chat identity.

## Draft 5 — ChatCode deep dive
Installed ChatCode 2.1.5 evidence showed:
- usage_statistics / live_sessions subsystems;
- SQLite minute/hour/day-style telemetry;
- calls, request_bytes, response_bytes, tokens_in_est, tokens_out_est, read/write/task/git/remote/error counters;
- bytes_div_4_estimate token semantics;
- logical chat registry with chat identity + transport identity + last-seen state;
- UI active policy equivalent to status active OR idle <= 45 seconds.

## Draft 6 — MCP 2026 protocol correction
MCP 2026-07-28 removes initialize/initialized and protocol-level Mcp-Session-Id. Therefore a modern FileMCP cannot derive a stable logical ChatGPT chat from protocol session state. Logical-chat identity must be an application-level feature.

## Draft 7 — final direction
Telemetry truth first, persistence second, server instrumentation third, identity fourth, UI last. No UI-owned counters.

## Draft 8 — OBS-004 boundary correction after freeze
Independent implementation review found that 48h minute retention cannot produce exact 7d/30d local-day totals for non-whole-hour offsets. ADR 0002 amends retention to 35 days and uses minute rows for exact headline totals; hour rows remain for chart efficiency.
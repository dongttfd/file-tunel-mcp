# ADR 0001 — Observability architecture and logical chat correlation

Status: Accepted
Date: 2026-09-22

## Context
FileMCP must add ChatCode-grade local observability without lying about ChatGPT token usage or breaking the existing loopback/security model. Modern MCP 2026-07-28 is stateless and no longer provides Mcp-Session-Id, so distinct conversation identity cannot be inferred from transport state.

## Decision
1. Core owns telemetry; WPF consumes immutable snapshots.
2. Request path uses atomic counters only.
3. A background writer persists delta rollups to SQLite WAL.
4. Token metrics are versioned estimates from MCP payload bytes.
5. Distinct chats use explicit non-authoritative application-level correlation handles; unbound traffic is never promoted to exact chat count.
6. Multi-drive metrics are tagged by workspace before aggregation.
7. No MCP content/arguments are persisted.

## Consequences
- Adds Microsoft.Data.Sqlite/native packaging responsibility.
- Exact chat count requires one connect/correlation flow and live ChatGPT proof.
- Dashboard remains truthful even when logical identity is unavailable.
- Telemetry failures do not couple to MCP execution.

## Rejected alternatives
UI-only counters, TCP-as-chat, tunnel-id-as-chat, Mcp-Session-Id on 2026 protocol, synchronous per-call DB writes, payload logging, unlabeled token estimates.

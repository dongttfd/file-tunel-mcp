# V11-001 — BOUNDED LOGICAL SESSION / CORRELATION EVIDENCE

Date: 2026-09-22
Status: PASS

## Implemented

- Logical chat correlation handles now have bounded retention and maximum-cap enforcement.
- Correlation last-seen is refreshed on successful resume/resolve.
- Expired and pressure evictions are counted.
- Logical session registry has a configurable maximum live/stale tracked-session cap and retention window.
- Capacity pressure evicts only already-persisted stale sessions; active/in-flight sessions are never evicted.
- If the cap is saturated by non-evictable sessions, new attribution degrades to unbound rather than failing the MCP tool call.
- Session eviction requires the final stale state to have been successfully persisted.
- LogicalSessionWriter acknowledges successful persistence back to the registry.
- Stale unbound activity is also retention-cleaned after persistence.

## Verification

Command:

```text
dotnet build windows\FileMCP.Windows.sln -c Release -warnaserror
```

Result: PASS, 0 warnings, 0 errors.

Command:

```text
./tests/test_windows_runtime.ps1
```

Result: PASS, 305 assertions.

New tests cover:

- correlation TTL expiry
- correlation cap / LRU-style pressure eviction by last-seen
- correlation pressure counters
- no session eviction before stale persistence is acknowledged
- persisted stale-session TTL eviction
- capacity-pressure eviction of persisted stale session
- concurrent in-flight session protection under pressure

## Safety invariant

The new retention path never persists raw chat handles and never turns observability pressure into an MCP execution failure.

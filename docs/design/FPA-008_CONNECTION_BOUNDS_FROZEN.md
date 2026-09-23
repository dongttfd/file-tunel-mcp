# FPA-008 - Bounded Local MCP Connections / Idle-Header Timeouts - Frozen Design

Date: 2026-09-22
Status: FROZEN BEFORE IMPLEMENTATION

## 1. Problem

The whole-repository audit found the local MCP TCP servers can accept/retain an unbounded number of slow or incomplete connections.

Windows:
- AcceptLoopAsync accepts continuously.
- every accepted client creates a handler task.
- request bytes are size-bounded, but ReadAsync has only server-lifetime cancellation.

macOS:
- NWListener accepts every connection.
- recursive receiveRequest has byte-size bounds but no connection cap or read/header deadline.

This is a resource-exhaustion hardening gap, not an authentication bypass.

## 2. Scope

Add equivalent bounds on both supported platforms without changing MCP semantics, auth policy, filesystem authority, Git policy, command policy or tunnel behavior.

Required defaults:

- max concurrent accepted MCP connections: 64;
- per-read idle timeout: 15 seconds;
- absolute request-header completion timeout: 30 seconds.

Defaults are intentionally conservative for loopback traffic. Tests may inject shorter limits.

## 3. Semantics

### Connection cap

At most 64 active local MCP connection handlers per server.

When saturated:

- accept may still complete at the OS listener;
- FileMCP immediately closes/cancels the excess connection;
- no handler task/work queue item is created for the rejected connection;
- no auth/tool processing occurs.

A released slot must be reusable immediately.

### Read idle timeout

Every pending network read is bounded.

If no request bytes arrive within the idle timeout:

- close/cancel the connection;
- do not execute an MCP request;
- do not emit a fabricated tool result;
- do not weaken local auth.

### Absolute header timeout

Before CRLFCRLF is observed, the connection also has an absolute header-completion deadline.

This prevents a trickle client from keeping a slot forever by sending one byte just before each idle timeout.

Once headers are complete, the absolute header deadline no longer applies; body acquisition remains protected by the per-read idle timeout and existing body-size limit.

## 4. Windows design

Add internal testable LocalMcpServerLimits:

- MaxConcurrentConnections
- ReadIdleTimeout
- HeaderReadTimeout
- Default

Public constructor behavior remains source-compatible and uses Default.
An internal constructor accepts explicit limits for tests.

Use a SemaphoreSlim slot gate:

- AcceptTcpClientAsync;
- nonblocking Wait(0);
- reject/dispose if saturated;
- otherwise run HandleClientAsync;
- release slot in finally.

Read loop:

- remember connection/header start using Stopwatch;
- determine whether CRLFCRLF has been seen;
- before each ReadAsync create linked cancellation;
- CancelAfter(min(idle timeout, remaining header deadline));
- timeout closes connection silently;
- server cancellation remains distinguishable and exits cleanly.

Do not dispose the semaphore while in-flight handlers may still release it.

## 5. macOS design

Add testable LocalMCPServerLimits with the same defaults.

Use a DispatchSemaphore slot gate.

Each accepted connection owns a small ConnectionLease:

- release exactly once;
- cancel NWConnection on finish;
- signal slot exactly once;
- ignore callbacks after finish.

receiveRequest carries:

- accumulated bytes;
- original header deadline;
- connection lease.

Each receive schedules a cancelable timeout work item for:

- min(idle deadline, remaining header deadline) while headers incomplete;
- idle deadline after headers complete.

The receive callback cancels its timeout work item before parsing.

send completion finishes the lease.

## 6. Security / privacy

No changes to:

- loopback bind;
- Host/Origin checks;
- local auth token;
- request size limits;
- JSON-RPC validation;
- tool authorization;
- telemetry payload privacy;
- correlation semantics.

Rejected connections and read timeouts do not become MCP/tool telemetry because no authenticated request completed parsing.

## 7. Tests

### Windows native tests

With injected small limits:

1. max=2: two partial clients occupy slots;
2. third connection is closed/rejected quickly;
3. after one slot is released, a valid request succeeds;
4. idle client is closed within short test timeout;
5. trickle bytes shorter than idle interval still hit absolute header deadline;
6. normal authenticated request remains unchanged;
7. Stop cancels blocked reads.

### macOS native tests

Equivalent socket-level coverage against LocalMCPServer with injected limits:

- saturation/rejection;
- slot reuse;
- idle timeout;
- trickle/header timeout;
- normal MCP request still succeeds.

### Contract test

Add a source-level cross-platform transport-bounds contract ensuring both implementations expose:

- 64 default cap;
- 15s idle default;
- 30s header default;
- connection slot gate;
- timeout path.

## 8. Acceptance

FPA-008 PASS only when:

- Windows Release build warnings-as-errors PASS;
- Windows core/runtime tests PASS;
- macOS warnings-as-errors typecheck PASS;
- full Swift integration PASS;
- x64/ARM64 packaging gates remain PASS;
- native GitHub verify-macos, verify-windows and verify-windows-arm64 SUCCESS;
- no existing HTTP parser/auth test changes behavior;
- no new unbounded per-connection state remains.

## 9. Non-goals

- rate limiting authenticated MCP tool calls;
- per-user quotas;
- remote IP policy (listener remains loopback only);
- changing OS listen backlog;
- changing MCP payload limits;
- FPA-009 dynamic health discovery.

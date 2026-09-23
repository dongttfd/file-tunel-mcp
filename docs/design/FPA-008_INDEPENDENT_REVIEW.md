# FPA-008 - Independent Multi-Round Review

Date: 2026-09-22

## Round 1 - Threat boundary

Same-user local processes are within the documented trust boundary, lowering severity, but tunnel-side or buggy local clients can still consume resources. Defense-in-depth is justified.

## Round 2 - Cap placement

Rejecting before Accept would require OS backlog tuning and is platform-specific.

Decision: accept, nonblocking slot acquisition, immediately close excess connections.

## Round 3 - Slowloris behavior

Idle timeout alone is insufficient because a client can trickle bytes forever.

Decision: combine per-read idle timeout with absolute header-completion deadline.

## Round 4 - Large body compatibility

A single absolute whole-request timeout could break valid large requests.

Decision: absolute deadline applies only until headers complete; body remains protected by per-read idle timeout and existing 8 MB cap.

## Round 5 - Auth behavior

Timeout/rejection must not create synthetic authenticated request records.

Decision: close before Process/ProcessAsync if parsing never completes.

## Round 6 - Slot leaks

Every error/timeout/send completion must release exactly one slot.

Windows: finally around handler.
macOS: idempotent ConnectionLease.

## Round 7 - Stop behavior

Server shutdown must win over per-read timeout and must not surface as a request error.

Windows linked server cancellation remains authoritative.
macOS lease cancellation is idempotent.

## Round 8 - Cross-platform truth

Fixing only Windows would leave the same whole-app audit finding on macOS.

Decision: implement equivalent bounds on both platforms in FPA-008.

Final review: proceed with cross-platform bounds; no protocol or authority change required.

# FPA-003 - Independent Multi-Round Review

Date: 2026-09-22

## Round 1 - Failure-domain review

Restarting the whole runtime would unnecessarily drop the local MCP server and profile lock.

Decision: restart tunnel-client only.

## Round 2 - Retry safety

Retrying a transport child is safe only if no user tool request is replayed.

Decision: supervisor owns process launch only. Tool execution remains untouched.

## Round 3 - Restart storm

Unbounded immediate restart is unacceptable for 24x7 operation.

Decision: exponential backoff + jitter + bounded attempts/window + cooldown.

## Round 4 - Stable recovery

A long healthy run should forgive old restart history.

Decision: stable-run reset mirrors Windows at 2 minutes.

## Round 5 - Stop race

A user Stop during backoff must win deterministically.

Decision: requestedStop set before queue work; pending restart work canceled; restart closure rechecks stop state.

## Round 6 - Stale callback

Managed process exit callbacks can arrive after cleanup/new child creation.

Decision: generation token captured per child; old generation ignored.

## Round 7 - Security context

Re-running init/doctor on every child crash expands failure surface and may touch configuration unnecessarily.

Decision: capture only the validated run launch context after initial init/doctor and restart child with it.

## Round 8 - UX

Users need a way to cancel automatic recovery.

Decision: Restarting/Cooldown keep Disconnect enabled.

## Final review

Proceed. This is a reliability parity change with no need to alter filesystem, Git, auth, or MCP protocol authority.

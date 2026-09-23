# FPA-003 - Frozen Task Graph

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-003-A | freeze | Add TunnelSupervisor policy/options/decision | deterministic policy tests |
| FPA-003-B | A | Extend macOS runtime states + UI handling | exhaustive compile/state behavior |
| FPA-003-C | A,B | Capture launch context + generation + child-only restart loop | first crash auto-recovers |
| FPA-003-D | C | Stop/shutdown cancellation + stale-callback guards | no restart after stop |
| FPA-003-E | A-D | Wire compile lists + fake tunnel integration tests | runtime suite PASS |
| FPA-003-F | E | Windows regression + native GitHub macOS/Windows CI | both jobs SUCCESS |
| FPA-003-G | F | evidence/state/queue closure | FPA-003 PASS |

Dependency:

```text
A -> B -> C -> D -> E -> F -> G
```

# FPA-008 - Frozen Task Graph

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-008-A | freeze | Windows limits + slot gate + bounded read | Windows focused tests |
| FPA-008-B | A | Windows saturation/idle/trickle/slot-reuse tests | core suite PASS |
| FPA-008-C | freeze | macOS limits + ConnectionLease + bounded receive | Swift compile |
| FPA-008-D | C | macOS saturation/idle/trickle/slot-reuse tests | Swift runtime PASS |
| FPA-008-E | A-D | cross-platform bounds contract + CI wiring | contract PASS |
| FPA-008-F | E | full local/native CI regression | all jobs SUCCESS |
| FPA-008-G | F | evidence/state/queue closure | FPA-008 PASS |

Dependency:

```text
freeze -> A -> B
       -> C -> D
B + D -> E -> F -> G
```

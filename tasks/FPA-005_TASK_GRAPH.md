# FPA-005 - Frozen Task Graph

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-005-A | freeze | parameterize Windows app smoke by architecture | x64 local smoke compatibility |
| FPA-005-B | A | add native Windows ARM64 GitHub job | workflow contract identifies native runner |
| FPA-005-C | B | add CI/static contract for ARM64 job | drift prevented |
| FPA-005-D | A-C | push candidate and run native CI | x64 + ARM64 jobs SUCCESS |
| FPA-005-E | D | evidence/state/queue closure | FPA-005 PASS |

Dependency: A -> B -> C -> D -> E

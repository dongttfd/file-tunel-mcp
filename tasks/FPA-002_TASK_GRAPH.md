# FPA-002 - Frozen Task Graph

Status: READY AFTER DESIGN FREEZE

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-002-A | design freeze | Review/repair LogicalChatCorrelation.swift | format/create/resume/TTL/cap/hash tests |
| FPA-002-B | A | Wire correlation facade into LocalMCPServer | 19-tool parity and metadata stripping |
| FPA-002-C | A,B | Wire source into all macOS build/dev/CI/test compile lists | no missing-symbol build path |
| FPA-002-D | A,B,C | Add Swift unit/integration + cross-platform parity tests | all correlation/schema cases PASS |
| FPA-002-E | D | macOS typecheck/runtime/build gate + Windows regression | evidence clean |
| FPA-002-F | E | update README/audit queue/evidence/state | FPA-002 PASS, FPA-003 READY |

Dependency:

```text
A -> B -> C -> D -> E -> F
```

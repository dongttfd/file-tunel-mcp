# FPA-007 - Frozen Task Graph

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-007-A | freeze | Add DesktopSingleInstanceCoordinator in Core | first/second/dispose/activation tests |
| FPA-007-B | A | Wire App startup/exit + MainWindow activation method | secondary exits before new window |
| FPA-007-C | A,B | Add packaged duplicate-launch smoke | tray first instance is restored |
| FPA-007-D | A-C | Windows build/runtime/package regression | local gates PASS |
| FPA-007-E | D | Native GitHub macOS/x64/ARM64 CI | all jobs SUCCESS |
| FPA-007-F | E | evidence/state/queue closure | FPA-007 PASS |

Dependency: A -> B -> C -> D -> E -> F

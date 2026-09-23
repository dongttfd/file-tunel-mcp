# FPA-009 - Frozen Task Graph

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-009-A | freeze | URL-file path + strict parser | parser tests |
| FPA-009-B | A | pass --health.url-file and lifecycle cleanup | launch/restart tests |
| FPA-009-C | A,B | dynamic RefreshHealthAsync discovery/probe | Reachable/Unreachable proof |
| FPA-009-D | C | full Windows + native CI regression | all platform jobs SUCCESS |
| FPA-009-E | D | evidence/state closure | FPA-009 PASS |

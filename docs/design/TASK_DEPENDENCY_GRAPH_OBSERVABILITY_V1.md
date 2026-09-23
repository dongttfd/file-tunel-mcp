# OBSERVABILITY V1 — TASK DEPENDENCY GRAPH

Architecture is frozen. Implementation may start only in this order/dependency graph.

FMR-001 Drive-root containment correctness
  |
  +--> OBS-001 Metric contracts + estimator + classifier
         |
         +--> OBS-002 Atomic hot meter + immutable snapshots
                |
                +--> OBS-003 SQLite WAL schema + writer + migrations
                       |
                       +--> OBS-004 Historical queries + retention + timezone ranges
                       |
                       +--> OBS-005 LocalMcpServer instrumentation
                              |
                              +--> OBS-006 Multi-drive ObservabilityHub + runtime uptime
                              |      |
                              |      +--> OBS-009 WPF Overview core cards
                              |
                              +--> OBS-007 Logical chat connect/correlation facade
                                     |
                                     +--> OBS-008 Live session registry/state machine
                                            |
                                            +--> OBS-010 Period/session/detail UI
                                                   |
                                                   +--> OBS-011 Realtime graph + health
                                                          |
                                                          +--> OBS-012 Security/privacy/load/recovery audit
                                                                 |
                                                                 +--> OBS-013 Packaged x64 + live ChatGPT acceptance

Critical path:
FMR-001 -> OBS-001 -> OBS-002 -> OBS-003 -> OBS-005 -> OBS-006 -> OBS-009 -> OBS-010 -> OBS-011 -> OBS-012 -> OBS-013

Identity path:
OBS-005 -> OBS-007 -> OBS-008 -> OBS-010 -> OBS-013

Query path:
OBS-003 -> OBS-004 -> OBS-010

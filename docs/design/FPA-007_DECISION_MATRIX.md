# FPA-007 - Decision Matrix

| Option | Blocks duplicate GUI | Restores tray instance | Crash cleanup | Testability | Complexity | Decision |
|---|---:|---:|---:|---:|---:|---|
| Keep profile lock only | No | No | N/A | High | Low | Reject |
| App mutex only | Yes | No | Good | Medium | Low | Reject |
| Mutex + named event | Yes | Yes | Good | Medium | Medium | Reject |
| Named pipe first-instance + activate | Yes | Yes | Good | High | Medium | **SELECT** |
| Full local RPC/HTTP control server | Yes | Yes | Varies | Medium | High | Reject |

Selected: named pipe first-instance + one bounded `activate` command.

# FPA-003 - Solution Decision Matrix

| Option | Recovery | Bounded | Preserves MCP server | Complexity | Decision |
|---|---|---|---|---|---|
| Fail and require manual reconnect | No | Yes | No | Low | Reject |
| Immediate infinite child restart | Yes | No | Yes | Low | Reject |
| Restart whole runtime with backoff | Yes | Yes | No | Medium | Reject |
| Port bounded child-process supervisor | Yes | Yes | Yes | Medium | **SELECT** |
| Import a full external gateway/supervisor | Yes | Varies | Varies | High | Reject |

Selected: bounded child-process supervisor matching the proven Windows policy.

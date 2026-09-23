# FPA-008 - Solution Decision Matrix

| Option | Bounds slow clients | Bounds concurrency | Cross-platform | Compatibility | Decision |
|---|---|---|---|---|---|
| Keep current behavior | No | No | Yes | High | Reject |
| Idle timeout only | Partial | No | Yes | High | Reject |
| Connection cap only | No | Yes | Yes | High | Reject |
| Cap + per-read idle only | Partial (trickle survives) | Yes | Yes | High | Reject |
| Cap + idle + absolute header deadline | Yes | Yes | Yes | High | **SELECT** |
| Full HTTP server framework migration | Yes | Yes | Possible | Risk/high scope | Reject |

Selected defaults: 64 concurrent, 15s idle, 30s header.

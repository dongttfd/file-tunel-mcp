# AGENTS.md

## Repository-specific rules

- Repository: FileMCP.
- Follow docs/process/IDEA_CAPTURE_AND_DESIGN_LAW.md for every new feature or architectural change.
- Keep MCP listeners loopback-only.
- Never weaken shared-root containment, reparse/junction defenses, Git safe mode, secret redaction, or tunnel environment allowlisting.
- Runtime API keys/local-auth tokens stay in Credential Manager or transient process environment, never telemetry/state files.
- Observability stores metadata/counters only. Never persist request/response bodies, tool arguments, commands, file contents, Git messages, prompt/chat text, cookies, or bearer credentials.
- Token metrics must be labeled as MCP payload estimates unless an upstream source provides authoritative usage.
- Multi-drive workspaces remain independent security/runtime boundaries; aggregation is read-only.
- TCP connections must never be presented as distinct chats.
- Build/test evidence is mandatory before PASS.
- Lifecycle after architecture freeze: CLAIM -> ANALYZE -> PLAN -> CODE -> TEST -> EVIDENCE -> VERIFY -> COMMIT -> REVIEW -> MERGE -> DONE.

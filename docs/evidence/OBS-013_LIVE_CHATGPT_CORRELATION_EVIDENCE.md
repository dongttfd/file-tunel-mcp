# OBS-013 LIVE CHATGPT CORRELATION EVIDENCE

Date: 2026-09-22
Status: LIVE PATH PROVEN; ONE CROSS-TURN REUSE CHECK REMAINS

## Proven in a real refreshed ChatGPT conversation

- ChatGPT connector registry exposed 19 FileMCP tools, including `filemcp_observability_connect`.
- `filemcp_observability_connect` created a new opaque logical-chat handle.
- Normal FileMCP `read_file`, `git_status`, and `run_command` calls accepted the same `_filemcp_chat` metadata.
- Re-calling `filemcp_observability_connect` with the same handle returned `resumed=true`.
- SQLite durable session identity matched SHA-256 hash `b6964644e7324df30e5c56c159de47b8f6351da97ce448882811758d8c0eea6f`.
- Durable workspace row was bound to workspace D with real tool-call counters.
- Raw correlation handle was absent from the SQLite database, WAL, and SHM files at verification time.

## Remaining frozen-spec check

The architecture freeze requires stable propagation across multiple ChatGPT turns before exact `AI chats` wording is enabled. The current assistant turn proved create/resume/propagation inside one real ChatGPT turn. The next user turn must reuse the same logical handle on at least one normal FileMCP call and verify it maps to the same durable session hash.

Do not mark OBS-013 PASS or enable exact `AI chats` wording until that cross-turn check succeeds.

## Cross-turn completion

A subsequent user turn resumed the exact same logical-chat handle with `resumed=true`. A normal FileMCP `read_file` call using the same `_filemcp_chat` value succeeded, and a follow-up SQLite check still resolved to SHA-256 session hash:

`b6964644e7324df30e5c56c159de47b8f6351da97ce448882811758d8c0eea6f`

The durable workspace row remained bound to workspace D and its real tool-call counters increased across turns. The raw handle remained absent from the SQLite database, WAL and SHM files.

Result: PASS. The frozen cross-turn propagation gate is satisfied, so exact correlated `AI chats` wording is now enabled while unbound traffic remains explicitly separate and is never counted as a chat.

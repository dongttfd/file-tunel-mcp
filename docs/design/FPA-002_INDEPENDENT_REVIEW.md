# FPA-002 - Independent Multi-Round Review

Date: 2026-09-22
Target: macOS logical-chat / MCP tool-surface parity

## Round 1 - Capability parity

Finding: audited baseline macOS tool surface had 18 tools vs Windows 19.

Decision: real parity requires advertising the connect tool, not merely documenting a Windows-only exception.

## Round 2 - Authority review

Risk: a correlation handle could accidentally become an auth/session credential.

Constraint: handle is metadata only. All file/Git/command authority remains controlled by existing workspace and enableCommands policies.

PASS criterion: correlation path cannot alter LocalTools/SafePath/Git policies.

## Round 3 - Privacy review

Risk: raw handle could be logged/persisted.

Decision: registry keys are SHA-256 hashes; raw handle is returned only in connect response and accepted transiently as tool metadata.

No file/settings/database persistence in FPA-002.

## Round 4 - Cardinality / 24x7 review

Risk: ChatGPT contexts can create unbounded handles.

Decision: match Windows defaults (4096 entries, 2h retention) and pressure-evict oldest last-seen entries.

## Round 5 - Schema compatibility

Risk: adding `_filemcp_chat` causes strict argument validators to reject valid calls.

Decision: augment advertised normal schemas, then strip reserved metadata before existing strict tool/skill validation.

Unknown correlation metadata degrades to unbound semantics.

## Round 6 - Build-system review

Confirmed gap: a new Swift file is useless unless every explicit `swiftc` invocation includes it.

Must update build, dev, CI typecheck, runtime-test and server-test source lists.

## Round 7 - Concurrent-draft review

An uncommitted draft appeared during design.

Positive:
- Secure random via Security;
- SHA-256 via CryptoKit;
- bounds/TTL;
- tool definition and metadata stripping concept.

Problems found before acceptance:
- source file is not wired into existing Swift compile lists;
- `LocalMCPServer.swift` contains a malformed multiline log string in the uncommitted diff;
- tests are not yet updated;
- no automated cross-platform parity guard yet.

Decision: retain useful draft, repair it only after frozen design, then subject it to full tests.

## Round 8 - Scope control

Rejected: porting Windows SQLite/session dashboard/OTLP stack inside FPA-002.

Reason: it would silently expand architecture and couple FPA-002 to unrelated persistence/UI work.

## Final independent review

Proceed with the frozen minimal parity architecture. No security objection if the facade stays authority-neutral and in-memory bounded.

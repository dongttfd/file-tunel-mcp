# FPA-002 - macOS Logical-Chat / MCP Tool-Surface Parity Evidence

Date: 2026-09-22
Status: PASS

## Architecture

Frozen design:
- `docs/design/FPA-002_MAC_LOGICAL_CHAT_PARITY_FROZEN.md`
- `docs/design/FPA-002_INDEPENDENT_REVIEW.md`
- `docs/design/FPA-002_DECISION_MATRIX.md`
- `tasks/FPA-002_TASK_GRAPH.md`

## Implemented

- Added bounded process-local macOS `LogicalChatCorrelationService`.
- CSPRNG: Security `SecRandomCopyBytes`.
- Hash: CryptoKit SHA-256.
- Windows parity defaults: 4096 handles, 2h retention, 32 random bytes.
- Added `filemcp_observability_connect` to macOS tool surface.
- Added optional `_filemcp_chat` facade metadata to normal tool schemas.
- Reserved metadata is stripped before LocalTools/CodexSkillRegistry strict validation.
- Unknown/malformed normal-call correlation metadata does not grant authority and degrades to unbound behavior.
- Modern discover instructions document the facade.
- Added deterministic create/resume/invalid/unknown/TTL/cap tests.
- Added legacy + modern MCP integration coverage.
- Added Windows/macOS source tool-surface parity regression gate.
- Added new Swift source to build, dev, CI typecheck and integration compile lists.

## Local verification

```text
Git Bash syntax check: PASS
tests/test_tool_surface_parity.ps1: PASS (required=19)
tests/test_project_state_contract.ps1: PASS
Windows Release build -warnaserror: PASS, 0 warnings / 0 errors
Windows runtime suite: PASS, 414 assertions
git diff --check: PASS
```

## Native GitHub verification

Fork workflow run: `35718764133`
Commit: `a174efd`

```text
verify-macos:   SUCCESS
verify-windows: SUCCESS
```

Native macOS job therefore proved:
- Swift warnings-as-errors typecheck;
- full `tests/test_swift_runtime.sh`;
- `build_macos_app.sh`;
- bundled legal-resource verification.

## Result

FPA-002 PASS. The public logical-chat/MCP facade is again cross-platform, while FPA-003 remains the separate tunnel resilience parity task.

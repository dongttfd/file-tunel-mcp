# FPA-002 - macOS Logical-Chat Parity Candidate Evidence

Date: 2026-09-22
Status: CANDIDATE - REAL macOS CI REQUIRED

## Frozen design

- docs/design/FPA-002_MAC_LOGICAL_CHAT_PARITY_FROZEN.md
- docs/design/FPA-002_INDEPENDENT_REVIEW.md
- docs/design/FPA-002_DECISION_MATRIX.md
- tasks/FPA-002_TASK_GRAPH.md

## Candidate implementation

- Added bounded process-local `LogicalChatCorrelationService` on macOS.
- Uses Security `SecRandomCopyBytes` and CryptoKit SHA-256.
- Matches Windows defaults: 4096 known handles, 2h retention, 32 random bytes.
- Added `filemcp_observability_connect` to macOS tools/list.
- Added optional `_filemcp_chat` to normal tool schemas.
- Reserved metadata is removed before LocalTools/CodexSkillRegistry strict validation.
- Unknown/invalid normal-call correlation metadata degrades to unbound/no-correlation behavior.
- Updated modern discover instructions.
- Added compile wiring to build/dev/CI/test paths.
- Added deterministic correlation tests and MCP integration tests.
- Added Windows/macOS public tool-surface parity regression test.

## Local gates available on Windows

```text
Git Bash shell syntax:
PASS

tests/test_tool_surface_parity.ps1:
PASS - required=19

dotnet build windows/FileMCP.Windows.sln -c Release -warnaserror:
PASS - 0 warnings / 0 errors

tests/test_windows_runtime.ps1:
PASS - 414 assertions

git diff --check:
PASS
```

## Remaining acceptance

The current machine has no Swift compiler/macOS frameworks. FPA-002 cannot be marked PASS until a real macOS runner completes:

- warnings-as-errors Swift typecheck;
- tests/test_swift_runtime.sh;
- build_macos_app.sh;
- legal resource verification.

This candidate is pushed only to obtain that required native evidence.

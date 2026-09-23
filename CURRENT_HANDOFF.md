# CURRENT HANDOFF

## Current status

Project: FileMCP
Branch: `main`
Git SHA source of truth: run git rev-parse HEAD.
Expected worktree at handoff: CLEAN.

## Completed technical program

PASS:
- FMR-001.
- OBS-001 through OBS-013.
- V11-001 through V11-008.
- FPA-001, FPA-002, FPA-003, FPA-005, FPA-006, FPA-007, FPA-008 and FPA-009.

Latest normal verification:
- Windows Release build: 0 warnings / 0 errors.
- Windows runtime suite: 444 assertions PASS.
- macOS native Verify: PASS.
- Windows x64 native Verify: PASS.
- Windows ARM64 native Verify: PASS.
- Production signing/notarization plumbing contract: PASS.
- Production Release workflow: active on fork default branch main.

## FPA-004 scope decision

FPA-004 state: OUT-OF-SCOPE BY PRODUCT AUTHORITY for the current release target.

The current product scope does not require Windows Authenticode identity or Apple Developer ID/notarization identity. Unsigned developer/internal/direct-use distribution is accepted. Existing signing/notarization plumbing remains implemented and verified as an optional future capability; no signed/notarized artifact claim is made.

Authority/evidence:
- `docs/adr/0003-unsigned-distribution-scope.md`;
- `docs/evidence/FPA-004_SCOPE_DECISION_EVIDENCE.md`.

If public-market signed distribution is required later, reopen FPA-004-F/G and provide real credentials plus real signed/notarized artifact evidence.

## GitHub/upstream status

Canonical verified working branch for release plumbing: fork main.
Fork: nguyenkhactang922-bot/file-tunel-mcp.
Production Release: active on fork main.

Upstream: dongttfd/file-tunel-mcp.
PR #2 remains the upstream integration path.
Connected bot does not have upstream write/merge permission.

## Resume law

On a new chat:
1. confirm git root / branch / HEAD / status;
2. read AGENTS.md and docs/CHATCODE_GLOBAL_MULTI_PROJECT_EXECUTION_LAW.md;
3. read this file and PROJECT_STATE.md;
4. read tasks/FINAL_PRODUCT_AUDIT_FIX_QUEUE.md;
5. resume the single authoritative next action;
6. do not repeat completed OBS/V11/FPA technical work.


## Secure production secret provisioning helper

The helper remains available and verified for a future signed-distribution scope, but production signing credentials are not required by the current unsigned release target.


## V11-009 final technical verification

Technical main verification is PASS on fork main.

Evidence:
- docs/evidence/V11-009_FINAL_TECHNICAL_MAIN_EVIDENCE.md
- native Verify run 35874974993: macOS / Windows x64 / Windows ARM64 SUCCESS
- local Windows Release build: 0 warnings / 0 errors
- Windows runtime suite: 444 assertions PASS
- x64/ARM64 package resources PASS
- x64 packaged app smoke PASS

PR #2 is open, clean and mergeable at the exact verified tree. The connected bot has upstream READ only and cannot execute the final merge.

No repository-internal technical task remains.

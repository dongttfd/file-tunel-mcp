# FPA-006 - PROJECT STATE NORMALIZATION EVIDENCE

Date: 2026-09-22
Status: PASS

## Problem

Project state files contained stale hardcoded Git SHAs and several historical NEXT_EXACT_ACTION entries. A fresh chat could therefore resume from an obsolete commit/task even when Git truth had moved forward.

## Fix

- Rewrote `CURRENT_HANDOFF.md` into a current-only handoff.
- Rewrote `PROJECT_STATE.md` into a current-only project state.
- Converted `tasks/TASK_QUEUE.md` into an explicitly historical completed OBS queue.
- Kept `tasks/FINAL_PRODUCT_AUDIT_FIX_QUEUE.md` as the only current remediation queue.
- Removed self-invalidating hardcoded HEAD values from state documents; Git is now the dynamic SHA source of truth.
- Added `tests/test_project_state_contract.ps1`.

## Regression contract

The test enforces:

- no hardcoded `HEAD:`, `Current branch HEAD:`, or `Baseline main HEAD:` SHA markers;
- handoff/project branch text matches the actual checked-out Git branch;
- exactly one `AUTHORITATIVE NEXT_EXACT_ACTION:` across the active state/queue set;
- historical OBS queue cannot advertise a current next action;
- final-product queue owns the authoritative next action.

## Verification

```text
tests/test_project_state_contract.ps1
PASS
project-state-contract: ok
branch=chatgpt/OBS-001-observability-foundation
authoritative-next=1

git diff --check
PASS
```

Result: FPA-006 PASS.

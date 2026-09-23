# V11-009 - PRE-MERGE FINAL ACCEPTANCE EVIDENCE

Date: 2026-09-22
Status: TECHNICAL MAIN VERIFIED ON FORK / FINAL PASS EXTERNAL-BLOCKED

## OBS-013 prerequisite

PASS. Real ChatGPT connector exposed `filemcp_observability_connect`; the same opaque handle was resumed on a later user turn, normal FileMCP calls remained bound to the same SHA-256 durable session, and the raw handle was absent from SQLite/WAL/SHM. Exact correlated `AI chats` wording is enabled while unbound traffic remains separate.

## Final review findings

- `origin/main` is already an ancestor of the feature branch; no rebase/merge conflict is required before PR.
- `git diff --check origin/main...HEAD`: PASS.
- secret scan returned no API key/runtime key/tunnel-id matches in tracked source outside vendored/build output.
- NuGet vulnerable-package audit: no vulnerable packages reported.
- NuGet direct outdated audit: no direct updates reported.

## Release automation finding and fix

The first V11-009 package attempt correctly failed because the currently connected `FileMCP-release\FileMCP.exe` was locked by Windows. Killing that bridge from the active ChatGPT session would violate the live execution handoff.

`build_windows_app.ps1` now accepts a validated `-StagingName`, preserving the default `FileMCP-release` while allowing isolated final candidates such as `FileMCP-final`. This permits repeatable packaging without overwriting a running bridge.

The runtime restart integration test also uses an 8-second Windows-host allowance instead of 3 seconds; the production restart budget/backoff semantics are unchanged.

## Pre-merge package candidate

Command: `./build_windows_app.ps1 -Architecture x64 -StagingName FileMCP-final`

Result: PASS.

Archive: `dist/FileMCP-v0.4.0-windows-x64.zip`

SHA-256: `3C23BEE2198543CFE6D3C51FB134E31A82034B15FDDAC1FC9785F44603C17F23`

Packaged smoke PASS:

- WPF startup
- close-to-tray
- tunnel-client version
- native SQLite create/write/read
- optional OTLP provider
- OpenTelemetry redistribution notices

## Exact pre-merge worktree verification

- Release build with `-warnaserror`: PASS, 0 warnings, 0 errors.
- Full Windows runtime suite: PASS, 414 assertions.
- Isolated `FileMCP-final` package: PASS.
- Packaged app smoke: PASS.
- Final gate archive SHA-256: `3C23BEE2198543CFE6D3C51FB134E31A82034B15FDDAC1FC9785F44603C17F23`.
- NuGet vulnerable-package audit: no vulnerable packages reported.
- Direct-package outdated audit: no direct updates reported.

## Remaining V11-009 actions

1. Commit reviewed pre-merge changes.
2. Push branch and create/review PR.
3. Merge to `main`.
4. Checkout/pull `main` and rerun Release build/runtime/package smoke.
5. Record `MAIN VERIFIED` and close V11-009 in a final state-only change.

## Pull request review

- Upstream PR: `dongttfd/file-tunel-mcp#2`.
- PR head: `bc5dfda5eca741b7548596ae51e389cfdd15a927` before this evidence-only update.
- GitHub reports `mergeable=true` and `mergeable_state=clean`.
- PR file set: 59 files, matching the reviewed Observability V1/V1.1 implementation/evidence scope.
- GitHub check-runs: none configured/reported for the PR head; local mandatory gates are therefore the authoritative verification for this repository.
- Final local review found no new production blocker after the staging-path packaging fix.

## Local merged-main candidate verification

Because the authenticated GitHub account has only READ permission on `dongttfd/file-tunel-mcp`, the upstream merge endpoint cannot be executed from this session. Before stopping at that permission boundary, V11-009 constructed a local merge candidate directly from `origin/main` and merged the feature branch with Git's `ort` strategy.

Merge candidate commit: `053153185dfb46ce63ade76d5a1ce9ea3bc8cd47`.

Verification on that merged state:

- Release build: PASS, 0 warnings / 0 errors.
- Full Windows runtime suite: PASS, 414 assertions.
- Isolated package staging `FileMCP-main-verify`: PASS.
- Packaged WPF startup / tray / tunnel-client / SQLite / OTLP / notices smoke: PASS.
- Merged-state ZIP SHA-256: `3DFED36D609ED3BB30AC4B9DC902FD04426C108DD889A922354A3C3BB17E70FA`.

## Upstream merge permission blocker

- PR: `dongttfd/file-tunel-mcp#2`.
- GitHub reports the PR mergeable and clean.
- Authenticated CLI account: `nguyenkhactang922-bot`.
- Upstream viewer permission: READ.
- Direct push to upstream returned HTTP 403.
- Merge API returned HTTP 404 (permission-hidden merge endpoint).
- A bot fork was created and the feature branch is pushed there; PR #2 targets upstream `main`.

No technical test or merge-conflict blocker remains. The only remaining action is an upstream account with write/maintain permission merging PR #2. After that, checkout/pull upstream `main`, rerun the same main verification gates, and record V11-009 PASS / MAIN VERIFIED.

GitHub auth recheck (2026-09-22):
- Active CLI account remains `nguyenkhactang922-bot`.
- Upstream permission remains `READ`.
- PR #2 remains open, clean and mergeable.
- Attempt to switch `gh` to account `dongttfd` failed because that account is not logged in on this machine.
- No technical blocker remains; only an upstream WRITE/MAINTAIN-authenticated account can perform the final merge.


## Fork default-branch technical main verification

The complete remediation + release-plumbing tree was merged into fork default branch main without conflict.

Exact fork main commit: 1d3e14d23a089bf36d36c1a3c2eb315005136793.

Local merged-main gates:
- project-state-contract PASS;
- tool-surface-parity PASS;
- macos-supervisor-contract PASS;
- http-connection-bounds-contract PASS;
- dynamic-health-discovery-contract PASS;
- windows-arm64-assurance-contract PASS;
- windows-single-instance-contract PASS;
- windows-release-contract PASS;
- production-release-signing-contract PASS;
- Windows Release build PASS, 0 warnings / 0 errors;
- Windows runtime PASS, 444 assertions.

Native GitHub Verify run 35756276412 on exact fork main commit 1d3e14d:
- verify-macos SUCCESS;
- verify-windows SUCCESS;
- verify-windows-arm64 SUCCESS.

Production Release workflow is registered and active on fork default branch main.

This closes the technical main-verification portion of V11-009 on the canonical fork. V11-009 must remain EXTERNAL-BLOCKED rather than PASS because upstream dongttfd/file-tunel-mcp PR #2 is still unmerged under a READ-only credential and FPA-004 still lacks the seven real production release credentials plus one real signed/notarized release run.

# V11-009 - Final Technical Main Verification Evidence

Date: 2026-09-23
Status: TECHNICAL MAIN VERIFIED / UPSTREAM MERGE AUTHORITY BLOCKED

## Exact verified main

Repository used for final technical verification:
- nguyenkhactang922-bot/file-tunel-mcp
- branch: main
- verified commit before this evidence-only closure commit: 71bc829bb6ea0476c2325a7c4d264b4cd8dd048d

## Scope

- FMR-001: PASS
- OBS-001 through OBS-013: PASS
- V11-001 through V11-008: PASS
- FPA-001, FPA-002, FPA-003, FPA-005, FPA-006, FPA-007, FPA-008, FPA-009: PASS
- FPA-004: OUT-OF-SCOPE for the current unsigned developer/internal/direct-use distribution target under ADR-0003; optional production signing/notarization plumbing remains implemented and verified.

## Final local verification on main

Contracts:
- project-state-contract: PASS
- tool-surface-parity: PASS (required=19)
- macos-supervisor-contract: PASS
- http-connection-bounds-contract: PASS (max=64, idle=15s, header=30s)
- dynamic-health-discovery-contract: PASS
- windows-arm64-assurance-contract: PASS
- windows-single-instance-contract: PASS
- windows-release-contract: PASS
- production-release-signing-contract: PASS

Build/runtime:
- Windows Release build -warnaserror: PASS, 0 warnings / 0 errors
- Windows runtime suite: PASS, 444 assertions

Packaging:
- x64 isolated package/resource verification: PASS
- ARM64 isolated package/resource verification: PASS
- x64 packaged app smoke: PASS
  - WPF startup
  - close-to-tray
  - packaged tunnel-client
  - single-instance activation
  - SQLite write/read
  - optional OTLP provider
  - OpenTelemetry notices

Artifact hashes:
- x64 ZIP SHA-256: 6F6C378918E72321B4E32EB7973C078DA2AE2EFCDBEB855A59C3CFFE3696CDFF
- ARM64 ZIP SHA-256: AEFFFBB1B4248EF2E1776FED031EA0719006C6D3CC15748832D9DE6699595A26

## Native GitHub CI on main

Verify run: 35874974993
Commit: 71bc829bb6ea0476c2325a7c4d264b4cd8dd048d

Jobs:
- verify-macos: SUCCESS
- verify-windows: SUCCESS
- verify-windows-arm64: SUCCESS

Production Release workflow is registered and active on fork default branch main. Signed/notarized production artifacts are not part of the current unsigned release acceptance scope.

## Upstream integration state

Upstream:
- dongttfd/file-tunel-mcp
- PR #2
- state: OPEN
- mergeable: TRUE
- mergeable_state: CLEAN
- PR head updated to exact verified tree 71bc829bb6ea0476c2325a7c4d264b4cd8dd048d

Authenticated GitHub CLI account:
- nguyenkhactang922-bot
- upstream permission: READ

Final merge attempt:
- GitHub merge API returned HTTP 404 because the authenticated account lacks upstream write/maintain merge authority.
- no second GitHub account is configured in local gh auth.

## V11-009 decision

All repository-internal technical acceptance and verified fork-main gates are complete.

V11-009 remains EXTERNAL-BLOCKED only on upstream merge authority because project law still requires upstream integration before final MAIN VERIFIED.

After an upstream account with WRITE/MAINTAIN merges PR #2:
1. fetch and checkout upstream main;
2. confirm merged main contains the PR head;
3. rerun final merged-main confirmation;
4. mark V11-009 PASS / MAIN VERIFIED.

No additional code, security, runtime, release-plumbing, or audit task is open.

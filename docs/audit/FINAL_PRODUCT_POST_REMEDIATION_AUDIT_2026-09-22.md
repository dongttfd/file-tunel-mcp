# FINAL PRODUCT POST-REMEDIATION AUDIT - 2026-09-22

Repo: D:\Tools\FileMCP
Branch: chatgpt/OBS-001-observability-foundation
Audited HEAD before this report commit: 8ba7989b28a7fc8db1595dca71a7efbc03e84559

## Executive conclusion

The repository-internal technical remediation from the independent whole-app audit is complete.

No remaining repo-internal bug/blocker was found in FPA-001, FPA-002, FPA-003, FPA-005, FPA-006, FPA-007, FPA-008 or FPA-009.

FPA-004 production signing/notarization plumbing is implemented and native-CI verified, but the application must NOT be called public-market release ready yet because the real release trust material is external and absent:

- real Windows public code-signing certificate/PFX;
- real Apple Developer ID Application certificate/P12;
- real App Store Connect notarization API key;
- production workflow registration on the chosen canonical default branch;
- one real credentialed release run with post-package verification.

Therefore the correct final state is:

technical remediation: COMPLETE
normal build/test/package CI: VERIFIED
public-market signing plumbing: VERIFIED
public-market signed/notarized release: EXTERNAL-BLOCKED
V11-009 / APP RELEASE READY: NOT YET PASS

## Independent verification rounds

### Round A - Git/state truth

- branch: chatgpt/OBS-001-observability-foundation;
- exact closure HEAD checked: 8ba7989b28a7fc8db1595dca71a7efbc03e84559;
- worktree clean at audit start;
- project-state-contract PASS;
- exactly one authoritative NEXT_EXACT_ACTION.

### Round B - cross-platform product contracts

PASS:

- tool-surface-parity: required=19;
- macos-supervisor-contract;
- HTTP connection bounds: max=64, idle=15s, header=30s;
- dynamic health discovery via official tunnel-client health.url-file;
- Windows ARM64 native assurance;
- Windows single-instance behavior;
- Windows release staging contract;
- production release signing contract.

### Round C - Windows runtime/build

Latest local evidence:

- Release build warnings-as-errors: PASS, 0 warnings / 0 errors;
- full Windows runtime suite: PASS, 444 assertions;
- Windows x64 package build: PASS;
- Windows ARM64 package build: PASS;
- x64 packaged app startup/tray/tunnel-client/single-instance/SQLite/OTLP/notices smoke: PASS.

### Round D - native CI

Exact closure run 35753187492:

- verify-macos: SUCCESS;
- verify-windows: SUCCESS;
- verify-windows-arm64: SUCCESS.

Production-signing implementation run 35752248896 also succeeded on all three native jobs.

### Round E - dependency hygiene

NuGet vulnerable audit:

- FileMCP.Core: no vulnerable packages;
- FileMCP.App: no vulnerable packages;
- FileMCP.Core.Tests: no vulnerable packages.

NuGet outdated audit:

- FileMCP.Core: no updates;
- FileMCP.App: no updates;
- FileMCP.Core.Tests: no updates.

### Round F - signing-secret hygiene

PASS:

- no tracked .pfx;
- no tracked .p12;
- no tracked .p8;
- no tracked private-key PEM markers;
- normal Verify workflow references no production signing secret;
- local build scripts remain unsigned;
- release credential files are runner-temp only;
- cleanup paths exist.

### Round G - release-environment boundary

Fork repository:
nguyenkhactang922-bot/file-tunel-mcp

production-release environment:
- created;
- custom deployment branch policy enabled;
- allowed branch: main only;
- release secrets configured: none.

Upstream:
dongttfd/file-tunel-mcp

PR #2:
- OPEN;
- base main;
- head points to the current feature branch;
- connected bot upstream permission remains read-only.

## FPA closure table

| Finding | Final state | Evidence |
|---|---|---|
| FPA-001 CI staging parity | PASS | canonical Windows release staging verified |
| FPA-002 macOS tool/logical-chat parity | PASS | native CI |
| FPA-003 macOS tunnel supervisor parity | PASS | native CI |
| FPA-004 production signing/notarization | EXTERNAL-BLOCKED after PLUMBING VERIFIED | release workflow/scripts/contracts + native CI; real credentials/run missing |
| FPA-005 Windows ARM64 assurance | PASS | native ARM64 hosted runner |
| FPA-006 state normalization | PASS | project-state contract |
| FPA-007 desktop single instance | PASS | native x64/ARM64 |
| FPA-008 bounded local MCP connections | PASS | 64 cap + idle/header deadline + native CI |
| FPA-009 dynamic health discovery | PASS | official health.url-file discovery + native CI |

## Exact remaining external actions

The canonical release owner must:

1. merge/register .github/workflows/release.yml on the chosen canonical default branch;
2. configure these seven production-release Environment secrets:
   - WINDOWS_CODESIGN_PFX_BASE64
   - WINDOWS_CODESIGN_PFX_PASSWORD
   - MACOS_DEVELOPER_ID_P12_BASE64
   - MACOS_DEVELOPER_ID_P12_PASSWORD
   - APPLE_NOTARY_KEY_P8_BASE64
   - APPLE_NOTARY_KEY_ID
   - APPLE_NOTARY_ISSUER_ID
3. dispatch Production Release with version 0.4.0;
4. retain evidence that Windows x64/ARM64 post-package Authenticode checks are Valid with trusted timestamps;
5. retain evidence that macOS notarization is Accepted, staple validation passes and Gatekeeper accepts the final packaged app.

Only after those external release-trust actions succeed may FPA-004, V11-009 and APP RELEASE READY be marked PASS.

## Final audit decision

No additional repository-internal feature/security/runtime bug is opened by this post-remediation audit.

The project is technically verified up to the external public-distribution trust boundary. The remaining blocker is not another code bug; it is real release identity/credential ownership plus canonical default-branch release execution.


## Default-branch registration addendum

After the initial post-remediation audit, the full verified remediation/release tree was merged into the fork default branch main without conflict. Exact main commit 884e9a89a98ddd5343bb4d3e6aef8b4499f810cb passed GitHub Actions run 35755705022 on verify-macos, verify-windows and verify-windows-arm64. GitHub now registers the Production Release workflow on the default branch. The production-release Environment exists and is restricted to branch main.

Accordingly, default-branch workflow registration is no longer an external blocker. The sole remaining public-market blocker is the absence of the seven real production release secrets and the resulting absence of a real signed/notarized release execution.


## Credential boundary recheck

A metadata-only credential inventory was performed after default-branch registration. No private-key material or passwords were read/exported.

Results:
- usable Windows code-signing identities with private key + Code Signing EKU + nonexpired certificate in CurrentUser/My or LocalMachine/My: 0;
- local release environment variables present: 0/7;
- GitHub production-release Environment secrets present: 0/7.

Therefore FPA-004 cannot advance to a real public-trust release without external credential provisioning.

## Real Production Release credential-boundary run

Default-branch Production Release run 35762454809 was dispatched for version 0.4.0. Windows x64, Windows ARM64 and macOS all entered the intended signing-material validation step and failed only because the corresponding real production Environment secrets were absent. This removes workflow registration and dispatch from the blocker list. The sole remaining FPA-004 blocker is provision of the real release identity credentials followed by one successful signed/notarized run.

## Final fail-fast release-boundary verification

Production Release run 35764732944 on default-branch main used the new credential preflight. The preflight failed because all seven required Environment secret names are absent; Windows x64, Windows ARM64 and macOS release jobs were skipped. The implementation commit passed normal Verify run 35764406983 on all three native platform jobs. No further repository-internal release defect is open.

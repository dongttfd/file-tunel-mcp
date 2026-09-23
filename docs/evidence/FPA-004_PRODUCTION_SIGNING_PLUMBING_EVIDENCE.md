# FPA-004 - Production Signing / Notarization Plumbing Evidence

Date: 2026-09-22
Status: PLUMBING + DEFAULT-BRANCH REGISTRATION VERIFIED / EXTERNAL-BLOCKED ONLY ON REAL RELEASE CREDENTIALS + REAL SIGNED RELEASE RUN

## Implementation commits

- 968e05a - frozen production signing release design.
- 934fb73 - production signing/notarization workflow, scripts, secret hygiene, contracts and release documentation.

## Native Verify evidence

GitHub Actions run 35752248896 on implementation commit 934fb73:

- verify-macos: SUCCESS
- verify-windows: SUCCESS
- verify-windows-arm64: SUCCESS

## Local verification

- production-release-signing-contract: PASS
- project-state-contract: PASS
- tool-surface-parity: PASS
- macos-supervisor-contract: PASS
- http-connection-bounds-contract: PASS
- dynamic-health-discovery-contract: PASS
- windows-arm64-assurance-contract: PASS
- windows-single-instance-contract: PASS
- windows-release-contract: PASS
- Windows Release build: PASS, 0 warnings / 0 errors
- Windows runtime suite: PASS, 444 assertions
- x64 candidate package build: PASS
- ARM64 candidate package build: PASS
- x64 packaged app smoke: PASS
- no tracked PFX/P12/P8: PASS
- no tracked private-key marker: PASS
- git diff --check: PASS

The local self-signed Authenticode smoke was intentionally not accepted as production evidence. This Windows desktop execution context refused noninteractive CurrentUser Root trust import with "UI is not allowed in this operation"; cleanup was verified and no test certificate remained.

## Release environment state

Repository: nguyenkhactang922-bot/file-tunel-mcp

- production-release GitHub Environment: CREATED.\n- deployment branch policy: custom policy, allowed branch main only.
- Environment release secrets: NONE CONFIGURED.
- Repository release secrets: NONE CONFIGURED.
- Fork permissions for connected bot: admin/push available.

Required Environment secrets still missing:

1. WINDOWS_CODESIGN_PFX_BASE64
2. WINDOWS_CODESIGN_PFX_PASSWORD
3. MACOS_DEVELOPER_ID_P12_BASE64
4. MACOS_DEVELOPER_ID_P12_PASSWORD
5. APPLE_NOTARY_KEY_P8_BASE64
6. APPLE_NOTARY_KEY_ID
7. APPLE_NOTARY_ISSUER_ID

## Default-branch / upstream boundary

Fork default branch: main.

The Production Release workflow exists on the feature branch. GitHub lists manual workflows from the default branch, so the new workflow is not dispatchable as a production workflow until it is merged into a default branch.

Upstream PR:
- repo: dongttfd/file-tunel-mcp
- PR: #2
- base: main
- head branch: chatgpt/OBS-001-observability-foundation
- implementation head observed: 934fb734a60fb339858a2e5faa560f1921523fb7
- state: OPEN
- connected bot upstream permission: read-only

## Exact remaining acceptance

FPA-004 PASS requires all of:

1. merge/register the Production Release workflow on the chosen canonical default branch;
2. provision the seven real release secrets into production-release;
3. manually dispatch Production Release for source version 0.4.0;
4. Windows x64 post-package Authenticode verification PASS with trusted timestamp;
5. Windows ARM64 post-package Authenticode verification PASS with trusted timestamp;
6. macOS Developer ID hardened-runtime signature PASS;
7. Apple notarization status Accepted;
8. notarization ticket stapled and validated;
9. Gatekeeper assessment accepted;
10. final signed/notarized artifacts uploaded without secret leakage.

Until those real credentialed checks exist, FPA-004 is EXTERNAL-BLOCKED, not PASS.


## Default-branch registration closure

Fork default branch main now contains the complete remediation/release tree.

Exact verified main commit: 884e9a89a98ddd5343bb4d3e6aef8b4499f810cb.

GitHub Actions run 35755705022 on that exact default-branch commit:
- verify-macos: SUCCESS
- verify-windows: SUCCESS
- verify-windows-arm64: SUCCESS

Workflow registry now exposes Production Release as active. Environment production-release is restricted to branch main. Configured production-release Environment secrets: none.

The previous default-branch/workflow-registration blocker is CLOSED. The only remaining FPA-004 acceptance blocker is real production trust material plus one real credentialed Production Release run.


## Credential boundary recheck

A metadata-only credential inventory was performed after default-branch registration. No private-key material or passwords were read/exported.

Results:
- usable Windows code-signing identities with private key + Code Signing EKU + nonexpired certificate in CurrentUser/My or LocalMachine/My: 0;
- local release environment variables present: 0/7;
- GitHub production-release Environment secrets present: 0/7.

Therefore FPA-004 cannot advance to a real public-trust release without external credential provisioning.

## Real default-branch Production Release boundary proof

Production Release was dispatched manually from fork main for version 0.4.0.

Run: 35762454809.

All three jobs reached the intended credential preflight and failed only because real production credentials are not configured:

- release-windows-x64: WINDOWS_CODESIGN_PFX_BASE64 is not configured.
- release-windows-arm64: WINDOWS_CODESIGN_PFX_BASE64 is not configured.
- release-macos-arm64: MACOS_DEVELOPER_ID_P12_BASE64 is not configured.

This proves default-branch workflow registration and dispatch are working. The remaining blocker is real release identity material, not workflow registration or repository code.

A non-secret readiness helper exists at release/check_production_release_readiness.ps1; it checks workflow registration and the seven required Environment secret names, and can dispatch only after all names are present.

## Readiness checker live result

release/check_production_release_readiness.ps1 was executed against the canonical fork.

Result:
- Production Release workflow: active
- default branch: main
- version: 0.4.0
- configured required Environment secrets: 0/7
- exit code: 2, intentionally indicating not ready

No secret values were read or printed.

## Fail-fast production credential preflight proof

After adding a dedicated release-preflight job, Production Release was dispatched again from main for version 0.4.0.

Run: 35764732944.

Result:
- release-preflight: FAILURE because all seven required production-release Environment secrets are absent;
- release-windows-x64: SKIPPED;
- release-windows-arm64: SKIPPED;
- release-macos-arm64: SKIPPED.

The preflight log listed exactly the seven expected missing secret names and no secret values. This proves the workflow now fails early without consuming platform build/signing runners when credentials are not configured.

The preflight implementation commit 277e52c passed Verify run 35764406983 on macOS, Windows x64 and Windows ARM64.

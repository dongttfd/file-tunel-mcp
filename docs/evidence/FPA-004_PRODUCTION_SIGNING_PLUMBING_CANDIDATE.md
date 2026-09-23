# FPA-004 - Production Signing / Notarization Plumbing Candidate Evidence

Date: 2026-09-22
Status: ACTIVE CANDIDATE - NATIVE VERIFY REQUIRED, REAL RELEASE CREDENTIALS NOT YET CONFIGURED

## Frozen authority

- docs/design/FPA-004_PRODUCTION_SIGNING_FROZEN.md
- docs/design/FPA-004_INDEPENDENT_REVIEW.md
- docs/design/FPA-004_DECISION_MATRIX.md
- tasks/FPA-004_TASK_GRAPH.md

Architecture was frozen in commit 968e05a before implementation.

## Implemented release plumbing

Windows:
- release/sign_windows_release.ps1;
- imports PFX through SecureString into CurrentUser certificate store;
- requires private key, Code Signing EKU and currently-valid certificate;
- uses native SignTool SHA-256 + RFC3161 timestamping;
- verifies staged FileMCP.exe with SignTool and Get-AuthenticodeSignature;
- requires trusted timestamp certificate;
- recreates ZIP after signing;
- extracts final ZIP and verifies embedded FileMCP.exe again;
- removes imported certificate in finally.

macOS:
- release/sign_notarize_macos.sh;
- temporary keychain with generated password;
- imports Developer ID Application P12;
- signs nested tunnel-client then outer FileMCP.app with hardened runtime + secure timestamp;
- strict/deep code-sign verification;
- submits notarization ZIP with notarytool/App Store Connect API key;
- requires Accepted;
- staples + validates ticket;
- runs Gatekeeper spctl assessment;
- packages only after stapling;
- extracts final ZIP and verifies signature/staple/Gatekeeper again;
- deletes temporary keychain/work material in trap cleanup.

GitHub:
- .github/workflows/release.yml;
- workflow_dispatch-only;
- environment: production-release;
- contents: read only;
- separate Windows x64, Windows ARM64 and macOS arm64 release jobs;
- credential files are materialized only in RUNNER_TEMP;
- cleanup steps run with if: always();
- final artifacts upload only after signing/notarization steps.

Secret hygiene:
- .gitignore blocks *.pfx, *.p12 and *.p8;
- tests/test_release_signing_contract.ps1 rejects tracked signing key files;
- Verify workflow must not reference production release secret names;
- ordinary build scripts remain unsigned.

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
- macOS release script bash syntax: PASS
- Windows release signer PowerShell parse: PASS
- Windows Release build -warnaserror: PASS, 0 warnings / 0 errors
- Windows runtime suite: PASS, 444 assertions
- Windows x64 release candidate build: PASS
- Windows ARM64 release candidate build: PASS
- Windows x64 packaged app smoke: PASS
- git diff --check: PASS

Local self-signed Authenticode plumbing smoke could not be completed because this desktop execution context refused noninteractive trust-store import with "UI is not allowed in this operation". Cleanup verification confirmed no test certificate remained. This is not counted as production evidence.

## Current external credential truth

Connected fork inspection:
- repository secret names: none configured;
- GitHub environments: none configured;
- upstream permission for connected bot: read-only.

Therefore no real Developer ID/notarization or public Windows code-signing run can be executed yet.

## Remaining acceptance

1. native Verify CI on the implementation commit must succeed for macOS, Windows x64 and Windows ARM64;
2. configure protected environment production-release;
3. configure the exact seven required environment secrets documented in README;
4. execute Production Release workflow on the verified release commit;
5. capture signed/notarized/stapled post-package evidence.

FPA-004 must not be marked PASS until step 4/5 succeeds with real credentials.

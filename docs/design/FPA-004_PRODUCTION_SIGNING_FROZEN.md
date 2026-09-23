# FPA-004 - Production Signing / Notarization - Frozen Design

Date: 2026-09-22
Status: FROZEN BEFORE IMPLEMENTATION

## 1. Goal

Add a production-only release gate that produces verifiably trusted public-distribution artifacts while preserving unsigned local/developer builds.

## 2. Separation of concerns

Existing verify.yml remains the normal unsigned CI and must continue to run on push/pull_request without signing credentials.

A separate release.yml is the only workflow allowed to consume signing/notarization secrets. It is workflow_dispatch-only and uses the GitHub environment `production-release`.

No signing credential, certificate payload, private key, password or notarization token may enter source, logs, cache or uploaded artifacts.

## 3. Windows release design

Targets: x64 and ARM64.

Build first with the existing build_windows_app.ps1, then sign the staged FileMCP.exe before recreating the final ZIP.

Credential model:
- secret WINDOWS_CODESIGN_PFX_BASE64: base64 PKCS#12/PFX bytes;
- secret WINDOWS_CODESIGN_PFX_PASSWORD: PFX password.

Workflow decodes the PFX only into RUNNER_TEMP.

release/sign_windows_release.ps1:
1. locate staged FileMCP.exe;
2. import PFX into Cert:\CurrentUser\My using a SecureString;
3. require private key + Code Signing EKU + currently valid certificate;
4. locate Windows SDK signtool;
5. sign with SHA-256 digest and RFC3161 SHA-256 timestamp;
6. verify with signtool /pa and Get-AuthenticodeSignature;
7. require Status=Valid and a non-null timestamp certificate;
8. delete/recreate the package ZIP from signed staging content;
9. expand the ZIP into a temp directory and verify the packaged FileMCP.exe signature again;
10. remove the imported cert and temporary expanded content in finally.

The vendored tunnel-client binary is checksum-verified before packaging and is not re-signed by FileMCP.

No installer exists, so there is no additional installer signing target in FPA-004.

## 4. macOS release design

Current distributable target: darwin-arm64, matching the vendored tunnel-client inventory.

Credential model:
- secret MACOS_DEVELOPER_ID_P12_BASE64;
- secret MACOS_DEVELOPER_ID_P12_PASSWORD;
- secret APPLE_NOTARY_KEY_P8_BASE64;
- secret APPLE_NOTARY_KEY_ID;
- secret APPLE_NOTARY_ISSUER_ID.

Workflow decodes certificate/key files only into RUNNER_TEMP.

release/sign_notarize_macos.sh:
1. create a random-password temporary keychain;
2. import Developer ID Application P12 into that keychain;
3. require exactly one usable Developer ID Application identity;
4. sign the bundled tunnel-client as nested code with hardened runtime + secure timestamp;
5. sign the outer FileMCP.app with hardened runtime + secure timestamp;
6. codesign --verify --deep --strict;
7. create a temporary notarization ZIP using ditto;
8. submit with xcrun notarytool + App Store Connect API key and wait for Accepted;
9. staple the notarization ticket;
10. validate the staple;
11. verify code signature again;
12. require Gatekeeper spctl assessment to succeed;
13. create final versioned ZIP from the stapled app;
14. delete the temporary keychain and temporary submission material in trap/final cleanup.

No entitlements are introduced by FPA-004. If future features require entitlements, that is a new architecture decision.

## 5. Release workflow

.github/workflows/release.yml:
- workflow_dispatch only;
- explicit version input, checked against source version;
- environment: production-release;
- permissions: contents: read;
- independent jobs:
  - release-windows-x64 on windows-2025;
  - release-windows-arm64 on windows-11-vs2026-arm;
  - release-macos-arm64 on macos-26;
- each job validates required secret presence without printing values;
- each job builds, signs/notarizes, verifies, then uploads only final release artifacts;
- credential files live only in RUNNER_TEMP and are deleted in an `if: always()` cleanup step.

## 6. Developer-build behavior

Unchanged:
- build_windows_app.ps1 remains unsigned;
- build_macos_app.sh remains unsigned;
- verify.yml needs no release secret and continues to exercise ordinary developer artifacts.

## 7. Secret-safety contract

Add tests/test_release_signing_contract.ps1 to enforce:
- dedicated release workflow exists and is manual-only;
- production-release environment is referenced;
- exact secret names are referenced but no credential value is committed;
- Windows release invokes Authenticode sign + verification + timestamp validation;
- macOS release invokes hardened-runtime codesign, notarytool, stapler and Gatekeeper verification;
- credential file extensions are ignored;
- ordinary build scripts remain credential-free/unsigned.

verify.yml runs this contract without secrets.

## 8. Acceptance levels

### Plumbing-complete
Can be reached without real certificates:
- release scripts/workflow/docs/contracts committed;
- ordinary CI all green;
- syntax/contract verification passes;
- no secrets configured in repository is explicitly recorded.

### FPA-004 PASS / public-market-ready
Requires real protected release credentials and one real release workflow run proving:
- Windows x64 FileMCP.exe Authenticode Status=Valid + trusted timestamp;
- Windows ARM64 FileMCP.exe Authenticode Status=Valid + trusted timestamp;
- macOS Developer ID signature valid with hardened runtime;
- Apple notarization Accepted;
- ticket stapled and validated;
- Gatekeeper assessment accepted;
- final signed/stapled artifacts uploaded;
- no secret leakage.

Without that real run FPA-004 must remain EXTERNAL-BLOCKED, never falsely marked PASS.

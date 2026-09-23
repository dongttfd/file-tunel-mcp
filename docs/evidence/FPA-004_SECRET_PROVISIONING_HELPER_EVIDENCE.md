# FPA-004 - Secure Production Secret Provisioning Helper Evidence

Date: 2026-09-23
Status: PASS (TOOLING ONLY; REAL RELEASE CREDENTIALS STILL EXTERNAL)

## Purpose

Provide a safe operator path to configure the seven already-frozen production-release GitHub Environment secrets without pasting certificate/private-key material or passwords into chat, command history, or repository files.

## Added

- docs/design/FPA-004_SECRET_PROVISIONING_ADDENDUM.md
- release/configure_production_release_secrets.ps1
- tests/test_release_secret_provisioning_contract.ps1
- Verify workflow coverage on Windows x64 and Windows ARM64
- README operator instructions

## Security properties

- PFX/P12/P8 files must exist outside the FileMCP repository.
- Secret file bytes are base64-encoded only in process memory.
- Values are streamed to gh secret set through redirected stdin.
- gh --body is deliberately not used.
- Windows/macOS certificate passwords are requested with Read-Host -AsSecureString.
- SecureString BSTR buffers are zeroed with ZeroFreeBSTR.
- No dotenv/plaintext secret file is created.
- The helper prints only configured secret names.
- Repository/environment identifiers are validated before constructing gh arguments.
- The implementation is compatible with Windows PowerShell 5.1.

## Verification

- Windows PowerShell version used for local contract proof: 5.1.19041.2673.
- release-secret-provisioning-contract: PASS.
- production-release-signing-contract: PASS.
- project-state-contract: PASS.
- git diff --check: PASS.
- Negative proof: a dummy PFX placed inside the repository was rejected before any password prompt or GitHub secret operation.
- Dummy credential cleanup: PASS; no test PFX remained.

## Boundary

This helper does not create or fake signing identity. FPA-004 remains EXTERNAL-BLOCKED until real Windows code-signing and Apple Developer/notarization credentials are supplied and one real Production Release succeeds.

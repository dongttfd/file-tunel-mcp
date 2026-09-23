# FPA-004 - Secure Release Secret Provisioning Addendum

Date: 2026-09-23
Status: FROZEN TOOLING ADDENDUM

## Purpose

The production signing architecture is already frozen and verified. This addendum adds only a local operator helper for provisioning the seven existing production-release Environment secrets without pasting private keys/passwords into chat, command history, or repository files.

No release secret name, signing step, or acceptance criterion changes.

## Safety rules

- Credential files must live outside the repository tree.
- PFX/P12/P8 file bytes are read locally, base64-encoded in memory, and streamed to gh secret set through redirected standard input.
- PFX/P12 passwords are requested with Read-Host -AsSecureString; they are converted to plaintext only briefly in process memory, written to gh stdin, then the unmanaged SecureString buffer is zeroed.
- Apple key ID and issuer ID are read interactively if not provided.
- Secret values are never printed.
- No dotenv file, temporary credential copy, or plaintext secret file is created.
- The helper validates GitHub authentication and the target Environment before provisioning.
- The helper runs release/check_production_release_readiness.ps1 after provisioning and can optionally dispatch the real release only after all required secret names are present.

## Acceptance

- helper syntax/contract test PASS;
- normal Verify remains credential-free;
- helper contains no committed secret value;
- secret transport uses stdin, not CLI --body;
- readiness checker remains the authority before dispatch.

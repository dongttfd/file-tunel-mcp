# FPA-004 Scope Decision Evidence

Date: 2026-09-23
Decision: production signing/notarization is not required for the current FileMCP release scope.
Authority: project/product owner.

## Verified repository state before scope decision

- Authenticode signing/timestamp verification plumbing: implemented and verified.
- macOS Developer ID/notarization/staple/Gatekeeper plumbing: implemented and verified.
- Secure production-secret provisioning helper: implemented and verified.
- Production Release workflow: active.
- Real signing credentials: absent.
- No real signed/notarized artifact proof exists.

## Closure classification

FPA-004 is **OUT-OF-SCOPE BY PRODUCT AUTHORITY** for the current unsigned developer/internal/direct-use distribution target.

This is not equivalent to `PASS` for signed public-market distribution. If that distribution target returns, FPA-004 must be reopened at the real-credential execution/evidence stage.

## Remaining whole-app gate

V11-009 remains pending only on upstream integration/merge authority, unless project authority separately declares the verified fork `main` canonical.

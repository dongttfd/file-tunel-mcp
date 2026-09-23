# ADR-0003 - Unsigned Distribution Scope

Status: ACCEPTED
Date: 2026-09-23
Authority: project/product owner decision

## Decision

The current FileMCP product/release scope does not require production code signing or Apple notarization.

The supported acceptance target is unsigned developer/internal/direct-use distribution. Public-market distribution with trusted publisher identity is explicitly outside the current release acceptance scope.

## Consequences

- Windows PFX, macOS Developer ID P12, and Apple notarization P8/IDs are not required to close the current technical program.
- FPA-004 is closed as `OUT-OF-SCOPE BY PRODUCT AUTHORITY`, not as a successful signed-release proof.
- Existing Authenticode / Developer ID / notarization plumbing remains in the repository as an optional future capability and must not be weakened.
- The `production-release` workflow may continue to fail-fast when real signing credentials are absent; that is not a blocker for the current unsigned scope.
- Documentation and state must not claim that unsigned artifacts are trusted/signed/notarized or suitable for a public-market release requiring those guarantees.
- If public-market signed distribution is reintroduced later, FPA-004-F/G must be reopened and completed with real credentials and real artifact verification.

## Current final gate

With FPA-004 removed from the current acceptance scope, the remaining V11-009 closure condition is upstream integration/merge by an account with write/maintain authority, unless project authority separately designates the verified fork `main` as the final canonical main.

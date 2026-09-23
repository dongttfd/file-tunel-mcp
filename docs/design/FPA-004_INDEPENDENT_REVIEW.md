# FPA-004 - Independent Multi-Round Release Review

Date: 2026-09-22

## Round 1 - CI separation
Signing secrets must never be available to push/PR verification. Decision: dedicated workflow_dispatch-only release workflow.

## Round 2 - Windows credential exposure
Passing the PFX password to signtool would expose it in a process command line. Decision: import PFX with SecureString, then sign by certificate thumbprint; signtool never receives the password.

## Round 3 - Windows package order
Signing after ZIP creation would leave the uploaded ZIP unsigned internally. Decision: build -> sign staged EXE -> verify -> recreate ZIP -> verify EXE extracted from ZIP.

## Round 4 - Windows scope
There is no installer/MSIX. Decision: sign FileMCP.exe only; do not invent an installer task.

## Round 5 - macOS nested code
The bundled tunnel-client is executable nested code. Decision: sign nested executable before signing the outer app; verify deep/strict after signing.

## Round 6 - hardened runtime
Public Developer ID notarization expects hardened runtime for application code. Decision: use codesign --options runtime --timestamp; no new entitlements.

## Round 7 - notarization credential model
Apple ID passwords are operationally weaker and harder to rotate cleanly. Decision: App Store Connect API key via notarytool.

## Round 8 - keychain isolation
Do not alter the runner login keychain permanently. Decision: create/delete a temporary keychain and target it explicitly for codesign.

## Round 9 - secret leakage
Credential bytes must never become build artifacts or shell traces. Decision: RUNNER_TEMP only, no xtrace, cleanup always, artifact paths restricted to dist outputs.

## Round 10 - truthful closure
A syntactically correct signing workflow is not proof of production trust. Decision: plumbing may be complete, but FPA-004 PASS requires a real signed/notarized run with real credentials.

Final review: architecture approved. External certificate/notarization credentials are the only allowed remaining blocker after implementation.

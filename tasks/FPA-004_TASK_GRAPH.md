# FPA-004 - Frozen Task Graph

| Task | Depends | Action | Acceptance |
|---|---|---|---|
| FPA-004-A | freeze | Windows signing script | syntax + contract |
| FPA-004-B | A | macOS sign/notarize/staple script | bash syntax + contract |
| FPA-004-C | A,B | dedicated production release workflow | manual-only + environment + secret-safe |
| FPA-004-D | C | README/release setup docs + gitignore hardening | docs/contract PASS |
| FPA-004-E | A-D | ordinary verify CI regression | macOS + Win x64 + Win ARM64 SUCCESS |
| FPA-004-F | E + external credentials | real release workflow execution | signed/notarized artifact evidence |
| FPA-004-G | F | final evidence/state closure | FPA-004 PASS |

If release credentials are unavailable after E, set FPA-004 EXTERNAL-BLOCKED with exact missing secret names and do not claim market-ready.

## Current scope override (2026-09-23)

ADR-0003 removes signed/notarized public-market distribution from the current acceptance target. FPA-004-F/G are deferred and the item is classified OUT-OF-SCOPE rather than PASS. Reopen them if signed public distribution becomes required.

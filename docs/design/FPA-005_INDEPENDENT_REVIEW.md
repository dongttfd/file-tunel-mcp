# FPA-005 - Independent Review

Date: 2026-09-22

## Round 1 - Build vs runtime

Cross-publishing win-arm64 on x64 proves buildability only. It cannot prove native execution.

Decision: require native ARM64 job.

## Round 2 - Test duplication

Copying the x64 smoke script would create drift.

Decision: one parameterized smoke script, identical assertions for x64 and ARM64.

## Round 3 - CI cost/scope

The full 414-assertion Windows core suite does not need to run twice for this task.

Decision: x64 keeps full core suite; ARM64 job focuses on native package/runtime smoke.

## Round 4 - Tunnel-client

The ARM64 vendored tunnel-client must execute on the ARM64 runner, not only have its hash checked on x64.

Decision: run `--version` natively as part of package smoke.

## Round 5 - Platform truth

GitHub currently provides standard Windows ARM64 hosted runners, so a documentation-only limitation is no longer justified.

Decision: native assurance is mandatory.

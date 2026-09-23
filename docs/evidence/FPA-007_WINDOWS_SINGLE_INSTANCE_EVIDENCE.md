# FPA-007 - Windows Desktop Single-Instance Evidence

Date: 2026-09-22
Status: PASS

## Architecture

Frozen before implementation:

- docs/design/FPA-007_WINDOWS_SINGLE_INSTANCE_FROZEN.md
- docs/design/FPA-007_INDEPENDENT_REVIEW.md
- docs/design/FPA-007_DECISION_MATRIX.md
- tasks/FPA-007_TASK_GRAPH.md

## Implemented

- Per-user named-pipe first-instance coordinator in FileMCP.Core.
- PipeOptions.FirstPipeInstance + CurrentUserOnly.
- Pipe name hashes the Windows user SID; raw SID is not exposed.
- One bounded activation command: activate.
- Secondary connect/read paths are timeout-bounded.
- Unknown/oversized commands are ignored.
- Secondary launch exits before constructing MainWindow.
- Primary restores/focuses tray-hidden MainWindow.
- Runtime ProfileLock remains unchanged.
- Packaged smoke launches a real second FileMCP.exe and verifies activation.
- Static single-instance contract runs in x64 and ARM64 Windows jobs.

## Local evidence

```text
Windows Release build: PASS, 0 warnings / 0 errors
Windows runtime suite: PASS, 423 assertions
windows-desktop-single-instance: ok
x64 packaged duplicate-launch smoke: PASS
windows-single-instance-activation-x64: ok
git diff --check: PASS
```

## Native GitHub evidence

Accepted candidate commit: `4325eec`
GitHub Actions run: `35728230159`

```text
verify-macos:          SUCCESS
verify-windows:        SUCCESS
verify-windows-arm64:  SUCCESS
```

Native markers:

```text
windows-desktop-single-instance: ok
windows-app-startup-x64: ok
windows-single-instance-activation-x64: ok
windows-app-startup-arm64: ok
windows-single-instance-activation-arm64: ok
```

## Result

FPA-007 PASS. A second FileMCP desktop launch now activates the existing tray instance instead of creating another GUI/runtime owner.

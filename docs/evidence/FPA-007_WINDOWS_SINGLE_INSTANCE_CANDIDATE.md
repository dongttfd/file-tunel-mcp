# FPA-007 - Windows Desktop Single-Instance Candidate Evidence

Date: 2026-09-22
Status: CANDIDATE - NATIVE CI REQUIRED

## Frozen architecture

- docs/design/FPA-007_WINDOWS_SINGLE_INSTANCE_FROZEN.md
- docs/design/FPA-007_INDEPENDENT_REVIEW.md
- docs/design/FPA-007_DECISION_MATRIX.md
- tasks/FPA-007_TASK_GRAPH.md

## Implemented

- Added DesktopSingleInstanceCoordinator in FileMCP.Core.
- Uses per-user named pipe with PipeOptions.FirstPipeInstance and CurrentUserOnly.
- Pipe name includes a hash of the Windows user SID; raw SID is not exposed.
- Protocol is bounded to one 64-byte activation command.
- Secondary connect and primary read paths are timeout-bounded.
- Unknown/oversized messages do not activate the primary.
- Secondary process signals primary and exits before creating MainWindow.
- Primary listener dispatches to WPF and restores/focuses the tray-hidden window.
- Runtime ProfileLock remains unchanged as defense in depth.
- Packaged app smoke now launches a real second FileMCP.exe and verifies:
  - second process exits;
  - first process remains alive;
  - first process receives activation;
  - tray-hidden window activation path runs.
- Added static single-instance contract to x64 and ARM64 CI jobs.

## Local verification

```text
Windows Release build -warnaserror
PASS - 0 warnings / 0 errors

Windows runtime suite
PASS - 423 assertions
windows-desktop-single-instance: ok

Packaged x64 smoke
PASS:
- WPF startup
- close-to-tray
- tunnel-client
- single-instance second-launch activation
- SQLite
- OTLP
- notices

x64 package SHA-256
512DEB287AA2E87D043AA5BA6A1A36922A86B3AAE83276A7B93D7E6F2F510143

tests/test_windows_single_instance_contract.ps1
PASS

git diff --check
PASS
```

## Remaining acceptance

GitHub must prove on the candidate commit:

- verify-macos: SUCCESS;
- verify-windows: SUCCESS;
- verify-windows-arm64: SUCCESS;
- native ARM64 packaged smoke includes windows-single-instance-activation-arm64: ok.

FPA-007 remains ACTIVE until native CI is green.

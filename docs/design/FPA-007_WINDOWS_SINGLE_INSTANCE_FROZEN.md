# FPA-007 - Windows Desktop Single-Instance / Activation - Frozen Design

Date: 2026-09-22
Status: FROZEN BEFORE IMPLEMENTATION

## Problem

FileMCP is intentionally one desktop app managing C/D/E/F runtimes. Today Windows allows multiple FileMCP GUI processes to coexist. Profile locks prevent two runtimes from owning the same tunnel profile only after Connect, so users can end up with several tray icons/builds and confusing port/profile errors.

The real acceptance incident on this machine found eight FileMCP processes from multiple build directories while only one owned the active tunnel.

## Selected architecture

Use a per-user **named pipe first-instance coordinator**.

The first process creates a named pipe server with `PipeOptions.FirstPipeInstance` and becomes the primary GUI instance.

A later process:

1. cannot create the first pipe instance;
2. connects as a pipe client;
3. sends one bounded command: `activate`;
4. exits before constructing MainWindow.

The primary process listens for `activate`, then dispatches to the WPF UI thread and calls the existing tray restore/focus behavior.

## Why named pipe instead of mutex-only

Named pipe gives both:

- exclusivity;
- activation IPC.

It avoids a second named event, mutex thread-affinity on ReleaseMutex, and stale ownership complexity. OS handle cleanup removes the pipe if the primary crashes.

## Scope

Windows desktop app only.

macOS launch behavior is not changed by FPA-007.

## Coordinator placement

Add a testable coordinator in FileMCP.Core:

`DesktopSingleInstanceCoordinator`

Responsibilities:

- derive a stable per-user pipe name from a caller-supplied instance key plus hashed Windows user SID;
- attempt first-instance server creation;
- expose `IsPrimary`;
- primary: listen asynchronously for bounded activation commands;
- secondary: connect with a short timeout and send `activate`;
- cancellation/disposal closes listeners cleanly;
- no filesystem, Git, command, tunnel or MCP authority.

Production instance key:

`FileMCP.Desktop.v1`

Tests use unique GUID keys.

## Protocol

Message: UTF-8 `activate\n`.

Bounds:

- maximum command bytes: 64;
- secondary connect timeout: 2 seconds;
- listener reads only one bounded command per connection;
- unknown command: ignored;
- malformed/oversized command: ignored and connection closed.

The pipe carries no secrets and grants no new authority.

## App lifecycle

OnStartup:

```text
create coordinator
-> if secondary:
     signal primary
     Shutdown()
     return
-> create MainWindow
-> register activation callback
-> show window
```

Activation callback:

```text
Dispatcher.BeginInvoke
-> MainWindow.ActivateFromSecondaryLaunch()
-> Show()
-> normalize if minimized
-> Activate()
-> brief Topmost toggle
```

OnExit:

- dispose coordinator;
- stop activation listener;
- call base.OnExit.

ProfileLock stays unchanged for runtime safety.

## Race handling

- Pipe server is created before MainWindow, so simultaneous second launch can connect even before listener starts.
- Pipe backlog preserves the connection until the primary starts listening.
- Disposal/cancellation must not throw during app exit.
- If secondary cannot signal within timeout, it exits rather than starting another GUI instance.

## Tests

Core tests must prove:

- first coordinator is primary;
- second coordinator with same test key is secondary;
- secondary signal reaches primary callback;
- unknown message does not invoke activation;
- after primary disposal a new coordinator can become primary;
- repeated secondary activation signals are accepted sequentially;
- command read remains bounded.

Packaged app smoke must gain a duplicate-launch proof:

1. start first packaged FileMCP;
2. wait for WPF main window;
3. close it to tray;
4. start the same FileMCP.exe again;
5. second process exits promptly;
6. first process remains alive;
7. first window becomes visible/foreground-capable again.

## Acceptance

FPA-007 PASS requires:

- Core single-instance tests PASS;
- packaged x64 duplicate-launch smoke PASS;
- existing tray smoke PASS;
- Windows Release build warnings-as-errors PASS;
- Windows runtime suite PASS;
- native Windows ARM64 packaged smoke remains PASS in CI;
- no profile-lock/tunnel behavior regression;
- GitHub verify-macos, verify-windows and verify-windows-arm64 all SUCCESS.

## Non-goals

- cross-machine activation;
- command-line file opening;
- forwarding arbitrary arguments;
- authentication through the pipe;
- replacing runtime ProfileLock.

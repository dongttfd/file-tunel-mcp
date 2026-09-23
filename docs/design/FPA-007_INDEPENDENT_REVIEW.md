# FPA-007 - Independent Multi-Round Review

Date: 2026-09-22

## Round 1 - Product topology

FileMCP already manages four workspace runtimes in one desktop process.

Decision: multiple GUI processes are not a required feature.

## Round 2 - Mutex-only option

A mutex would block duplicates but a second launch would appear to do nothing when the first app is hidden in tray.

Decision: rejection-only UX is insufficient.

## Round 3 - Mutex + named event

Works, but uses two kernel objects and mutex ownership is thread-affine.

Decision: valid but unnecessarily complex.

## Round 4 - Named pipe first-instance

One primitive supplies exclusivity plus activation messaging and is directly testable in one process.

Decision: select named pipe.

## Round 5 - Security

Activation must not become an authority channel.

Decision: accept exactly one bounded non-sensitive command, `activate`; no args, paths, commands or credentials.

## Round 6 - User isolation

Machine-global pipe naming could block different users.

Decision: hash current Windows SID into pipe name; never expose raw SID.

## Round 7 - Startup race

Second process can arrive before MainWindow/listener.

Decision: create pipe server before MainWindow. Client connect/backlog is allowed to wait briefly; listener starts immediately after window construction.

## Round 8 - Crash/recovery

A crashed process must not permanently block FileMCP.

Decision: rely on OS pipe-handle teardown; verify a new coordinator can become primary after disposal.

## Round 9 - Defense in depth

Desktop singleton is UX/operability, not tunnel security.

Decision: keep per-profile runtime locks exactly as they are.

## Final review

Proceed. Named-pipe first-instance activation is the smallest architecture that solves the observed multi-process failure mode without weakening runtime security.

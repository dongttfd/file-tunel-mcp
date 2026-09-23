# FPA-003 - macOS Tunnel Supervisor Parity - Frozen Design

Date: 2026-09-22
Status: FROZEN BEFORE IMPLEMENTATION

## 1. Problem

Windows FileMCP already supervises unexpected tunnel-client exits with bounded exponential backoff, jitter, restart budget, cooldown and stable-run reset.

The macOS baseline currently stops the local MCP server, releases the profile lock and transitions to failed/stopped whenever tunnel-client exits.

This creates a real reliability parity gap.

## 2. Selected architecture

Port the **tunnel-process supervisor pattern**, not arbitrary request retry.

On macOS:

- local MCP server remains running across unexpected tunnel-client exits;
- profile lock remains held;
- only tunnel-client is restarted;
- init/doctor are not rerun for every child restart;
- launch context is captured after successful init/doctor;
- stop/shutdown cancels pending restart immediately;
- cleanup invalidates stale process callbacks with a generation counter;
- no MCP tool call is replayed.

## 3. State model

Extend `LocalMCPRuntimeState`:

- `stopped`
- `starting`
- `running`
- `restarting(String)`
- `cooldown(String)`
- `stopping`
- `failed(String)`

UI semantics:

- starting: disabled Connecting button;
- running: enabled Disconnect;
- restarting/cooldown: enabled Disconnect so user can cancel recovery;
- stopping: disabled Disconnecting;
- stopped/failed: enabled Connect.

Only `failed` triggers modal error behavior.

## 4. Supervisor policy

Create `macos/TunnelSupervisor.swift`.

Defaults mirror Windows:

- initial backoff: 1s;
- max backoff: 30s;
- restart window: 5 min;
- max restarts in window: 5;
- stable-run reset: 2 min;
- jitter ratio: 20%.

Policy decision:

- if previous process ran at least stable-run-reset duration, reset history;
- remove attempts outside restart window;
- if budget exhausted, return cooldown until oldest attempt leaves window;
- otherwise return exponentially increasing bounded delay with jitter;
- add one restart attempt to the window.

Cooldown completion resets policy and starts a fresh backoff attempt.

## 5. Runtime lifecycle

Persist a launch context containing only already-approved runtime data:

- tunnel-client executable;
- run arguments;
- environment;
- sensitive values for redaction.

Initial start:

```text
validate
-> acquire profile lock
-> start local MCP server
-> init
-> doctor
-> capture run launch context
-> start tunnel process
-> running
```

Unexpected tunnel exit:

```text
generation check
-> dispose old child reference
-> record exit
-> if stop requested: finish stop
-> else policy.next(...)
-> restarting/cooldown
-> schedule bounded restart
-> start tunnel child only
-> running
```

Restart launch failure:

```text
policy.next(...)
-> backoff/cooldown
-> retry child launch only
```

Stop/shutdown:

- set requestedStop first;
- cancel pending restart work item;
- invalidate generation on final cleanup;
- stop child if present;
- stop MCP server only during final stop;
- release profile lock only during final stop;
- reset supervisor policy.

## 6. Race safety

Required:

- all runtime lifecycle mutation stays on existing serial runtime queue;
- each started tunnel gets an incrementing generation;
- onExit closure captures generation;
- stale exit callbacks are ignored;
- pending restart work is cancelable;
- restart work rechecks stop request + launch context before acting.

## 7. Security

No change to:

- workspace containment;
- local-auth token;
- tunnel environment allowlist;
- profile lock;
- command enablement;
- Git safe mode;
- API-key redaction.

Supervisor never retries an MCP request/tool call.

## 8. Tests

### Policy tests

Deterministically test:

- first delay;
- exponential increase;
- max delay clamp;
- jitter bounds;
- restart-window budget;
- cooldown resume time;
- stable-run reset;
- reset clears counters;
- 10k decisions stay bounded.

### Runtime tests

Using fake tunnel-client:

- first unexpected child crash automatically restarts;
- local MCP server/profile ownership remain usable during recovery;
- restart succeeds and state returns running;
- stop during pending restart cancels it;
- no child relaunch after stop;
- stale generation callback cannot tear down a newer child;
- repeated launch failures eventually produce cooldown (policy + runtime where practical);
- normal manual stop does not schedule restart.

### UI tests/static compile

All state switches must be exhaustive with restarting/cooldown.

## 9. Build wiring

Add `macos/TunnelSupervisor.swift` to:

- build_macos_app.sh;
- run_macos_dev.sh;
- GitHub Swift typecheck;
- runtime integration Swift compile list.

## 10. Acceptance

FPA-003 PASS requires:

- frozen policy tests PASS on real macOS;
- runtime crash/restart/cancel tests PASS;
- full `tests/test_swift_runtime.sh` PASS;
- warnings-as-errors macOS typecheck PASS;
- `build_macos_app.sh` PASS;
- Windows Release build + 414 assertions remain PASS;
- native GitHub verify-macos + verify-windows SUCCESS;
- no permission/security regression.

## 11. Non-goals

- distributed supervisor state;
- persistent restart counters;
- MCP request retry/replay;
- changing tunnel-client itself;
- FPA-004 signing/notarization.

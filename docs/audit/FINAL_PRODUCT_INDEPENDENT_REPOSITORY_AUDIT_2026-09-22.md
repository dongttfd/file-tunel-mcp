# FINAL PRODUCT INDEPENDENT REPOSITORY AUDIT  -  2026-09-22

Repo: `D:\Tools\FileMCP`
Branch audited: `chatgpt/OBS-001-observability-foundation`
Audited HEAD: `b48f6afdcb52479ad4dc251cf1dac6dd7850731e`
Upstream main observed during audit: `3a41ba526d593aacdcc58e48105268427455712b`

## 1. Executive conclusion

**DO NOT close the whole application as "APP RELEASE READY" yet.**

The Windows implementation is in strong condition: the current HEAD builds cleanly with warnings-as-errors, the full Windows runtime suite passes 414 assertions, the live ChatGPT logical-chat proof is complete, NuGet reports no vulnerable packages and no direct updates, and no production secret-like token was found in tracked source outside test/evidence/vendor exclusions.

However, the independent whole-repository audit found several issues that are outside the already-passed OBS/V11 core tests:

- one **confirmed Windows CI/release gate bug**;
- two **confirmed macOS/Windows product-parity gaps** against the repo's current cross-platform claim;
- one **production-distribution signing/notarization gap**;
- one **Windows ARM64 release-assurance gap**;
- stale project-state documents that violate the handoff/state law;
- additional P2 hardening/operability items.

There is **no P0 security bypass or Windows core corruption found** in this audit. The highest-confidence remaining problems are release integration and cross-platform parity rather than the Windows observability core.

Recommended decision:

```text
OBS-013                     PASS
V11-001..V11-008            PASS
Windows core regression     PASS
Whole-app final audit       FOUND BLOCKERS
V11-009 / APP RELEASE READY DO NOT CLOSE YET
```

Fix the P1 items below (or explicitly narrow the product scope and update the claims), rerun the final gates, then finish V11-009.

---

## 2. Independent audit method

The repo was reviewed in separate rounds so one subsystem's assumptions would not be allowed to validate itself.

### Round A  -  repository / dependency inventory

Checked:

- Git truth: branch, HEAD, upstream main and clean worktree;
- C# / PowerShell inventory;
- TODO/FIXME/HACK/NotImplemented surface;
- NuGet direct and transitive dependencies;
- vulnerability and outdated-package status.

Result:

- 43 C# files, about 11.6k C# LOC in the Windows tree at audit time;
- no unresolved TODO/FIXME/HACK/NotImplemented marker in production source;
- direct production packages are intentionally small:
  - `Microsoft.Data.Sqlite 10.0.12`;
  - `OpenTelemetry 1.19.1`;
  - `OpenTelemetry.Exporter.OpenTelemetryProtocol 1.19.1`;
- current audit: no vulnerable NuGet packages reported;
- current audit: no direct package updates reported.

### Round B  -  OSS source-reuse / replacement audit

The 11 previously audited OSS repositories still exist under `D:\FileMCP-OSS-Audit`.

Freshness recheck:

| Repo | Audited checkout | Freshness at 2026-09-22 | FileMCP decision |
|---|---:|---:|---|
| IBM ContextForge | `1950bcb` | at origin HEAD | patterns selectively adapted |
| LiveCharts2 | `8537b90` | at origin HEAD | deliberately not adopted for V1 |
| Lunar | `d22d2b8` | 2 commits behind origin | resilience ideas audited; full gateway rejected |
| Microsoft MCP Gateway | `3594c4e` | at origin HEAD | health/gateway patterns selectively adapted |
| MCP Inspector | `2e90a62` | at origin HEAD | protocol/testing reference, not runtime dependency |
| joshrotenberg/mcp-proxy | `965ca25` | at origin HEAD | transport/proxy reference |
| OpenTelemetry .NET | `44d841f` | at origin HEAD | **official packages integrated directly** |
| prometheus-net | `60e9106` | at origin HEAD | deliberately deferred/rejected for V1 |
| ScottPlot | `fb3f5aa` | at origin HEAD | deliberately not adopted for V1 |
| soth-ai/mcp-proxy | `b198e9d` | at origin HEAD | retry/resilience patterns selectively adapted |
| ToolHive | `3c2a235` | at origin HEAD | health/lifecycle patterns selectively adapted |

Important interpretation:

**FileMCP did not and should not copy all of these gateways wholesale.** The selected architecture uses direct official packages where that is the strongest fit and adapts proven patterns where importing a whole gateway would add irrelevant complexity.

Verified Windows integrations include:

- official OpenTelemetry `ActivitySource` + `Meter`;
- official OTLP HTTP/Protobuf exporter;
- bounded logical-chat correlation;
- bounded logical-session lifecycle and persistence;
- automatic retention maintenance;
- restart backoff/jitter/budget/cooldown for the tunnel process;
- component-health model;
- W3C trace-context extraction with bounded metadata;
- privacy/cardinality guards.

Deliberately excluded by frozen design:

- retrying arbitrary MCP tool calls;
- Prometheus server;
- full ContextForge/ToolHive/Microsoft gateway transplant;
- distributed session store;
- payload tracing;
- LiveCharts2/ScottPlot migration;
- treating transport session IDs as exact ChatGPT chat identity.

**Conclusion for the user's OSS question:** the selected strong OSS ideas are genuinely integrated on the Windows V1.1 path. They are not fully integrated across the entire cross-platform repo; macOS is materially behind Windows in the new observability/resilience capabilities.

### Round C  -  filesystem and Git security boundary

Reviewed:

- `SafePathResolver.cs`;
- file read/write/delete/search paths;
- reparse/junction handling;
- Git worktree/common/object directory containment;
- alternates/config include handling;
- Git safe-mode environment/config;
- hooks, filters, external diff, GPG and transport restrictions.

No new P0/P1 boundary bypass was found.

Notable strengths:

- existing paths are canonicalized on Windows using `CreateFileW` + `GetFinalPathNameByHandleW`;
- rooted/UNC inputs are rejected where a relative path is required;
- shared root deletion is refused;
- Git worktree/Git/common/object directories must stay inside the shared root;
- repository config includes and unsafe alternates are guarded;
- safe mode disables hooks, external diff/textconv, signing and file transport;
- repository-controlled credential helpers / sensitive HTTP file settings are rejected in safe mode.

The app explicitly documents that same-OS-user processes are inside the local trust boundary, so theoretical same-user TOCTOU attacks were not promoted to release blockers.

### Round D  -  process execution and lifecycle

Reviewed:

- `ProcessRunner.cs`;
- Windows Job Object lifecycle;
- bounded process output;
- timeout/cancellation;
- tunnel child process ownership.

No new P0/P1 process-lifecycle bug was found.

Strengths:

- NUL validation on executable/arguments/environment;
- child processes are assigned to a Windows Job Object with kill-on-close;
- process-tree kill fallback exists;
- output is byte-bounded;
- command timeout is bounded;
- tunnel restart is limited to the tunnel process and never replays arbitrary MCP tool calls.

### Round E  -  HTTP/MCP parser and local authentication

Reviewed:

- loopback listener;
- Host / Origin policy;
- Content-Length framing;
- duplicate critical headers;
- Transfer-Encoding;
- local-auth token;
- modern/legacy MCP negotiation.

No authentication bypass was found.

Strengths:

- binds to `IPAddress.Loopback`;
- local token must be at least 32 UTF-8 bytes;
- invalid Host/Origin rejected;
- duplicate single-value security/protocol headers rejected;
- Transfer-Encoding unsupported/rejected;
- body size bounded;
- Content-Length/body length must match exactly;
- local auth is checked before an authenticated request body is accepted;
- only bodyless GET OAuth protected-resource discovery paths bypass local auth, and those paths intentionally return 404 without workspace data.

### Round F  -  observability/privacy/retention

Reviewed:

- chat correlation;
- session registry;
- SQLite persistence;
- retention worker;
- standard telemetry;
- trace context;
- OTLP exporter;
- health tracking.

No new raw-content/privacy leak was found.

Previously proven live:

- real ChatGPT connector exposes `filemcp_observability_connect`;
- same handle resumed across separate user turns;
- normal tool calls stayed bound to the same durable SHA-256 session;
- raw handle absent from SQLite/WAL/SHM.

Current design also correctly avoids persisting:

- raw prompt/chat text;
- file contents;
- tool arguments/results;
- command text;
- credentials.

### Round G  -  period/timezone correctness

Reviewed:

- Today / Yesterday / 7d / 30d period resolver;
- sub-hour timezone offsets;
- DST fallback;
- SQLite exact minute query/retention.

Existing tests cover:

- UTC+05:45 boundary;
- 25-hour DST fallback day;
- workspace/global period aggregation;
- rejection of unrepresentable non-minute-aligned starts.

No new period bug found.

### Round H  -  platform parity

This round found confirmed gaps. See FPA-002 and FPA-003 below.

### Round I  -  CI/package/release pipeline

This round found a confirmed clean-run CI failure. See FPA-001.

### Round J  -  market distribution / operational readiness

This round found release-readiness gaps around signing/notarization, ARM64 smoke assurance, stale state, and duplicate desktop instances.

---

# 3. Confirmed findings

## FPA-001  -  P1  -  Windows GitHub Actions release-resource verification uses the obsolete staging directory

**Category:** confirmed CI/release bug
**Must fix before final release:** YES

### Evidence

Current build script:

- `build_windows_app.ps1:6` defaults `StagingName = "FileMCP-release"`.
- publish output is `dist/windows-$Architecture/$StagingName`.
- README correctly documents:
  - `dist/windows-x64/FileMCP-release/`
  - `dist/windows-arm64/FileMCP-release/`

But CI still checks:

- `.github/workflows/verify.yml:118`
- `$root = "dist/windows-$arch/FileMCP"`

On a clean GitHub Actions worker, the build creates `FileMCP-release`, while the resource-verification step looks for `FileMCP`.

### Impact

The Windows CI pipeline can fail after successful build/package simply because it inspects a directory that the current build script no longer creates.

This also means a green local package test does not prove the checked-in GitHub workflow is internally consistent.

### Root cause

The release staging directory was changed/hardened during OBS-013/V11-009, but the downstream workflow verification path was not migrated with it.

### Required fix

Change the workflow to use the same canonical staging name as the build script.

Preferred approach:

1. expose one canonical staging convention;
2. call:
   `./build_windows_app.ps1 -Architecture <arch> -StagingName FileMCP-release`;
3. verify `dist/windows-$arch/FileMCP-release`;
4. add a CI assertion that the obsolete `FileMCP` path is not being relied upon.

### Acceptance

- clean GitHub runner;
- x64 package build PASS;
- ARM64 package build PASS;
- release-resource verification PASS for both;
- x64 app smoke PASS;
- artifact uploads PASS.

Suggested task: **FPA-001 / CI-STAGING-PARITY**.

---

## FPA-002  -  P1  -  README claims equivalent MCP surface, but macOS is missing the Windows observability-connect tool

**Category:** confirmed cross-platform product/API parity bug
**Must fix before claiming whole-app parity:** YES

### Evidence

README currently states:

> Equivalent MCP surface on both platforms: the same filesystem, Git, protocol, tunnel, and optional command-execution behavior.

Programmatic source comparison during this audit:

```text
WINDOWS_COUNT 19
MACOS_COUNT   18

WINDOWS_ONLY:
filemcp_observability_connect
```

Windows:

- `windows/src/FileMCP.Core/LocalMcpServer.cs` defines `filemcp_observability_connect`;
- operational tools gain the reserved `_filemcp_chat` correlation facade.

macOS:

- `macos/LocalMCPServer.swift` exposes the legacy/common 18-tool surface;
- no `filemcp_observability_connect`;
- no equivalent cross-turn correlation facade/instructions.

### Impact

The public MCP capability surface is no longer equivalent across supported platforms.

A ChatGPT app refreshed against Windows can see 19 tools and use exact AI-chat correlation, while the macOS server cannot provide that behavior.

### Root cause

Observability V1/V1.1 was implemented on the Windows core after the original Windows/macOS parity claim was written. The macOS server was not upgraded with the new application-level logical-chat correlation contract.

### Required resolution  -  choose one

**Option A  -  preferred if FileMCP remains cross-platform:** port the logical-chat correlation facade and its privacy/bounds semantics to macOS.

Minimum parity:

- `filemcp_observability_connect`;
- opaque 32-byte random handle format;
- bounded/TTL registry;
- `_filemcp_chat` reserved facade argument;
- facade strips correlation metadata before strict tool validation;
- raw handle never persisted/logged;
- cross-turn tests;
- discover instructions updated.

**Option B  -  only if product scope intentionally changes:** explicitly document Observability/AI-chat correlation as Windows-only and remove/qualify the blanket "Equivalent MCP surface" claim.

### Acceptance

- Windows and macOS advertised tool-name sets match, or docs explicitly describe the intentional delta;
- macOS modern `server/discover` accurately advertises correlation behavior;
- macOS live/unit tests cover create/resume/unknown/expired handle semantics;
- no raw handle persistence.

Suggested task: **FPA-002 / MAC-LOGICAL-CHAT-PARITY**.

---

## FPA-003  -  P1  -  macOS tunnel crash behavior lacks the Windows restart/backoff/cooldown supervisor

**Category:** confirmed runtime parity/reliability gap
**Must fix before claiming same tunnel behavior:** YES

### Evidence

Windows:

- `TunnelSupervisor.cs` implements exponential backoff, jitter, restart window/budget, stable-run reset and cooldown;
- `LocalMcpRuntime.cs` restarts only the tunnel child and keeps the local MCP server lifecycle controlled;
- V11 hardening includes restart-storm tests.

macOS:

- `macos/LocalMCPRuntime.swift:285+` `tunnelDidExit(exitCode:)`;
- on unexpected non-zero exit it:
  - clears tunnel process;
  - stops the local MCP server;
  - releases the profile lock;
  - sets runtime to `.failed`;
- there is no automatic retry/backoff/cooldown supervisor.

The Swift test labelled "first restart" is a manual `first.start(...)` after a previous stop. It is not a crash-restart supervisor test.

### Impact

A transient tunnel-client crash recovers automatically on Windows but leaves macOS failed until user intervention.

That contradicts the current whole-product "same ... tunnel behavior" expectation.

### Root cause

V11-003/V11-004 resilience work was Windows-only.

### Required fix

Port the bounded supervisor concept, not arbitrary tool retry.

Required properties:

- restart only `tunnel-client`;
- exponential backoff + jitter;
- bounded attempts per window;
- cooldown after budget exhaustion;
- stable-run reset;
- Stop/Shutdown immediately cancels a pending restart;
- no MCP tool replay;
- preserve profile/local-server security semantics;
- expose enough runtime status for macOS UI/logging.

### Acceptance

Swift tests must cover:

- real/fake tunnel child crash;
- automatic restart succeeds;
- repeated launch failures enter bounded cooldown;
- stop cancels pending restart;
- no runaway loop;
- stable run resets budget.

Suggested task: **FPA-003 / MAC-TUNNEL-SUPERVISOR-PARITY**.

---

## FPA-004  -  P1 for public-market release  -  production signing/notarization pipeline is not implemented

**Category:** release-readiness gap, not a Windows core logic bug
**Must fix before public commercial distribution:** YES

### Evidence

README explicitly states:

- macOS local build is unsigned; distribution builds should be code-signed and notarized;
- Windows local builds are unsigned; production distribution should Authenticode-sign the executable/package.

Repo audit found no production signing/notarization workflow or release script.

### Impact

For public distribution:

- Windows users can receive SmartScreen/untrusted publisher friction;
- macOS Gatekeeper can block or strongly warn on an unsigned/unnotarized app;
- release provenance is weaker than the source/package testing quality already present.

### Required fix

Do not commit signing secrets.

Create a release-only signing design:

Windows:

- Authenticode signing for `FileMCP.exe`;
- optional signed installer/package if one is introduced;
- CI verifies signature after signing.

macOS:

- Developer ID Application signing;
- hardened runtime as appropriate;
- notarization;
- stapling;
- post-package Gatekeeper verification.

Use CI secret storage / release environment, not repo files.

### Acceptance

- unsigned developer builds remain possible;
- production release job produces verifiably signed artifacts;
- no signing credential enters logs/artifacts/source;
- signature/notarization verification is automated.

Suggested task: **FPA-004 / PRODUCTION-SIGNING-GATE**.

---

## FPA-005  -  P2  -  Windows ARM64 package is advertised and built, but the app smoke test only executes x64

**Category:** release assurance gap
**Recommended before claiming Windows ARM64 production-ready:** YES

### Evidence

README advertises:

- Windows x64;
- Windows ARM64.

CI builds both.

But:

- `tests/test_windows_app.ps1:20` hardcodes `dist/FileMCP-v0.4.0-windows-x64.zip`;
- the app smoke test therefore executes only x64;
- ARM64 receives package/resource checks and artifact upload, not equivalent executable startup/SQLite/OTLP/tray smoke.

### Impact

A packaging/runtime regression specific to ARM64 can pass CI.

### Required fix

Best:

- add a Windows ARM64 runner and run the same app smoke.

Fallback if a native ARM64 runner is unavailable:

- add PE/RID architecture verification;
- inspect package contents/legal files;
- run architecture-appropriate self-contained publish validation;
- clearly document that runtime smoke remains unverified until native ARM64 CI is available.

Suggested task: **FPA-005 / WIN-ARM64-RUNTIME-GATE**.

---

## FPA-006  -  P2  -  project state/handoff documents are stale and internally contradictory

**Category:** workflow/state correctness bug
**Must fix before MAIN VERIFIED under the user's project law:** YES

### Evidence

Git truth at audit:

```text
branch: chatgpt/OBS-001-observability-foundation
HEAD:   b48f6afdcb52479ad4dc251cf1dac6dd7850731e
```

But:

- `CURRENT_HANDOFF.md` still contains `HEAD: 3033084`;
- `PROJECT_STATE.md` still contains `Current branch HEAD: 3033084`;
- old historical `NEXT_EXACT_ACTION` blocks remain alongside the current merge blocker;
- the top status text is not fully normalized to the latest GitHub permission state.

### Impact

A new chat following the bootstrap law can resume from obsolete instructions or report the wrong commit as verified.

### Required fix

Normalize state files to one current truth section and move historical gates into an explicitly historical section.

Required current truth:

- current branch;
- current HEAD;
- origin/main;
- OBS-013 PASS;
- V11-009 status;
- current whole-app audit blockers;
- exactly one `NEXT_EXACT_ACTION`.

Suggested task: **FPA-006 / STATE-NORMALIZATION**.

---

## FPA-007  -  P2  -  desktop app does not enforce one app instance; profile locking occurs only when a runtime connects

**Category:** operability/UX issue
**Evidence from real use:** YES

### Evidence

The Windows App startup creates `MainWindow` directly and has no application-wide single-instance mutex.

The only mutex is `ProfileLock` in `LocalMcpRuntime`, so multiple FileMCP desktop processes can coexist until they try to use the same tunnel profile.

During the real acceptance workflow on this machine, eight FileMCP processes from three build directories were found simultaneously, while only one owned port 8008/tunnel-client. This caused confusing "same connection error" behavior.

### Impact

Users can launch multiple tray apps and lose track of which build/profile owns the port/tunnel.

Security is still protected by profile/port checks, so this is not a P0/P1 boundary failure, but it is a real product-operability problem.

### Recommended fix

If multiple GUI instances are not an intentional supported mode:

- add an app-wide per-user single-instance mutex;
- second launch should focus the existing window and exit;
- retain per-profile runtime lock as defense-in-depth.

If multiple GUI instances are intentionally supported, display the active executable path/PID/profile ownership clearly and document the behavior.

Suggested task: **FPA-007 / SINGLE-INSTANCE-UX**.

---

## FPA-008  -  P2 hardening  -  Local MCP TCP server has request-size bounds but no per-connection idle timeout or connection cap

**Category:** defense-in-depth / resource exhaustion risk
**Status:** code-confirmed missing guard, exploitability not demonstrated in this audit

### Evidence

`LocalMcpServer.AcceptLoopAsync` accepts clients continuously and starts one task per client.

`HandleClientAsync`:

- has request header/body byte limits;
- but `ReadAsync` uses only the server-lifetime cancellation token;
- no per-connection header/read idle deadline;
- no explicit maximum concurrent client semaphore.

### Impact

A large number of slow/incomplete connections can keep sockets/tasks alive.

The SECURITY model places same-user local processes inside the trust boundary, which reduces severity. The practical remote exposure also depends on tunnel-client/control-plane connection behavior. Therefore this is P2 hardening, not a confirmed remote vulnerability.

### Recommended fix

- bounded concurrent connection semaphore;
- bounded header/body read idle timeout;
- metrics for rejected/expired connections;
- tests for many slow/incomplete clients;
- preserve current request-size and auth semantics.

Suggested task: **FPA-008 / HTTP-CONNECTION-BOUNDS**.

---

## FPA-009  -  P3 / accepted limitation unless stronger health is required  -  dynamic health port `:0` cannot be independently probed by FileMCP

**Category:** observability limitation, already acknowledged by design

### Evidence

Default:

`127.0.0.1:0`

Port 0 asks tunnel-client for an ephemeral health/admin port.

Windows `TryParseProbeEndpoint` requires a resolved port > 0. Therefore the dashboard reports:

`dynamic/not probed`

The current evidence/design explicitly says fixed endpoints are probed and dynamic `:0` is `NotConfigured` rather than guessed.

### Impact

Default configuration knows tunnel process state but does not independently TCP-probe the tunnel health/admin listener.

### Optional improvement

Use tunnel-client's resolved health URL file mechanism (if stable/current in the vendored client) so FileMCP can discover and probe the actual ephemeral endpoint without exposing a public listener.

This is not required to fix before V11-009 if the documented limitation is accepted.

Suggested task: **FPA-009 / DYNAMIC-HEALTH-DISCOVERY**  -  optional.

---

# 4. What was NOT found

The independent rounds did **not** find evidence of:

- a shared-root escape in the reviewed Windows path resolver;
- a Git safe-mode metadata escape in the reviewed controls;
- raw ChatGPT/file/tool/command content being deliberately stored by observability;
- raw logical-chat handle persistence;
- OTLP endpoint embedded-credential support;
- unbounded logical chat/session cardinality after V11-008;
- arbitrary MCP tool retry/replay;
- an HTTP local-auth bypass;
- a current NuGet vulnerability;
- a Windows build warning/error;
- a failing Windows core test in the current HEAD.

Current Windows audit evidence:

```text
dotnet build -c Release -warnaserror
PASS  -  0 warnings / 0 errors

tests/test_windows_runtime.ps1
PASS  -  414 assertions

NuGet vulnerable audit
PASS  -  no vulnerable packages reported

NuGet direct outdated audit
PASS  -  no direct updates reported

tracked production secret-like scan
PASS  -  no match under the audit exclusions
```

---

# 5. Fix order / dependency-aware queue

Recommended order before final whole-app closure:

```text
FPA-001 CI staging parity -------------------------+
                                                   |
FPA-006 state normalization -----------------------+--> FINAL CLEAN CI
                                                   |
FPA-002 mac logical-chat parity --> FPA-003 mac tunnel parity
          |                           |
          +---------------------------+--> CROSS-PLATFORM PARITY GATE

FPA-005 Windows ARM64 runtime assurance -----------> PLATFORM RELEASE GATE

FPA-004 production signing/notarization -----------> MARKET RELEASE GATE

FPA-007 single-instance UX ------------------------+
FPA-008 HTTP connection bounds --------------------+--> HARDENING GATE
FPA-009 dynamic health discovery (optional) -------+
```

### Minimum required before resuming V11-009 as "whole app final"

If the intended product remains **Windows + macOS with equivalent behavior**, fix:

1. FPA-001;
2. FPA-002;
3. FPA-003;
4. FPA-006.

Then rerun both platform CI gates.

For a **public market release**, additionally complete FPA-004 and obtain reasonable ARM64 assurance under FPA-005.

FPA-007/FPA-008 should be fixed before calling the product industrial/24x7 hardened, although they do not invalidate the already-proven Windows OBS-013 logic.

If the user intentionally narrows the next release to **Windows only**, FPA-002/FPA-003 may be converted from implementation blockers into explicit scope/documentation work, but the README and product claims must be changed before release.

---

# 6. Required final acceptance after fixes

Do not mark the application release-ready based only on the old 414-test gate.

After the selected fixes:

## Windows

- `dotnet build windows/FileMCP.Windows.sln -c Release -warnaserror`;
- full Windows runtime suite;
- x64 package + app smoke;
- ARM64 gate appropriate to the available hardware;
- exact GitHub workflow clean-run verification;
- vulnerability/outdated audit;
- package legal-resource verification;
- live connector smoke if MCP schema changes.

## macOS

If parity is retained:

- Swift warnings-as-errors typecheck;
- Swift runtime integration suite;
- logical-chat create/resume/expiry/cap tests;
- tunnel crash/restart/backoff/cooldown tests;
- app build;
- package legal resources;
- signed/notarized production artifact gate for public distribution.

## Cross-platform contract

Automate a canonical tool-surface parity test so a future Windows-only tool addition cannot silently break the README claim again.

## State

Before final merge:

- `CURRENT_HANDOFF.md`, `PROJECT_STATE.md` and task queue all point to the same HEAD/state;
- exactly one next action;
- no historical blocker presented as current.

---

# 7. Final audit decision

The repo is **not bug-free / not fully market-ready yet**.

The strongest statement supported by current evidence is:

```text
Windows FileMCP core + Observability V1/V1.1:
STRONG / VERIFIED

Selected OSS integration on Windows:
REAL and technically justified

Whole-repository cross-platform parity:
NOT COMPLETE

Clean CI release integration:
HAS A CONFIRMED PATH BUG

Public-market distribution:
SIGNING / NOTARIZATION NOT YET IMPLEMENTED

V11-009:
DO NOT USE AS THE ONLY REMAINING APP-WIDE GATE
```

The first fix should be **FPA-001**, because it is deterministic, small, and can break the current Windows GitHub Actions release gate even though all local Windows tests pass.

After that, decide whether FileMCP's next release is genuinely cross-platform. If yes, FPA-002/FPA-003 are mandatory implementation work. If Windows-only, explicitly freeze that scope and correct the cross-platform claims before final V11-009 closure.

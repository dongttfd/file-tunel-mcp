# FPA-002 - macOS Logical-Chat / MCP Tool-Surface Parity - Frozen Design

Date: 2026-09-22
Status: FROZEN BEFORE ACCEPTING IMPLEMENTATION

## 1. Problem

FileMCP publicly supports macOS and Windows and README currently claims an equivalent MCP surface.

Windows exposes 19 tools, including:

`filemcp_observability_connect`

and augments normal tool input schemas with reserved optional metadata:

`_filemcp_chat`

The audited macOS baseline exposed 18 tools and did not advertise this facade.

During the FPA-002 design review, an uncommitted concurrent draft appeared in the worktree. It already introduced:

- a macOS `LogicalChatCorrelationService`;
- `filemcp_observability_connect` handling;
- `_filemcp_chat` schema facade/stripping;
- updated modern discover instructions.

That draft is not accepted as implementation until this frozen design is satisfied and all tests/build wiring pass.

## 2. Scope

FPA-002 restores the **logical-chat facade contract and MCP tool-surface parity** on macOS.

It does NOT port the Windows-only observability database/dashboard/session persistence stack in this task.

Required macOS semantics:

- advertise `filemcp_observability_connect`;
- create a cryptographically random opaque handle;
- handle format exactly matches Windows:
  - prefix `chat_`;
  - 32 random bytes;
  - unpadded base64url payload;
  - total payload length 43 chars after prefix;
- resume known valid handles;
- reject malformed handles;
- reject unknown handles;
- bounded known-handle cardinality;
- bounded retention / expiry;
- least-recently-seen pressure eviction;
- hash raw handles internally using SHA-256;
- add optional `_filemcp_chat` to normal tool schemas;
- remove `_filemcp_chat` before strict tool argument validation;
- invalid/unknown correlation metadata on a normal tool call must degrade to unbound/no-correlation behavior and MUST NOT increase authority or fail an otherwise-valid tool call;
- correlation handle never changes filesystem/Git/command permissions;
- no raw handle logging;
- server/discover instructions describe the facade accurately.

## 3. Explicit non-goals

FPA-002 does not add:

- SQLite session persistence on macOS;
- macOS dashboard/UI session views;
- OTLP exporter on macOS;
- tunnel restart/backoff/cooldown (FPA-003);
- exact durable AI-chat counts on macOS;
- cross-process handle restoration.

The facade is process-local metadata continuity only.

## 4. Concurrency and bounds

Use a dedicated lock around the in-memory correlation map.

Defaults must match Windows:

- max handles: 4096;
- retention: 2 hours;
- random bytes: 32.

Cleanup behavior:

- cleanup runs opportunistically on connect/resolve;
- explicit cleanup/snapshot API remains testable;
- no unbounded growth;
- pressure creates room for one new handle;
- oldest last-seen entries are removed first.

## 5. Security and privacy

The handle is not authentication.

Requirements:

- use Security framework CSPRNG (`SecRandomCopyBytes`);
- use CryptoKit SHA-256 for internal hash keys;
- store only the SHA-256 hash in the registry;
- return raw handle only to the caller that requested it;
- never write raw handle to logs/files/settings;
- stripping `_filemcp_chat` occurs before the existing LocalTools/CodexSkillRegistry argument validation;
- command/file/Git policy remains unchanged.

## 6. Tool schema parity

`allToolDefinitions()` on macOS must:

1. augment every normal FileMCP/Codex tool input schema with optional `_filemcp_chat: string`;
2. append one `filemcp_observability_connect` definition;
3. preserve existing tool annotations/output schemas;
4. expose 19 tools when `run_command` is enabled and 18 when command execution is disabled in the same relative way as Windows.

The dedicated connect tool must NOT accept `_filemcp_chat`; only `chat_instance_id`.

## 7. Build/test wiring

New source file `macos/LogicalChatCorrelation.swift` must be included in every relevant Swift compilation path:

- `build_macos_app.sh`;
- `run_macos_dev.sh`;
- GitHub macOS static typecheck;
- runtime-test compilation in `tests/test_swift_runtime.sh`;
- server-test compilation in `tests/test_swift_runtime.sh`;
- any dedicated correlation unit executable added by the test script.

## 8. Required tests

### Unit-style correlation service tests

Cover:

- generated handle format;
- create returns `resumed=false`;
- resume returns `resumed=true`;
- invalid handle rejection;
- unknown handle rejection;
- resolve returns hash for known handle;
- malformed/unknown resolve returns nil;
- TTL expiry;
- max-cap pressure;
- retention counters;
- deterministic injected clock/random provider.

### MCP integration tests

Cover both legacy and modern where relevant:

- tools/list contains `filemcp_observability_connect`;
- normal tool input schema exposes `_filemcp_chat`;
- connect returns structured handle + resumed flag;
- resuming same handle succeeds;
- connect rejects unknown extra arguments;
- normal read call with valid `_filemcp_chat` succeeds;
- normal read call with unknown syntactically-valid handle still succeeds unbound;
- `_filemcp_chat` does not reach LocalTools strict validation;
- run_command exposure behavior remains unchanged when disabled;
- discover instructions mention the facade.

### Parity contract

Add an automated source/schema contract preventing future silent drift between Windows and macOS required public tool names.

## 9. Acceptance

FPA-002 PASS only when:

- Swift warnings-as-errors typecheck passes;
- full `tests/test_swift_runtime.sh` passes on macOS CI/native environment;
- `build_macos_app.sh` passes;
- tool-surface parity contract passes;
- no raw-handle logging/persistence is introduced;
- existing Windows build/runtime tests remain unchanged/pass;
- diff review confirms no permission/sandbox weakening.

## 10. Frozen decision

Preserve a cross-platform FileMCP product. Do not downgrade README claims to Windows-only as a shortcut.

Port the bounded logical-chat facade to macOS, keeping FPA-002 intentionally smaller than a full macOS Observability V1 port.

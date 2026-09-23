# OBS-013 Release / Final Acceptance Evidence

Date: 2026-09-22
Task: OBS-013 - self-contained win-x64 publish, native SQLite smoke, live ChatGPT correlation proof, final acceptance

## Automated release gates

- Release build: PASS, 0 warnings / 0 errors.
- Windows integration suite: PASS, 293 assertions.
- Self-contained win-x64 single-file publish: PASS.
- Release staging path: `dist/windows-x64/FileMCP-release/`.
- Archive: `dist/FileMCP-v0.4.0-windows-x64.zip`.
- Final SHA-256: `6B57D412CDB15325EDE50503E27713116BC4F86D0AC103313C94368FA0412BB2`.
- Packaged WPF startup: PASS.
- Close-to-tray behavior: PASS.
- Packaged tunnel-client version gate: PASS.
- Packaged native SQLite create/write/read: PASS (`tool_calls=1`, `read_calls=1`).
- SQLite file header from the packaged executable: PASS (`SQLite format 3`).
- NuGet vulnerable package audit: PASS, no vulnerable packages reported.
- Direct package outdated audit: PASS, no updates reported.

## Release contents

The x64 archive contains only:

- `FileMCP.exe`
- `FileMCP-LICENSE.txt`
- `tunnel-client.exe`
- `tunnel-client-LICENSE.txt`
- `tunnel-client-NOTICE.txt`
- `tunnel-client-THIRD-PARTY-LICENSES.txt`

The build pipeline now publishes into a dedicated `FileMCP-release` staging directory so a currently running production/bridge executable is not overwritten during packaging.

## Live ChatGPT correlation gate

Status: BLOCKED - not failed, not passed.

Reason:

- The ChatGPT conversation performing this release audit is still connected through the previously running FileMCP binary at `dist/windows-x64/FileMCP/FileMCP.exe`.
- Port `127.0.0.1:8008` is owned by that older bridge process.
- The ChatGPT-visible FileMCP tool registry in this conversation still exposes the legacy 18 tools and does not expose `filemcp_observability_connect` or `_filemcp_chat` facade metadata.
- Killing/replacing that process from inside the same conversation would intentionally sever the execution bridge used to complete the audit, and the current ChatGPT tool registry would still require a connector/schema refresh.

Therefore no local HTTP simulation is accepted as a substitute for the required live ChatGPT proof.

## Exact remaining acceptance action

1. Run/reconnect ChatGPT to the new packaged FileMCP build so the connector schema refreshes.
2. Confirm `filemcp_observability_connect` is visible to ChatGPT.
3. Call `filemcp_observability_connect` once and receive an opaque `chat_instance_id`.
4. Make normal FileMCP calls with that exact handle in `_filemcp_chat`.
5. Verify the FileMCP Overview shows one correlated observed context rather than unbound traffic.
6. Verify the durable SQLite store contains only the SHA-256 session hash, never the raw handle.
7. Only then enable/approve the exact AI-chat label and mark OBS-013 PASS.

## Result

All code, security, hardening, package, dependency, desktop, tunnel-client, and native SQLite gates are PASS. OBS-013 remains BLOCKED solely on the required live ChatGPT connector/schema correlation proof.

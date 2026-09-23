# FPA-009 - Dynamic Tunnel Health Discovery Evidence

Date: 2026-09-22
Status: PASS

Windows FileMCP now uses the vendored tunnel-client 0.0.12 official health URL-file mechanism for ephemeral health listeners.

Verified behavior:
- --health.url-file passed to tunnel-client run;
- stale URL file deleted before every launch/restart;
- URL file content bounded to 512 bytes and parsed as strict loopback-only HTTP;
- remote hosts, HTTPS, credentials, query, fragment, non-root path, missing port and oversized input rejected;
- dynamic 127.0.0.1:0 endpoint becomes Reachable through the resolved URL file;
- loss of the resolved endpoint becomes Unreachable while preserving last success;
- resolved endpoint cleared on exit/restart/stop;
- URL file removed on cleanup;
- fixed-port probing remains unchanged;
- UI reports dynamic/pending until discovery completes.

Local verification:
- Windows Release build: PASS, 0 warnings / 0 errors
- Windows runtime suite: PASS, 444 assertions
- dynamic-health-discovery-contract: PASS
- all existing cross-platform/static contracts: PASS

Native GitHub run 35748443822:
- verify-macos: SUCCESS
- verify-windows: SUCCESS
- verify-windows-arm64: SUCCESS

Result: FPA-009 PASS.

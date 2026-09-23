# FPA-009 - Dynamic Tunnel Health Discovery Candidate Evidence

Date: 2026-09-22
Status: CANDIDATE - NATIVE CI REQUIRED

## Mechanism

The vendored tunnel-client 0.0.12 officially supports --health.url-file and recommends it for ephemeral health listeners.

Windows FileMCP now:
- creates a controlled per-profile health URL file path;
- passes --health.url-file to tunnel-client run;
- deletes stale URL files before every launch/restart;
- validates resolved URL content as bounded loopback-only HTTP;
- dynamically resolves 127.0.0.1:0 / [::1]:0 health listeners;
- independently probes the resolved TCP endpoint;
- clears cached endpoints on child exit/restart/stop;
- deletes the URL file on cleanup;
- preserves fixed-port probing unchanged.

## Security

Resolved URL input is rejected unless it is an absolute loopback HTTP URL with an explicit port and no credentials, query, fragment or non-root path. File size is capped at 512 bytes and reparse points are rejected.

## Local proof

- strict parser tests: loopback IPv4 and IPv6 accepted;
- remote host, HTTPS, credentials, path, query, fragment, missing port and oversized file rejected;
- dynamic :0 fake tunnel writes the URL file and FileMCP reports Reachable;
- stopping the endpoint changes health to Unreachable while preserving last success;
- stale URL file is removed before child launch;
- restart tests run with MCP_TEST_HEALTH_REQUIRE_FRESH and therefore prove stale-file cleanup across supervised restarts;
- runtime stop removes the URL file;
- UI state now says dynamic/pending until discovery completes.

Local gates:
- project-state-contract: PASS
- tool-surface-parity: PASS
- macos-supervisor-contract: PASS
- http-connection-bounds-contract: PASS
- dynamic-health-discovery-contract: PASS
- windows-arm64-assurance-contract: PASS
- windows-single-instance-contract: PASS
- Windows Release build: PASS, 0 warnings / 0 errors
- Windows runtime suite: PASS, 444 assertions
- git diff --check: PASS

Native GitHub macOS / Windows x64 / Windows ARM64 verification remains required before FPA-009 PASS.

# FPA-009 - Independent Review

Date: 2026-09-22

Round 1 - mechanism: log parsing rejected; official tunnel-client health.url-file selected.
Round 2 - stale state: URL file must be deleted before every child launch, not only first startup.
Round 3 - SSRF/local boundary: URL-file content is validated to loopback HTTP only.
Round 4 - restart: cached resolved endpoint is invalidated on child exit so a new ephemeral port is rediscovered.
Round 5 - fixed-port compatibility: existing direct probe remains first choice.
Round 6 - privacy: file carries endpoint only; no secrets or MCP payload content.
Round 7 - scope: Windows only because that is where component-health probing/dashboard exists; no fake macOS parity work is introduced.

Decision: implement official URL-file discovery with strict loopback validation.

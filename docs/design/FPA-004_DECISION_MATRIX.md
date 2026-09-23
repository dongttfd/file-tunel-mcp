# FPA-004 - Solution Decision Matrix

## Windows

| Option | Portable | Secret isolation | Timestamp | Current repo fit | Decision |
|---|---|---|---|---|---|
| No signing | Yes | N/A | No | Existing | Reject for public release |
| PFX passed directly to signtool with /p | Yes | Weak | Yes | Easy | Reject |
| PFX imported to temp CurrentUser store + sign by thumbprint | Yes | Strong | Yes | Strong | SELECT |
| Azure Trusted Signing | Strong | Strong | Yes | Requires additional Azure account/config | Defer alternative |

## macOS notarization

| Option | Automation | Rotation | Secret handling | Decision |
|---|---|---|---|---|
| Unsigned/local only | N/A | N/A | N/A | Reject for public release |
| Apple ID + app-specific password | Good | Medium | Medium | Reject default |
| App Store Connect API key + notarytool | Strong | Strong | Strong | SELECT |

## Workflow topology

| Option | PR secret exposure risk | Release clarity | Decision |
|---|---:|---:|---|
| Put signing in verify.yml | High/complex | Low | Reject |
| Dedicated manual release workflow + protected environment | Low | High | SELECT |

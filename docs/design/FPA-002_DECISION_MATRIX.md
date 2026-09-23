# FPA-002 - Solution Decision Matrix

| Option | Tool parity | Security | Complexity | Truthfulness | Decision |
|---|---|---|---|---|---|
| A. Change README to say Windows-only correlation | No functional parity | Strong | Low | Accurate but reduces product parity | Reject |
| B. Add tool name only, no real handle registry | Superficial | Weak semantics | Low | Misleading/no-op | Reject |
| C. Port bounded in-memory correlation facade | Yes | Strong with hash/bounds | Medium | Accurate process-local parity | **SELECT** |
| D. Port full Windows Observability V1 stack to macOS now | Yes + more | Potentially strong | Very high | Accurate but scope explosion | Defer |

Selected: **C**.

Technology choices:

| Concern | Choice | Why |
|---|---|---|
| CSPRNG | Security / SecRandomCopyBytes | native, cryptographically secure |
| Hash | CryptoKit SHA256 | native and deterministic |
| synchronization | NSLock | small critical section, simple correctness |
| store | in-memory Dictionary keyed by hash | matches FPA-002 scope |
| eviction | oldest lastSeen | mirrors Windows behavior |
| metadata facade | schema augmentation + strip-before-validation | compatibility without authority change |

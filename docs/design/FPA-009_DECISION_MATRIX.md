# FPA-009 - Decision Matrix

| Option | Dynamic :0 probe | Robust | Security | Scope | Decision |
|---|---|---|---|---|---|
| Keep limitation | No | Yes | Strong | Low | Reject because official mechanism exists |
| Parse tunnel logs | Maybe | Fragile | Medium | Low | Reject |
| Guess/listen-scan ports | Maybe | Fragile | Weak | Medium | Reject |
| Force fixed health port | Yes | Medium | Strong | User-visible behavior change | Reject |
| Use tunnel-client --health.url-file | Yes | Strong | Strong with loopback validation | Low | SELECT |

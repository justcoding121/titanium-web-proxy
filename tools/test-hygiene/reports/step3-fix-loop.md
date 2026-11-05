# Step 3 fix loop

Two consecutive full runs of `tools/run-tests.ps1 -Project all -Verify` finished with exit 0. Each used a never-used worktree of `89b7c379` on `chore/test-no-prompt-hygiene`. Neither run set `CI`, `GITHUB_ACTIONS`, `TF_BUILD`, or `TITANIUM_ALLOW_WILDCARD_BIND_TESTS`.

| Run | Worktree | Result |
| --- | --- | --- |
| 1 | `C:\Work\qa-prompt-audit\edcb3b32007340339d2ee81f807edd57` | Exit 0. No prompt log. TWP certificates 0. Machine-state diff empty. |
| 2 | `C:\Work\qa-prompt-audit\f1e3b0cd56e84a02bf2d1b1127f30b07` | Exit 0. No prompt log. TWP certificates 0. Machine-state diff empty. |

No Windows Security dialog, no UAC consent, and no wildcard listen from a test process. HTTP/3 and other wildcard binds are inconclusive on this PC because the test host refuses them unless `TITANIUM_ALLOW_WILDCARD_BIND_TESTS=1`. CI sets that variable and still listens. Counts from run 2:

| Project | Total | Passed | Skipped |
| --- | --- | --- | --- |
| configuration | 335 | 334 | 1 |
| scripting | 105 | 68 | 37 |
| manager | 32 | 31 | 1 |
| gateway | 291 | 290 | 1 |
| xds | 11 | 11 | 0 |
| geoip | 3 | 3 | 0 |
| unit | 2904 | 2885 | 19 |
| cli | 353 | 350 | 3 |
| plus | 461 | 459 | 2 |
| inspector | 711 | 699 | 12 |
| integration | 778 | 682 | 96 |
| e2e | 252 | 227 | 25 |

The Step 2 baseline had one inspector failure (`Prune_LeavesAtMostKeepFiles`). These two runs had no failures. The new skips are the refused QUIC and wildcard listens, not new failures.

## What stopped the prompts

- Raw HTTP/2 origins used `TcpListener.Create`, which listens on every interface. They now listen on `127.0.0.1` and `::1`. The lint test rejects `TcpListener.Create`.
- A developer test host refuses a QUIC listen. A CLI child skips that listen and keeps the TCP proxy. A product process that does not set `TITANIUM_SKIP_ROOT_STORE_UI` still listens.
- DNS queries on that host bind to loopback and fail closed instead of connecting an unbound UDP socket.
- Suppressed trust installation does not write the Personal store.
- The prompt watcher records OS prompt titles. It does not record the Inspector window, the sign-in picker, or an outbound datagram's public source address.
- Firewall comparison ignores rule ids. Windows rewrites the id when an app refreshes the same allow entry.

## Still needs an elevated window

A dry run of `tools/test-hygiene/Remove-TestFirewallRules.ps1` found 6 block rules from earlier Cancel clicks (4 `testhost.exe`, 2 `dotnet.exe`). They were not removed. Pass `-Apply` from an elevated PowerShell if those rules should go. The backup of the earlier clean slate remains `C:\Work\qa-prompt-audit\fw-backup\firewall-before.wfw`.

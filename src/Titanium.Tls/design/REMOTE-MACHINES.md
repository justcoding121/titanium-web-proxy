# Remote machines

No secrets belong in this file.

| Alias | Address | OS | Login check | Sudo |
| --- | --- | --- | --- | --- |
| macmini | 192.168.1.55 | macOS 26.6.2, Darwin arm64 | Key login as `jehonathant` | `sudo -n true` succeeds |
| linuxmini | 192.168.1.2 | Ubuntu 26.04.1 LTS, Linux x86_64, 12 cores, about 429 GB free | Key login as `jehonathant` | `sudo -n true` succeeds |

The key is `~/.ssh/id_ed25519_twp` on this PC. `~/.ssh/config` has the aliases `macmini` and `linuxmini`. The machines do not commit or push.

## Already present

- macmini: Apple Git 2.54.0, Xcode at `/Applications/Xcode.app/Contents/Developer`, system LibreSSL 3.3.6 (not the oracle). No Homebrew. .NET SDK 10.0.202 and 10.0.404 are under `~/.dotnet`. OpenSSL 3.5.9 is at `~/opt/openssl-3.5.9` and does not replace LibreSSL. `openssl list -tls-groups` includes `X25519MLKEM768`, `MLKEM768`, and `x25519`.
- linuxmini: Git 2.53.0, OpenSSL 3.5.5. `openssl list -tls-groups` includes `X25519MLKEM768`, `MLKEM768`, and `x25519`, so this build can serve as the oracle. .NET SDK 10.0.404 is under `~/.dotnet`.

Non-interactive SSH does not source the shell profile. Call `$HOME/.dotnet/dotnet` directly. `DOTNET_ROOT` is also appended to `~/.zprofile` on macmini and `~/.bashrc` on linuxmini.

## Still to install

PowerShell 7 on both so the same helper scripts can run. Docker and libmsquic on linuxmini when Phase 2 and Phase 4 need them.

# Remote machines

No secrets belong in this file.

| Alias | Address | OS | Login check | Sudo |
| --- | --- | --- | --- | --- |
| macmini | 192.168.1.55 | macOS 26.6.2, Darwin arm64 | Key login as `jehonathant` | `sudo -n true` succeeds |
| linuxmini | 192.168.1.2 | Ubuntu 26.04.1 LTS, Linux x86_64, 12 cores, about 429 GB free | Key login as `jehonathant` | `sudo -n true` succeeds |

The key is `~/.ssh/id_ed25519_twp` on this PC. `~/.ssh/config` has the aliases `macmini` and `linuxmini`. The machines do not commit or push.

## Already present

- macmini: Apple Git 2.54.0, Xcode at `/Applications/Xcode.app/Contents/Developer`, system LibreSSL 3.3.6 (not the oracle). No Homebrew. .NET SDK 10.0.202 and 10.0.404 are under `~/.dotnet`. OpenSSL 3.5.9 is at `~/opt/openssl-3.5.9` and does not replace LibreSSL. `openssl list -tls-groups` includes `X25519MLKEM768`, `MLKEM768`, and `x25519`. A restore that contacts `api.nuget.org` fails with `UntrustedRoot`. A tree that is already restored builds with `--no-restore` once a stored `NU1900` log is cleared from `project.assets.json`.
- linuxmini: Git 2.53.0, OpenSSL 3.5.5. `openssl list -tls-groups` includes `X25519MLKEM768`, `MLKEM768`, and `x25519`, so this build can serve as the oracle. .NET SDK 10.0.404 is under `~/.dotnet`.

Non-interactive SSH does not source the shell profile. Call `$HOME/.dotnet/dotnet` directly. `DOTNET_ROOT` is also appended to `~/.zprofile` on macmini and `~/.bashrc` on linuxmini.

## Installed for the managed stack

- PowerShell 7.7.0 at `~/opt/powershell/pwsh` on both machines, from the official release archive. It is not on the default `PATH`. Non-interactive SSH still calls `$HOME/.dotnet/dotnet` and `$HOME/opt/powershell/pwsh` by full path.
- A clone of the public repository is at `~/work/twp` on both, checked out at `feat/managed-tls-quic-stack`. Sync is a git bundle copied with `scp` (`git fetch ~/twp-managed.bundle HEAD:feat/managed-tls-quic-stack`). Nothing is pushed from these machines.

## Installed for the QUIC interop leg

- linuxmini: Docker Engine 29.1.3 from the Ubuntu package `docker.io` (29.1.3-0ubuntu4.1), with containerd 2.2.2 and runc 1.4.0. `docker.service` is enabled. Docker Compose 2.40.3 is the `docker-compose-v2` package. `tshark` 4.6.4 is installed for the interop runner. The install is for the QUIC interop runner and h3spec. No extra firewall rule was added.
- linuxmini: `h3spec` 0.1.15, the Linux x86_64 release binary, at `/tmp/h3spec-linux-x86_64`. The release page did not publish a checksum file. The SHA-256 of the downloaded file is `c3c5f497153f4ba81f82023791ba7f9a4bc46d4141211e614196f141c5996830`.

## Still to install

`libmsquic` on linuxmini as a package. A copy was extracted earlier for the platform listener and is not a system package. `QuicConnection.IsSupported` stays false until that library is on the loader path.

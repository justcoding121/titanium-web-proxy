# Progress

## A0

- Worktree: `c:\Work\Repositories\twp-managed`, branch `feat/managed-tls-quic-stack`, cut from `origin/develop` at `8b018a52`. The branch does not track `origin/develop`. The main checkout stayed on `develop`.
- SDK 10.0.404 is at `C:\Program Files\dotnet` and is not on the default PATH. Shells prefix that directory.
- OpenSSL 3.5.9 is at `C:\Program Files\Git\usr\bin\openssl.exe`.
- Installed, with approval: PowerShell 7.7.0, actionlint 1.7.12, `dotnet-stryker` 5.1.0, `dotnet-counters` 10.0.750501, `dotnet-trace` 10.0.750501.

## S1 and S5

See `SPIKES.md`.

## T1.0

Scaffold committed. `pwsh tools/run-tests.ps1 -Project tls` passed (2 tests). Workflow actionlint passed. The PyYAML preflight step fails on this PC because `python` is the Microsoft Store stub, which A2 already recorded. No workflow syntax error came from actionlint.

## A0b

Key login works for `macmini` (192.168.1.55, Darwin arm64, macOS 26.6.2) and `linuxmini` (192.168.1.2, Ubuntu 26.04.1, x86_64). `sudo -n true` succeeds on both. The password was used once to install the key and is not stored. Both machines have the .NET 10.0.404 SDK under `~/.dotnet`. macmini also has OpenSSL 3.5.9 under `~/opt/openssl-3.5.9`, with `X25519MLKEM768`. Details are in `REMOTE-MACHINES.md`.

## T1.1

`TlsHkdf.ExpandLabel` matches the RFC 8448 section 3 `c hs traffic`, `s hs traffic`, and `derived` values. `TranscriptStore` hashes, supports `HashWith` for binders, and replaces the transcript with a `message_hash` handshake message. Remaining for the task's done-when: the rest of the RFC 8448 section 3 to 5 Expand-Label rows, the section 5 HelloRetryRequest transcript, the RFC 5869 sanity cases, the zero-allocation gate, and the traceability rows.

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

## T1.4

`X25519` is a 51-bit-limb ladder. It matches RFC 7748 section 5.2, the Alice/Bob agreement in section 6.1, and the 1,000-iteration chain that starts from the base point. An all-zero shared secret is rejected. Still open: the million-iteration vector, Wycheproof, the OpenSSL differential, the timing check, and the constant-time review.

## T1.3

`TlsRecordProtector` protects one direction with AES-128-GCM, AES-256-GCM, or ChaCha20-Poly1305. It decrypts the RFC 8448 section 3 server handshake flight and the client Finished record. Padding, a bad tag, a replay, an oversized plaintext, and an all-zero inner plaintext are rejected. The IV is wiped on dispose.

## T1.2

The schedule walks Early, Handshake, and Master. A transition zeroes the secret it consumes. Section 3 covers the master secret, application traffic keys, the exporter master, and the resumption PSK. Section 4 covers the early traffic secret and the resumption binder. Section 5 covers the HelloRetryRequest handshake secret. SHA-384 is checked against an HMAC implementation in the test project.

## T1.1

`TlsHkdf.ExpandLabel` matches every Expand-Label output in RFC 8448 sections 3 to 5, including traffic secrets, finished keys, key, and iv. `TranscriptStore.ReplaceWithMessageHash` reproduces the section 5 HelloRetryRequest transcript hash. RFC 5869 cases 1 to 3 pass through `HKDF.Extract` and `HKDF.Expand`. The label builder adds no allocation beyond `HKDF.Expand` itself, which still allocates inside the runtime. Traceability rows for RFC 8446 sections 7.1 and 4.4.1 are in `TRACEABILITY.md`.

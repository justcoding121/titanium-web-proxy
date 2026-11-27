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

Key login works for `macmini` (192.168.1.55, Darwin arm64, macOS 26.6.2) and `linuxmini` (192.168.1.2, Ubuntu 26.04.1, x86_64). `sudo -n true` succeeds on both. The password was used once to install the key and is not stored. Both machines have the .NET 10.0.404 SDK under `~/.dotnet`, PowerShell 7.7.0 under `~/opt/powershell`, and a clone at `~/work/twp` on `feat/managed-tls-quic-stack`. macmini also has OpenSSL 3.5.9 under `~/opt/openssl-3.5.9`, with `X25519MLKEM768`. Details are in `REMOTE-MACHINES.md`.

`DESIGN.md` and the C1 checklist in `SELF-REVIEW.md` are written. The harness has `HexVectors`, `OpenSslOracle`, `FixedEntropy`, `AllocationGate`, the traceability guard, and a corpus runner. `TranscriptStore` now keeps an `IncrementalHash`. `TlsRecordProtector.TryUnprotect` returns a `RecordResult` instead of throwing on a bad record. Stage secrets come from a 48-byte pinned pool, and the master secret is wiped once both application traffic secrets, the exporter master, and the resumption master have been derived. A plaintext record reader accepts one ChangeCipherSpec. The X25519 disassembly has no secret-dependent branch. Next command: the linuxmini timing check, then the key-exchange groups. S5 key update and the Chrome QUIC capture are still open.

## T1.4

`X25519` is a 51-bit-limb ladder. It matches RFC 7748 section 5.2, the Alice/Bob agreement in section 6.1, and the 1,000-iteration chain that starts from the base point. An all-zero shared secret is rejected. Release measurements on this PC, 200 calls after one warmup: dedicated squaring on the old 128-bit multiply was 1240 µs; a hardware `Math.BigMul` with a carry after every add was 817 µs; the same multiply with addition left unreduced until the next multiply was 462 µs. A regression test fails above 900 µs. The 60 µs target is not met. BouncyCastle 2.6.2 on the same machine, same loop, was 253 µs, so the gap is not closed by switching libraries either. The portable ladder stays. The RFC 7748 million-iteration output matches, 10,000 random pairs match BouncyCastle, 50 OpenSSL `pkeyutl` samples match, and the Wycheproof `x25519_test.json` vectors match. The Release disassembly of `ConditionalSwap` and `ToBytes` has no secret-dependent branch. Still open: the timing check on linuxmini. Next command: that timing check, then the key-exchange groups.

## T1.3

`TlsRecordProtector` protects one direction with AES-128-GCM, AES-256-GCM, or ChaCha20-Poly1305. It decrypts the RFC 8448 section 3 server handshake flight and the client Finished record. Padding, a bad tag, a replay, an oversized plaintext, and an all-zero inner plaintext are rejected. The IV is wiped on dispose. A plaintext header reader rejects an oversized length and accepts a single `0x01` ChangeCipherSpec. Wycheproof AES-GCM and ChaCha20-Poly1305 vectors with a 96-bit nonce match the primitives this layer calls.

## T1.2

The schedule walks Early, Handshake, and Master. A transition zeroes the secret it consumes. Section 3 covers the master secret, application traffic keys, the exporter master, and the resumption PSK. Section 4 covers the early traffic secret and the resumption binder. Section 5 covers the HelloRetryRequest handshake secret. SHA-384 is checked against an HMAC implementation in the test project.

## T1.1

`TlsHkdf.ExpandLabel` matches every Expand-Label output in RFC 8448 sections 3 to 5, including traffic secrets, finished keys, key, and iv. `TranscriptStore.ReplaceWithMessageHash` reproduces the section 5 HelloRetryRequest transcript hash. RFC 5869 cases 1 to 3 pass through `HKDF.Extract` and `HKDF.Expand`. A one-block Expand-Label is a single `HMACSHA256` or `HMACSHA384` call, and that path allocates nothing in a Release run on this PC (64 iterations). Outputs longer than one hash block still go through `HKDF.Expand`. Traceability rows for RFC 8446 sections 7.1 and 4.4.1 are in `TRACEABILITY.md`.

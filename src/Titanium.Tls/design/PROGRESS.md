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

`DESIGN.md` and the C1 checklist in `SELF-REVIEW.md` are written. The harness has `HexVectors`, `OpenSslOracle`, `FixedEntropy`, `AllocationGate`, the traceability guard, and a corpus runner. `TranscriptStore` now keeps an `IncrementalHash`. `TlsRecordProtector.TryUnprotect` returns a `RecordResult` instead of throwing on a bad record. Stage secrets come from a 48-byte pinned pool, and the master secret is wiped once both application traffic secrets, the exporter master, and the resumption master have been derived. A plaintext record reader accepts one ChangeCipherSpec. The X25519 disassembly has no secret-dependent branch. `KeyExchangeGroups` covers X25519, secp256r1, secp384r1, and X25519MLKEM768. The hybrid group uses the platform ML-KEM when it is present and the managed fallback otherwise. Ed25519 verification is in place and is not a signing operation. The C2 review's major findings are applied. The C3 self-review is in `SELF-REVIEW.md`. Handshake codecs parse and write the TLS 1.3 messages, including a Chrome 158 ClientHello and the RFC 8448 server flight. The Chrome 158 profile is measured from the 50 captured ClientHellos, and the C4 self-review is in `SELF-REVIEW.md`. The engine skeleton reaches `Connected` on a stub flight. Next command: the client handshake (T1.9). Still open: the linuxmini X25519 timing check, an OpenSSL NIST derive sample, S5 key update, and the Chrome QUIC capture.

## T1.4

`X25519` is a 51-bit-limb ladder. It matches RFC 7748 section 5.2, the Alice/Bob agreement in section 6.1, and the 1,000-iteration chain that starts from the base point. An all-zero shared secret is rejected. Release measurements on this PC, 200 calls after one warmup: dedicated squaring on the old 128-bit multiply was 1240 µs; a hardware `Math.BigMul` with a carry after every add was 817 µs; the same multiply with addition left unreduced until the next multiply was 462 µs. A regression test fails above 900 µs. The 60 µs target is not met. BouncyCastle 2.6.2 on the same machine, same loop, was 253 µs, so the gap is not closed by switching libraries either. The portable ladder stays. The RFC 7748 million-iteration output matches, 10,000 random pairs match BouncyCastle, 50 OpenSSL `pkeyutl` samples match, and the Wycheproof `x25519_test.json` vectors match. The Release disassembly of `ConditionalSwap` and `ToBytes` has no secret-dependent branch. Still open: the timing check on linuxmini.

## T1.5

`NamedGroup` carries secp256r1 (`0x0017`), secp384r1 (`0x0018`), x25519 (`0x001D`), and X25519MLKEM768 (`0x11EC`). `KeyExchangeGroups.Create` returns an X25519 share or a NIST share. The NIST share is an uncompressed `04 || X || Y` point and the shared secret is the padded X coordinate. A bad prefix, a short share, and an off-curve point are rejected. Two locally generated sides agree for all three groups. Wycheproof `ecdh_secp256r1_ecpoint_test.json` and `ecdh_secp384r1_ecpoint_test.json` match, including the shared secret. An OpenSSL derive sample for the NIST groups is still open.

## T1.6

X25519MLKEM768 is asymmetric. The client share is 1184 bytes of ML-KEM-768 encapsulation key followed by 32 bytes of X25519. The server share is 1088 bytes of ciphertext followed by 32 bytes of X25519. The shared secret is the 32-byte ML-KEM secret followed by the 32-byte X25519 secret. Chrome 158 and OpenSSL 3.5.9 both use that order, and Chrome's X25519 half is not the standalone X25519 share (0 of 50). A damaged ciphertext still decapsulates and the secret changes. `TlsFingerprint.Current` is `Full` when `MLKem.IsSupported` is true and `ReducedNoPostQuantum` otherwise.

## T1.6b

`ManagedKeccak` matches platform SHA3-256, SHA3-512, SHAKE128, and SHAKE256, including the empty input. `ManagedMlKem768` follows FIPS 203: matrix `A[i,j]` is `SampleNTT(ρ || j || i)`, the client share is the encapsulation key then X25519, and a bad ciphertext is implicit rejection. A seeded round trip agrees with itself. Against platform `MLKem` on this PC, a managed key decapsulates a platform ciphertext, a platform key decapsulates a managed ciphertext, and both sides produce the same secret after one ciphertext byte is flipped. An encapsulation key whose first coefficient is at least `q`, and a decapsulation key whose stored hash does not match `SHA3-256(ek)`, both fail closed. Seeded encapsulation allocates nothing after warmup. On this PC, 50 encapsulations after one warmup averaged 652 µs managed and 14.8 µs platform. The 2 ms regression ceiling is the test bound. Still open: NIST ACVP files, a dudect run, and an OpenSSL hybrid handshake in both directions.

## T1.18

`Ed25519Verifier` checks RFC 8032 section 5.1.7 with the cofactorless equation `[S]B = R + [k]A`. It rejects a non-canonical `S`, a non-canonical point, and a small-order public key. The field arithmetic is the X25519 51-bit limb type. SHA-512 is the platform hash. There is no signing operation. `SignatureScheme.Ed25519` is `0x0807`. `TlsAlgorithmPolicy.FipsApprovedOnly` rejects it. RFC 8032 section 7.1 (including the 1023-byte message), the Wycheproof Ed25519 verify vectors, and eight OpenSSL signatures match. A flipped signature byte does not. The Chrome profile must not advertise `0x0807`. Wiring the check into `CertificateVerify`, and an Ed25519 handshake against `openssl s_server`, wait until the handshake exists.

## C2

The constant-time review found no blockers. Four majors are applied. ML-KEM adds the message polynomial without branching on the decrypted bits, and `Mod` no longer branches on the sign. Field carries use an arithmetic carry bit, and `ToBytes` reduces with `ulong` rather than a 128-bit add. Keccak and the CBD sampler wipe their scratch buffers, and the hybrid decapsulation key is pinned for its lifetime because the 48-byte secret pool cannot hold 2400 bytes. The 60 µs X25519 miss and the variable-time Ed25519 verifier stay as specified. Still open from that review: a dudect run, and division in compress and decode, which now opts into optimized JIT code but is not a fixed multiply-shift.

## T1.7

`TlsReader` and `TlsWriter` read and write TLS vectors on a caller span, back-patch length prefixes, and flush into an `IBufferWriter`. Handshake messages cover ClientHello, ServerHello (a HelloRetryRequest is the RFC 8446 random), EncryptedExtensions, Certificate, CertificateRequest, CertificateVerify, Finished, NewSessionTicket, KeyUpdate, EndOfEarlyData, CompressedCertificate, and MessageHash. A known extension on the wrong message is `unsupported_extension` (110). An unknown extension is kept. A duplicate of either, or a `pre_shared_key` that is not last in a ClientHello, is `illegal_parameter` (47). A length or vector failure is `decode_error` (50). The reassembler caps a message at 128 KiB by default and refuses anything above 1 MiB. A ClientHello parse allocates nothing. Chrome 158 `hello-001.bin` round-trips, including the key-share and encrypted-client-hello extensions. The RFC 8448 server flight yields EncryptedExtensions, Certificate, CertificateVerify, and Finished. CompressedCertificate is parsed and not decompressed.

## T1.8

`ChromeFingerprintProfile` is measured from the 50 TCP ClientHellos in `tests/Titanium.Tls.Tests/Fixtures/captures/chrome/158.0.8127.89/tcp`. Record version `0x0301`, legacy version `0x0303`, a 32-byte session id, and no padding. The cipher, group, version, and signature lists are constant apart from their GREASE value. The first and last extensions are GREASE with empty and one-byte bodies, and those two values differ. The GREASE group and the GREASE key share are the same value. The other GREASE slots are independent. The 17 middle extensions are shuffled. `encrypted_client_hello` is outer type 0, KDF `0x0001`, AEAD `0x0001`, a 32-byte enc, and a payload length drawn from the 50 captured lengths. Extension `0xCA34` is the 186-byte constant from the captures. A generated hello matches the JA4 of `hello-001.bin`. JA3 is checked as cipher, group, and point-format sets because the extension order changes the hash. `ReducedNoPostQuantum` omits group `0x11EC` and its key share. `ClientHelloTemplate` stays internal until a second browser lands. `CertificateCompression.TryDecompress` accepts brotli and rejects a declared length that does not match the output.

## C4

The profile constants match `hello-001.bin`, including the 186-byte `0xCA34` body, the ECH outer header, and the key-share lengths. JA4 does not cover those bodies, so `ConstantExtensionBodiesMatchTheCapture` does. HelloRetryRequest order and a trailing `pre_shared_key` are not in the captures; they follow the plan and RFC 8446. Eight fresh ClientHellos from the installed Chrome matched the pinned invariants (`TITANIUM_CHROME_DRIFT=1`, category `Explicit`). A normal test run does not launch the browser. No consult.

## T1.8b

`TlsEngine.Advance` and `Resume` are synchronous. A client and a server reach `Connected` by exchanging empty handshake messages: the server pauses on `ClientHelloReceived`, the client pauses on `ServerCertificateReceived`, and Finished completes both sides. A message the current state does not list is `unexpected_message` (10). After that alert the engine is `Closed` and further input is ignored. A handshake body above 128 KiB is `decode_error` (50). More than 32 consecutive empty records is `unexpected_message`. A record that does not fit the buffer cap is `record_overflow` (22). No method on `TlsEngine` returns a `Task` or a `ValueTask`. `CryptoExecutionPolicy` selects online revocation and an external private key for a later offload and still runs the call inline.

## C3

The key-schedule, record, and key-exchange checklist is in `SELF-REVIEW.md`. Two majors came out of it. An inner plaintext is content plus the content-type byte plus padding, and that sum must not exceed 2^14; `Protect` and `TryUnprotect` now enforce it, including a padding value that would wrap a 32-bit add. The HKDF salt and the all-zero input keying material are cleared before extract. Record and key-schedule tests pass, including RFC 8448. The change-cipher-spec window beyond "accept one plaintext `0x01`" belongs to the handshake engine.

## T1.3

`TlsRecordProtector` protects one direction with AES-128-GCM, AES-256-GCM, or ChaCha20-Poly1305. It decrypts the RFC 8448 section 3 server handshake flight and the client Finished record. Padding, a bad tag, a replay, an oversized plaintext, and an all-zero inner plaintext are rejected. The IV is wiped on dispose. A plaintext header reader rejects an oversized length and accepts a single `0x01` ChangeCipherSpec. Wycheproof AES-GCM and ChaCha20-Poly1305 vectors with a 96-bit nonce match the primitives this layer calls.

## T1.2

The schedule walks Early, Handshake, and Master. A transition zeroes the secret it consumes. Section 3 covers the master secret, application traffic keys, the exporter master, and the resumption PSK. Section 4 covers the early traffic secret and the resumption binder. Section 5 covers the HelloRetryRequest handshake secret. SHA-384 is checked against an HMAC implementation in the test project.

## T1.1

`TlsHkdf.ExpandLabel` matches every Expand-Label output in RFC 8448 sections 3 to 5, including traffic secrets, finished keys, key, and iv. `TranscriptStore.ReplaceWithMessageHash` reproduces the section 5 HelloRetryRequest transcript hash. RFC 5869 cases 1 to 3 pass through `HKDF.Extract` and `HKDF.Expand`. A one-block Expand-Label is a single `HMACSHA256` or `HMACSHA384` call, and that path allocates nothing in a Release run on this PC (64 iterations). Outputs longer than one hash block still go through `HKDF.Expand`. Traceability rows for RFC 8446 sections 7.1 and 4.4.1 are in `TRACEABILITY.md`.

# Spikes

Findings from Phase 0. Throwaway projects under `scratch/` are not part of the product and are not committed.

## S1. BCL surface probe

Machine: Windows x64, runtime 10.0.15, SDK 10.0.404. Method: a Release console probe (`scratch/BclProbe`) that reflects the public surface and runs the operations.

- `SslServerAuthenticationOptions`, `SslClientAuthenticationOptions`, `QuicClientConnectionOptions`, `QuicServerConnectionOptions`, and `QuicListenerOptions` all construct. Every property listed by the probe is publicly settable. Notable members: `AllowTlsResume` on both TLS option types, `ServerCertificateContext` and `ClientCertificateContext`, `QuicListenerOptions.ConnectionOptionsCallback`.
- `SslClientHelloInfo` has a public constructor `(string serverName, SslProtocols sslProtocols)`. The managed stack can construct it. It does not need its own hello-info type for that shape.
- `QuicException` has a public constructor `(QuicError error, long? applicationErrorCode, string message)`. The managed stack can throw it.
- `SslStreamCertificateContext` has no public constructor. `Create(X509Certificate2, X509Certificate2Collection, bool)` and the `SslCertificateTrust` overload are public. `TargetCertificate` and `IntermediateCertificates` are public and readable. TWP can pass a context and the managed stack can read the leaf and intermediates.
- `X509Certificate2.MatchesHostname(string, bool, bool)` exists. Use it for hostname checks.
- `ServerCertificateSelectionCallback` is `(object sender, string hostName) -> X509Certificate`. `LocalCertificateSelectionCallback` takes `object sender`. Both are reusable. The QUIC listener callback is `Func<QuicConnection, SslClientHelloInfo, CancellationToken, ValueTask<QuicServerConnectionOptions>>`. `QuicConnection` is sealed, so a managed listener needs its own delegate. The callback's hello-info argument can still be `SslClientHelloInfo`.
- `AesGcm.IsSupported` and `ChaCha20Poly1305.IsSupported` are true. In-place encrypt and decrypt (ciphertext overlapping plaintext exactly) round-trips for both. Tag size 16 is accepted by `new AesGcm(key, 16)`.
- `MLKem.IsSupported` is true. ML-KEM-768 sizes are encapsulation key 1184, ciphertext 1088, shared secret 32, decapsulation key 2400. Generate, encapsulate, import, and decapsulate agree. `SHA3_256`, `SHA3_512`, `Shake128`, and `Shake256` are supported on this machine. The managed ML-KEM fallback is still required for platforms where `MLKem.IsSupported` is false. Confirm those flags on macOS in A0b.
- ECDH P-256 and P-384 agree (32 and 48 bytes). Creating a public-only `ECParameters` value, and creating one whose public point was flipped, both throw `PlatformNotSupportedException` ("curve is not valid for this platform") rather than a point-validation error. Do not rely on that API to reject off-curve points. Validate points in our own code before `DeriveRawSecretAgreement`.
- `ECCurve.CreateFromFriendlyName("curve25519")` plus `ECDiffieHellman.Create` succeeds on Windows (key size 255) and two keys agree on a 32-byte secret. B5 stays managed: this is not a portable API across the three operating systems, and the Chrome share must not depend on a platform that only sometimes has it. Keep `IKeyExchangeGroup` so a later `net11.0` build can call `X25519DiffieHellman` where it exists.
- `HKDF.Extract` and `HKDF.Expand` span overloads work. `Aes.EncryptEcb(ReadOnlySpan<byte>, Span<byte>, PaddingMode)` writes 16 bytes. `BrotliDecoder.TryDecompress` returns true.
- `QuicConnection.IsSupported` is true on this PC. It is false on macmini and on linuxmini (no libmsquic). Platform QUIC differential tests that listen on a wildcard UDP socket are not run on this PC (they raise a firewall prompt). Run those on the remote machines once libmsquic is installed, and in CI.
- `SslStream` TLS 1.3 baselines (loopback, self-signed RSA, validation callback returns true, cipher `TLS_AES_256_GCM_SHA384`):
  - TCP reset without `close_notify`: `ReadAsync` throws `IOException` ("forcibly closed by the remote host"), not 0.
  - Read after `Dispose`: `ObjectDisposedException`.
  - Zero-length write completes.
  - Zero-length read does not return immediately. A 200 ms cancellation throws `OperationCanceledException`.
  - Write after `ShutdownAsync`: `InvalidOperationException` ("Write operations are not allowed after the channel was shutdown.").
  - Read cancelled while blocked: `OperationCanceledException`.
  - Concurrent client write and server read: the server reads the 4 bytes.

The same probe on linuxmini (runtime 10.0.15, linux-x64, system OpenSSL 3.5.5) agrees with Windows on the option surface, AEAD, ECDH, and the `SslStream` baselines in `PARITY-DIFFS.md`. Differences: `MLKem.IsSupported` and SHA-3/SHAKE are true; an off-curve point is rejected with `OpenSslCryptographicException` ("point is not on curve"); `curve25519` is `PlatformNotSupportedException`; `QuicConnection.IsSupported` is false.

The same probe on macmini (runtime 10.0.15, osx-arm64) agrees on the option surface, AEAD, and ECDH P-256/P-384. Differences that bind later tasks: `MLKem.IsSupported` is false and `SHA3_256`, `SHA3_512`, `Shake128`, and `Shake256` are false, so T1.6b (managed ML-KEM and a managed Keccak) is required for macOS. `curve25519` is `PlatformNotSupportedException`, so the managed X25519 ladder is the portable implementation. An off-curve point fails with `AppleCFErrorCryptographicException` rather than a shared BCL exception. `QuicConnection.IsSupported` is false. Every `SslStream` TLS 1.3 scenario timed out at 30 seconds (`TimeoutException` on `AuthenticateAsClientAsync`/`AuthenticateAsServerAsync`); macOS is not a parity baseline until that handshake completes.

Effect on the plan: reuse `SslClientHelloInfo`, `QuicException`, the certificate-selection delegates, `SslStreamCertificateContext.Create`, and `MatchesHostname`. Keep a distinct QUIC listener callback because it takes `QuicConnection`. Validate ECDH points ourselves. The Windows and Linux `SslStream` baselines are in `PARITY-DIFFS.md`.

## S5. OpenSSL oracle

`C:\Program Files\Git\usr\bin\openssl.exe` is OpenSSL 3.5.9 (29 Sep 2026). `openssl list -tls-groups` includes `X25519MLKEM768`, `SecP256r1MLKEM768`, `MLKEM768`, `x25519`, `secp256r1`, and `secp384r1`.

Working interop, loopback only:

```text
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 2 -nodes -subj /CN=localhost
openssl s_server -tls1_3 -cert cert.pem -key key.pem -accept 127.0.0.1:18443 -groups X25519MLKEM768:X25519:P-256 -keylogfile keylog.txt -quiet
openssl s_client -tls1_3 -connect 127.0.0.1:18443 -groups X25519MLKEM768 -brief
```

Result: `CONNECTION ESTABLISHED`, `Protocol version: TLSv1.3`, `Ciphersuite: TLS_AES_256_GCM_SHA384`, `Negotiated TLS1.3 group: X25519MLKEM768`, `Signature type: rsa_pss_rsae_sha256`. The key log file was 987 bytes. Self-signed verification failed as expected and did not stop the handshake.

Further switches that the matrix needs, on the same server:

```text
openssl s_client -tls1_3 -connect 127.0.0.1:18443 -groups X25519 -ciphersuites TLS_AES_128_GCM_SHA256 -sigalgs rsa_pss_rsae_sha256 -alpn h2 -quiet
```

`k` and `K` written to `s_client` stdin (a pipe on Windows, a pty on linuxmini) did not add `CLIENT_TRAFFIC_SECRET_1` or `SERVER_TRAFFIC_SECRET_1` to the key log. The handshake secrets (`*_TRAFFIC_SECRET_0`, exporter, handshake traffic) were logged. The key-update command is not yet a confirmed oracle. Do not treat it as working in T1.13 until a run shows the `_1` secrets.

## S4. Chrome 158 ClientHello

Chrome 158.0.8127.89 was installed on this PC. Fifty fresh-profile TCP ClientHellos are in `tests/Titanium.Tls.Tests/Fixtures/captures/chrome/158.0.8127.89/tcp/`. Captured with `tools/managed-stack/Capture-ClientHello.ps1`. The byte-shift in that script must cast each header byte to `int` before `-shl`, or PowerShell keeps the length in a byte and the saved record is truncated.

What is stable across all 50:

- Record legacy version `0x0301`, ClientHello legacy version `0x0303`, session id length 32.
- Cipher list, in order: GREASE `0x5a5a`, `0x1301`, `0x1302`, `0x1303`, `0xc02b`, `0xc02f`, `0xc02c`, `0xc030`, `0xcca9`, `0xcca8`, `0xc013`, `0xc014`, `0x009c`, `0x009d`, `0x002f`, `0x0035`.
- Supported groups: GREASE `0x0a0a`, `0x11ec` (X25519MLKEM768), `0x001d`, `0x0017`, `0x0018`.
- Key shares: GREASE (1 byte), `0x11ec` (1216 bytes), `0x001d` (32 bytes). The X25519 share inside the hybrid did not equal the separate X25519 share in any of the 50 hellos.
- 19 extensions. Slot 0 and slot 18 are GREASE and those two GREASE values differ. The other slots permute. No `padding` and no `pre_shared_key` on a fresh profile (the hybrid share already makes the hello large, and there is no ticket).
- Present: `server_name`, ALPN, ECH (`0xfe0d`), `compress_certificate`, `supported_versions`, `key_share`, `signature_algorithms`, `signed_certificate_timestamp`, `extended_master_secret`, `session_ticket`, `psk_key_exchange_modes`, `status_request`, `ec_point_formats`, `renegotiation_info` (`0xff01`), and codepoint `0x44cd` (17613, one per hello; `0x4469` / 17513 is absent). Codepoint `0xca34` is also one per hello and is not named here.
- A QUIC Initial was not captured. `--enable-quic --origin-to-force-quic-on=localhost:<port>` produced no UDP datagram.

## S2. Crypto per-call cost

BenchmarkDotNet 0.14.0, Release, this PC (Windows x64, .NET 10.0.15, AVX-512 and AES). Five iterations, so treat the numbers as a floor, not a publication result. The throwaway project is not in the repo.

| Operation | Mean | Allocated |
| --- | --- | --- |
| AES-GCM 16 B | 120 ns | 0 |
| AES-GCM 1,200 B | 226 ns | 0 |
| AES-GCM 16 KiB | 1.42 µs | 0 |
| AES-ECB 16 B | 244 ns | 88 B |
| ChaCha20-Poly1305 16 B | 249 ns | 0 |
| ChaCha20-Poly1305 1,200 B | 2.38 µs | 0 |
| ChaCha20-Poly1305 16 KiB | 30.2 µs | 0 |
| HMAC-SHA256 32 B | 221 ns | 0 |
| `HKDF.Expand` 32 B | 448 ns | 288 B |
| ECDH P-256 derive (existing key) | 543 µs | 833 B |
| ML-KEM-768 encapsulate | 14.8 µs | 1,168 B |
| ML-KEM-768 decapsulate | 22.1 µs | 0 |
| RSA-2048 PSS sign | 420 µs | 56 B |
| RSA-2048 PSS verify | 13.6 µs | 56 B |
| ECDSA P-256 sign | 133 µs | 264 B |
| ECDSA P-256 verify | 115 µs | 120 B |

A QUIC packet on this CPU is two native calls (AES-GCM plus one AES block) at a few hundred nanoseconds. ChaCha is much slower than AES-GCM here. `HKDF.Expand` allocates about 288 bytes, which is why one-block labels now use `HMACSHA256.HashData` directly. ECDH derive and RSA sign dominate a full handshake; ML-KEM encapsulate is about 15 µs plus the allocation of the ciphertext array.

## S3

S3 is the managed X25519 implementation, landed as T1.4. Release on this PC: 1240 µs with dedicated squaring, 817 µs after `Math.BigMul` and eager carries, 462 µs after leaving addition unreduced. The 60 µs budget is missed. BouncyCastle 2.6.2 measured 253 µs in the same loop. The million-iteration RFC 7748 vector, 10,000 BouncyCastle pairs, 50 OpenSSL samples, and the Wycheproof file match. Release JIT on this PC, tiered compilation off: `ConditionalSwap` is a mask and xor with no taken branch. `ToBytes` reduces with `setb` rather than a branch; its only branches are the public span-length check and the stack cookie. `ScalarMult` branches on the public argument lengths and on the public bit index. The timing statistic on linuxmini is still open. Field multiply is a call, not inlined, which is the main reason the ladder is still at 462 µs.

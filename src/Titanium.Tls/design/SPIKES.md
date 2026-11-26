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
- `QuicConnection.IsSupported` is true on this PC. Platform QUIC differential tests that listen on a wildcard UDP socket are not run here (they raise a firewall prompt). Run those on the remote machines and in CI.
- `SslStream` TLS 1.3 baselines (loopback, self-signed RSA, validation callback returns true, cipher `TLS_AES_256_GCM_SHA384`):
  - TCP reset without `close_notify`: `ReadAsync` throws `IOException` ("forcibly closed by the remote host"), not 0.
  - Read after `Dispose`: `ObjectDisposedException`.
  - Zero-length write completes.
  - Zero-length read does not return immediately. A 200 ms cancellation throws `OperationCanceledException`.
  - Write after `ShutdownAsync`: `InvalidOperationException` ("Write operations are not allowed after the channel was shutdown.").
  - Read cancelled while blocked: `OperationCanceledException`.
  - Concurrent client write and server read: the server reads the 4 bytes.

Effect on the plan: reuse `SslClientHelloInfo`, `QuicException`, the certificate-selection delegates, `SslStreamCertificateContext.Create`, and `MatchesHostname`. Keep a distinct QUIC listener callback because it takes `QuicConnection`. Validate ECDH points ourselves. Record the `SslStream` baselines in `PARITY-DIFFS.md` when the stream exists.

## S5. OpenSSL oracle

`C:\Program Files\Git\usr\bin\openssl.exe` is OpenSSL 3.5.9 (29 Sep 2026). `openssl list -tls-groups` includes `X25519MLKEM768`, `SecP256r1MLKEM768`, `MLKEM768`, `x25519`, `secp256r1`, and `secp384r1`.

Working interop, loopback only:

```text
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 2 -nodes -subj /CN=localhost
openssl s_server -tls1_3 -cert cert.pem -key key.pem -accept 127.0.0.1:18443 -groups X25519MLKEM768:X25519:P-256 -keylogfile keylog.txt -quiet
openssl s_client -tls1_3 -connect 127.0.0.1:18443 -groups X25519MLKEM768 -brief
```

Result: `CONNECTION ESTABLISHED`, `Protocol version: TLSv1.3`, `Ciphersuite: TLS_AES_256_GCM_SHA384`, `Negotiated TLS1.3 group: X25519MLKEM768`, `Signature type: rsa_pss_rsae_sha256`. The key log file was 987 bytes. Self-signed verification failed as expected and did not stop the handshake.

`s_client` key-update stdin (`k`, `K`) is still to be exercised when T1.13 wires the oracle helper. The group and ciphersuite switches used above are the ones the matrix needs (`-groups`, default TLS 1.3 ciphersuite).

## S2, S3, S4

Not run yet. S2 is the crypto cost benchmark. S3 is the managed X25519 implementation and its timing evidence, landed as T1.4 rather than a second throwaway copy. S4 needs an installed Chrome or a user decision.

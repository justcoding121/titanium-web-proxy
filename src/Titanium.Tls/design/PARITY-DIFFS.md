# Parity differences

Expected differences between `SslStream` and `TlsStream`. An entry needs a reason. A divergence that is not listed here fails the parity suite.

Seeded from the S1 `SslStream` baselines (Windows x64 and Linux x64, runtime 10.0.15, TLS 1.3, `TLS_AES_256_GCM_SHA384`, self-signed RSA, validation callback returns true). macOS did not complete this handshake within 30 seconds, so it is not a baseline yet.

| Scenario | SslStream result |
| --- | --- |
| TCP reset without close_notify | `ReadAsync` throws `IOException` |
| Read after dispose | `ObjectDisposedException` |
| Zero-length write | Completes |
| Zero-length read | Does not return immediately. A 200 ms cancellation throws `OperationCanceledException` |
| Write after `ShutdownAsync` | `InvalidOperationException` ("Write operations are not allowed after the channel was shutdown.") |
| Read cancelled while blocked | `OperationCanceledException` |
| Concurrent client write and server read | The server reads the 4 bytes |

`TlsStream` matches those six rows on this PC: a transport end without `close_notify` throws `IOException`, read after dispose throws `ObjectDisposedException`, a zero-length write completes, a zero-length read waits, write after shutdown throws the `SslStream` message above, a cancelled read throws `OperationCanceledException`, and a concurrent write of 4 bytes is read by the peer. The same bytes also move against `SslStream` in both directions with a self-signed P-256 certificate and a validation callback that returns true.

Recorded differences:

- Revocation mode stays `NoCheck`. Online revocation is not hopped to another thread.
- The engine ClientHello is the built-in one (AES-128-GCM-SHA256, X25519 and P-256, `ecdsa_secp256r1_sha256`). `TlsStreamOptions.FingerprintProfile` is stored and `FingerprintFidelity` reports it. The profile is not the hello on the wire.
- A write that fits in 2^14 bytes is one TLS record. A larger write is split. `SslStream` may coalesce several small writes.
- Stapled OCSP and signed certificate timestamps are copied and not enforced.
- `NegotiateClientCertificateAsync` throws `NotSupportedException`. Post-handshake client authentication is not implemented.

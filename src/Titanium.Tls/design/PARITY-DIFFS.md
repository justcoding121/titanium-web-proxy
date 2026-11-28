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

`SslStream` on macOS, with the runtime on macmini, cannot enable TLS 1.3. `EnabledSslProtocols = SslProtocols.Tls13` throws `PlatformNotSupportedException`, and a request that also allows TLS 1.2 negotiates TLS 1.2. Kestrel on that machine therefore answers a TLS 1.3-only client with alert 70. The tests that need a platform TLS 1.3 peer are inconclusive there.

`TlsStream` matches those six rows on this PC: a transport end without `close_notify` throws `IOException`, read after dispose throws `ObjectDisposedException`, a zero-length write completes, a zero-length read waits, write after shutdown throws the `SslStream` message above, a cancelled read throws `OperationCanceledException`, and a concurrent write of 4 bytes is read by the peer. The same bytes also move against `SslStream` in both directions with a self-signed P-256 certificate and a validation callback that returns true.

Recorded differences:

- Revocation mode stays `NoCheck`. Online revocation is not hopped to another thread.
- The engine ClientHello is the built-in one (AES-128-GCM-SHA256, X25519 and P-256, `ecdsa_secp256r1_sha256`). `TlsStreamOptions.FingerprintProfile` is stored and `FingerprintFidelity` reports it. The profile is not the hello on the wire. With `TWP_MANAGED_TLS=1`, `UpstreamTlsTests.AcceptAnyServerCertificate_StillWorksOnTcp` gets a 502. The origin's `SslStream` reports that the peers have no common algorithm, because this hello offers only an ECDSA signature scheme.
- A write that fits in 2^14 bytes is one TLS record. A larger write is split. `SslStream` may coalesce several small writes.
- Stapled OCSP and signed certificate timestamps are copied and not enforced.
- `NegotiateClientCertificateAsync` throws `NotSupportedException`. Post-handshake client authentication is not implemented.

## OpenSSL 3.5.9

`s_server` and `s_client` from OpenSSL 3.5.9 complete the pairwise cells in T1.13. A ClientHello from this stack, the same hello truncated, retyped, limited to TLS 1.2, given a duplicated extension, or given an extension length past the message, and a zero-length application record, get the same first answer from this server and from `s_server`: a ServerHello, the same alert, or both sides still waiting on an incomplete record. The managed client is limited to AES-128-GCM-SHA256, an ECDSA P-256 peer certificate, and `ecdsa_secp256r1_sha256` client authentication. OpenSSL 3.5.5 sends `compress_certificate` (27) in CertificateRequest even when the ClientHello did not offer it. The client ignores that request and sends an uncompressed certificate. OpenSSL 3.5.9 does not send the extension. `www.microsoft.com` sends alert 40 to that offer. OpenSSL 3.5.9 with the same signature scheme, groups, and cipher gets the same alert. A normal OpenSSL client completes TLS 1.3 there with `rsa_pss_rsae_sha256` and X25519MLKEM768. P-256 against that client is a HelloRetryRequest. The managed server accepts the three TLS 1.3 ciphers, X25519, P-256, P-384, X25519MLKEM768, and RSA-2048 as well as the two ECDSA certificates. `s_server` is a terminal relay, so the transfer test reads stdout and writes the same bytes to stdin. Tickets do not resume across the two stacks. `s_client` key update (`k`/`K`) is not an oracle for `CLIENT_TRAFFIC_SECRET_1` and is not part of the matrix. The full cross product is a Slow test and was not run here. Kestrel on this PC accepts the managed client.

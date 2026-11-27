Phase 1 is a managed TLS 1.3 stack in `Titanium.Tls`. `TlsStream` follows `SslStream`. The proxy does not call it yet, and there is no proxy flag. Crypto is synchronous. The only planned P/Invoke is the later QUIC offload, which is not in this library. User-facing flags stay off. The managed stack fails closed while FIPS mode is on unless the caller sets `AllowManagedStackUnderFips` or `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1`.

## What was built

The client and server complete TLS 1.3, including HelloRetryRequest, certificates, CertificateVerify, Finished, NewSessionTicket, KeyUpdate, and `close_notify`. The client offers `TLS_AES_128_GCM_SHA256` and `ecdsa_secp256r1_sha256`. The server also accepts AES-256-GCM and ChaCha20-Poly1305, and it signs with the key it was given when the client offered that scheme. Key agreement is X25519, secp256r1, secp384r1, and X25519MLKEM768 when the platform has ML-KEM. `FipsApprovedOnly` drops X25519, the hybrid, and ChaCha20-Poly1305. A Chrome fingerprint profile is stored and reported. The bytes on the wire are still the engine's own ClientHello.

## Evidence

`TRACEABILITY.md` maps the RFC 8446, RFC 7748, and RFC 5869 rows that have tests. Negative rows cover a bad record, a downgrade sentinel, a HelloRetryRequest that does not change the share, an unsolicited extension, a missing key share, a wrong Finished, a truncated record, an oversized handshake, a second ChangeCipherSpec, and handshake bytes after `close_notify`.

The three-machine gate before these C7 fixes, Release, `TITANIUM_REQUIRE_ORACLE=1`, `TestCategory!=Slow`: this PC passed 202 and skipped 0, including `ScalarMultStaysUnderTheRegressionCeiling`. macmini passed 190 and skipped 12. linuxmini passed 201, then one assertion was corrected and that single test passed; the other 201 were not repeated. After the fixes, the same filter on this PC passed 211 and skipped 0 in 8 m 41 s. The nine new tests are in that 211. `src/Titanium.Web.Proxy.sln` builds in Release. `tools/run-tests.ps1 -Project unit` passed 3361, skipped 29, and failed 0. The real-peer sweep, the full interop matrix, the 30-minute soak, and the million-iteration X25519 test stay out of that filter. macmini and linuxmini have not been re-run on this tree.

Interop, on this PC, is the pairwise matrix in `PROGRESS.md` under T1.13, plus a 10 MiB transfer and one Kestrel HTTP/1.1 GET. OpenSSL tickets are not resumed. `www.microsoft.com` sends alert 40 to this client's offer, and OpenSSL 3.5.9 with the same offer does too.

Stryker 5.1.0, Standard, scored the thirteen handshake, codec, key schedule, and record files at 70.06 percent: 1880 killed, 109 timed out, 428 survived, and 422 with no coverage. One hundred of the ignored mutants are treated as equivalent: an unsigned shift of a non-negative value shares its statement with a killed left shift, a wipe of a temporary that is not read again, or a `return` whose following conditions cannot match. The survivors that stay are the same four groups. Peer parsers for a group id, a selected PSK index, and a ticket timestamp still see a zero high byte in this suite. `Dispose` and the wipes of `_psk` and `ticket.Psk` have no assertion after they run. `TlsEngine.Traffic.cs` is 42.34 percent, `TlsEngine.cs` is 63.69 percent, and `TlsEngine.Server.cs` is 65.34 percent. The score was not re-run after the C7 fixes.

## Benchmarks against the gates

Numbers are from T1.15 on this PC. No gate was lowered.

| Gate | Result |
| --- | --- |
| Full handshake at least 0.90 of the platform | 0.75 (4.545 ms against 3.405 ms) |
| Resumed handshake at least 1.20 | The platform's second connection was not faster than its first, so the row is not evidence |
| Loopback bulk at least 0.95 | 0.99 on the median, about 0.93 on the error-bar bound |
| X25519 at most 60 µs | Missed. The portable ladder stays |
| X25519 ceiling 900 µs | Passed on this PC in the gate run. An earlier run was 1069.7 µs. The ceiling stays 900 µs |
| Idle memory | The pre-fix measurement was 961 KB managed at 8,000 connections, mostly three 128 KiB buffers per endpoint. Those buffers are now allocated on the first byte. That measurement was not repeated, so the gate is not claimed |
| Handshake limiter | Unused. The soak cap is the test's |

macmini and linuxmini were not benchmarked. The async path was not measured. The handshake was not split into ladder, signature, and record time.

## Parity

Differences from `SslStream` and from OpenSSL are in `PARITY-DIFFS.md`. On this macOS build, `SslStream` cannot enable TLS 1.3, so those peer tests are inconclusive there. OpenSSL 3.5.5 sends `compress_certificate` on CertificateRequest when the client did not offer it. The client ignores that extension and sends an uncompressed certificate.

## C7

The review's blocker and seven majors are applied. Details and the regressions are in `SELF-REVIEW.md` and `BUG-LEDGER.md`. A required empty client certificate is alert 116. Resumption does not skip a client-certificate request. The validation callback sees the client certificate. Protected alerts from the reader are written under the write lock. ALPN must be exactly one offered name. A ClientHello that cannot hold its ticket or cookie is not truncated. The ticket cache is locked and bounded. Certificate entry 101 is `decode_error`.

## Known limitations

Online revocation runs on the caller thread. Stapled OCSP and SCTs are copied and not checked. The Chrome profile is not the engine hello. Cross-stack tickets do not resume. The client does not offer AES-256-GCM or the hybrid group. RSA between two managed endpoints fails with alert 40 because the client offers only `ecdsa_secp256r1_sha256`. ML-KEM decapsulation timing, an OpenSSL NIST derive sample, ACVP, the OpenSSL key-update oracle, and the Chrome QUIC Initial capture are still open.

## Open risks

The mutation score on `TlsEngine.Traffic.cs` is 42.34 percent. The idle-memory and handshake-rate gates are missed. The engine still has no handshake limiter. A proxy that accepts many sockets and calls `Authenticate` still allocates the receive buffer when it starts the read; the 128 KiB handshake buffers wait until a byte arrives, and that has not been re-measured.

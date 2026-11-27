> For operators and library users. This page describes the managed stack in this tree. The proxy does not call it yet.

Titanium.Tls is a TLS 1.3 implementation whose state machine is C#. Record protection, signatures, and the NIST curves use `System.Security.Cryptography`. X25519 is a managed ladder because it is not available through that API on every platform. Titanium.Quic is the QUIC library and does not carry traffic yet.

## Enabling it

Titanium Web Proxy still authenticates with `SslStream`. There is no managed-stack flag in this tree. Turning the stack on for the proxy, and the later Inspector default, belong to a later phase. Until then a process uses `TlsStream` only when that process constructs one.

`FipsEnvironment.IsEnabled` is true when Windows `FipsAlgorithmPolicy` is 1 or Linux `/proc/sys/crypto/fips_enabled` reads `1`. It is false on macOS. While that is true, `TlsStream` authentication throws `NotSupportedException` unless `TlsStreamOptions.AllowManagedStackUnderFips` is set or the environment variable `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE` is `1`. The override forces `TlsAlgorithmPolicy.FipsApprovedOnly`. This library is not a FIPS-validated module.

## Fingerprint

`TlsFingerprintProfiles.Chrome` is a measured ClientHello profile. `TlsStream` stores that profile and reports `TlsStream.FingerprintFidelity`. The bytes on the wire are still the engine's short hello, not the profile. Fidelity is `Full`, `ReducedNoPostQuantum` when ML-KEM is unavailable, or `ReducedFipsPolicy` when the FIPS algorithm set is in force. `ReducedFipsPolicy` drops ChaCha20-Poly1305, X25519, and the hybrid share from the profile's offer.

## Limits

The managed client offers only `TLS_AES_128_GCM_SHA256`, X25519 and P-256, and `ecdsa_secp256r1_sha256`. Under `FipsApprovedOnly` the client share is P-256 and the group offer is P-256 and P-384. The server can also select AES-256-GCM, ChaCha20-Poly1305, P-384, and X25519MLKEM768 when the peer offers them. An RSA certificate does not complete a managed-to-managed handshake, because the client accepts only the ECDSA P-256 scheme. TLS 1.2 is rejected. Stapled OCSP and SCT bytes are copied and not checked. Online revocation stays `NoCheck` on the caller thread. Tickets resume only inside this stack. Local timing is on [Performance Local Lab](Performance-Local-Lab).

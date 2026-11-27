The managed stack is a TLS 1.3 and QUIC v1 implementation that TWP and a standalone `HttpClient` can opt into. Crypto and protocol logic are synchronous and do not perform I/O. `TlsStream` and, later, the QUIC drivers are the only places that wait.

## Names

Public names follow the protocol and mirror the BCL type they stand in for. `TlsStream` stands in for `SslStream`. QUIC types keep a `Managed` prefix (`ManagedQuicConnection`, `ManagedQuicListener`, `ManagedQuicStream`) because `System.Net.Quic` already exports the plain names and TWP imports that namespace. A guard test fails if a public simple name collides with `System.Net.Security`, `System.Net.Quic`, `System.Net.Sockets`, or `System.Security.Authentication`.

`SslClientHelloInfo` has a public constructor, so the managed stack can construct it. The QUIC listener callback cannot, because it takes a sealed `QuicConnection`. That callback is `ManagedQuicConnectionOptionsCallback`.

## Ownership

`TlsEngine` will own the role, the handshake state, one `TlsKeySchedule`, one `TranscriptStore`, and two record directions. It is sans-IO: `Advance` takes bytes and returns bytes to send plus events. Events that need the caller (`ClientHelloReceived`, `ServerCertificateReceived`) pause the engine. The caller does the async work and then calls `Resume`. No cryptographic method returns a `Task` or `ValueTask`.

`TlsStream` owns the inner `Stream`, the pooled receive buffer, and the engine. Read and write run on the caller's continuation. There is no pipe, no channel, and no extra thread per connection.

Secrets live in `SecretBuffer` (pinned, zeroed on dispose). A stage transition in `TlsKeySchedule` zeroes the secret it consumes. Short-lived key material uses `stackalloc` and `CryptographicOperations.ZeroMemory`. Secrets are not stored in strings, lists, or async state machines.

## Threading

The engine is not thread-safe. `TlsStream` will serialize reads with one lock and writes with another, matching a full-duplex `SslStream`. Crypto runs on the caller. The only later exception is `CryptoExecutionPolicy` offload, which is not built yet.

## Fingerprints

A profile is data. `TlsFingerprintProfile` is the public abstract type. `ChromeFingerprintProfile` is the only profile this plan ships, and it is a thin subclass over an internal template. No code outside `Fingerprint/Profiles/Chrome*.cs` may mention Chrome. Adding another browser later is a template, captures, and tests. A template entry is either implemented or advertised-only. The engine aborts if a server selects an advertised-only item.

Chrome 158 captures (see `SPIKES.md`) fix the cipher list, the group list, a 32-byte session id, GREASE in the first and last extension slots, and an X25519MLKEM768 key share whose X25519 half is independent of the separate X25519 share. Extension slots between the GREASE values permute. Fresh profiles have no padding and no `pre_shared_key`.

## Record and transcript decisions already in the code

`TlsHkdf.ExpandLabel` uses one HMAC when the output fits in one hash block, and `HKDF.Expand` otherwise. `HKDF.Expand` allocates about 288 bytes on this PC; the HMAC path allocates nothing in Release.

`TranscriptStore` still rehashes the whole buffer. The next change replaces that with `IncrementalHash` and drops raw bytes once HelloRetryRequest is no longer possible and binders are done.

`TlsRecordProtector` throws on a bad record today. The engine-facing API will return a status and an alert. Only `TlsStream` turns a fatal status into an exception, so a peer that sends garbage cannot force an exception per record.

## Key exchange

X25519 is a managed 51-bit-limb ladder. Windows can do `curve25519` through `ECDiffieHellman`; macOS and Linux cannot (`PlatformNotSupportedException` in S1). The managed ladder stays the portable implementation. `IKeyExchangeGroup` can call the platform later where it exists. ML-KEM and SHA-3 are present on Windows and Linux and absent on macOS, so the managed ML-KEM and Keccak fallbacks are required, not optional.

## What is not decided here

QUIC packet layout, loss recovery, and the HTTP/3 client are specified when their phase starts. The Phase 2 kickoff writes that spec and shows it to the user before implementation.

> For operators and library users. This page describes the managed TLS and QUIC stacks in this tree, and when the proxy uses them.

Titanium.Tls is a TLS 1.3 implementation whose state machine is C#. Record protection, signatures, and the NIST curves use `System.Security.Cryptography`. X25519 is a managed ladder because it is not available through that API on every platform. Titanium.Quic is the QUIC library. The proxy calls either stack only when its flag is on. With both flags left at the default, authentication stays on `SslStream` and QUIC stays on the platform listener and connection types.

## Defaults

- A `ProxyServer` in the library leaves `UseManagedTlsStack` and `UseManagedQuicStack` false.
- `titanium run` leaves them false unless `--managed-stack` or `TITANIUM_MANAGED_STACK` says otherwise.
- The proxy and Gateway charts set `server.managedStack: false`.
- Inspector constructs a `ProxyServer` and does not set the flags, so it stays on the platform stack. A later change can turn Inspector on by default. That change waits on the throughput comparison against the platform stack.

Assigning either flag after `Start` throws. Reading the flags after `Start` is allowed.

## Turning it on

Precedence, highest first: `--managed-stack`, then `TITANIUM_MANAGED_STACK`, then the config file, then the product default. The environment variable accepts `1`, `true`, `0`, and `false`.

In config, `server.managedStack` sets both flags. `server.managedTls` and `server.managedQuic` override that umbrella. Omit all three and the library default stays in force.

In code, set the properties before `Start`:

```csharp
proxy.UseManagedTlsStack = true;
proxy.UseManagedQuicStack = true;
```

`new HttpClient(new TitaniumHttpHandler())` always uses the managed stacks. That handler has no flag. It is a separate package, `Titanium.Net.Http`.

## What needs the flag

Fingerprint evasion and terminating QUIC that arrives as raw datagrams need a stack this process owns. The platform TLS and QUIC APIs do not let a program choose the exact ClientHello, extension order, or QUIC Initial layout, and they do not expose raw UDP datagrams to a QUIC connection.

With the QUIC flag on:

- A QUIC datagram on a SOCKS5 UDP association is answered by the managed listener. The reply goes back to the client. A plain datagram to another destination stays on the relay.
- A QUIC datagram inside a CONNECT-UDP tunnel is answered the same way. A plain datagram stays on the socket.
- A transparently redirected datagram is answered for the origin the client addressed. The reply goes back to the caller. The listener does not open its own socket.

With the QUIC flag off, SOCKS5 UDP ASSOCIATE and CONNECT-UDP keep relaying. Transparent interception throws `ManagedStackRequiredException`. The message names `UseManagedQuicStack` and `TITANIUM_MANAGED_STACK`.

`clusters[].httpClient.fingerprint: chrome` asks for a browser ClientHello. The proxy reports an error when the managed TLS flag is off. While the bytes on the wire are still the engine's built-in hello, the handshake is refused instead of sending that hello.

An accepted HTTP/3 connection on the managed listener is closed. HTTP/3 framing on that path still reads the platform stream. `TitaniumHttpHandler` speaks HTTP/3 on its own managed connection.

## FIPS

`FipsEnvironment.IsEnabled` is true when Windows `FipsAlgorithmPolicy` is 1 or Linux `/proc/sys/crypto/fips_enabled` reads `1`. It is false on macOS. While that is true, the proxy refuses to start with the managed flags on unless `server.managedStackFipsOverride` is true or `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE` is `1`. `TlsStream` and `TitaniumHttpHandler` throw `NotSupportedException` unless `AllowManagedStackUnderFips` is set or that same variable is `1`. The override forces `TlsAlgorithmPolicy.FipsApprovedOnly`. This library is not a FIPS-validated module. Fingerprint fidelity is reduced under that policy.

## Fingerprint

`TlsFingerprintProfiles.Chrome` is a measured ClientHello profile. `TlsStream` stores that profile and reports `TlsStream.FingerprintFidelity`. The bytes on the wire are still the engine's short hello, not the profile. Fidelity is `Full`, `ReducedNoPostQuantum` when ML-KEM is unavailable, or `ReducedFipsPolicy` when the FIPS algorithm set is in force. `ReducedFipsPolicy` drops ChaCha20-Poly1305, X25519, and the hybrid share from the profile's offer.

## Limits

The managed client offers only `TLS_AES_128_GCM_SHA256`, X25519 and P-256, and `ecdsa_secp256r1_sha256`. Under `FipsApprovedOnly` the client share is P-256 and the group offer is P-256 and P-384. The server can also select AES-256-GCM, ChaCha20-Poly1305, P-384, and X25519MLKEM768 when the peer offers them. An RSA certificate does not complete a managed-to-managed handshake, because the client accepts only the ECDSA P-256 scheme. TLS 1.2 is rejected. Stapled OCSP and SCT bytes are copied and not checked. Online revocation stays `NoCheck` on the caller thread. Tickets resume only inside this stack. Local timing is on [Performance Local Lab](Performance-Local-Lab).

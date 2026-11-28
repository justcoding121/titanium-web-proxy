Phase 3 connects the proxy to the managed TLS and QUIC stacks without changing the flag-off path. This spec is the kickoff. It is the task list. The proxy still calls `SslStream` and `System.Net.Quic`. No existing proxy file is edited by the kickoff.

## Decisions this kickoff locks

These follow Part B, Part L, and the Phase 3 sequence. They are not new product choices.

- The first artifact is `TWP-TOUCH-MAP.md` in this folder. Creation sites are classified before any proxy edit.
- A later seam adds new files first. An existing file changes only at a creation site, and only by one flag branch. The flag-off branch keeps the current lines and the raw `SslStream` or `QuicConnection`.
- `UseManagedTlsStack` and `UseManagedQuicStack` default off for the library, the CLI, charts, and Gateway. Inspector defaults on, in its own later commit. This kickoff does not add the flags.
- A managed handshake that fails because the server chose TLS 1.2 falls back to `SslStream` for that connection. The fallback lives in the proxy, not in `TlsEngine`.
- HTTP/3 framing stays in the proxy until the extraction task. `TitaniumHttpHandler` waits until that extraction exists.
- 0-RTT stays out of this kickoff. The QUIC stack does not send or accept early data.
- FIPS stays fail-closed. An override is a later config task, not a silent switch.
- The compatibility project may adapt option types. It must not reference `Titanium.Web.Proxy`.

## Tasks

### P3.1 Touch map

Done with this kickoff. Every proxy file that constructs `SslStream`, `QuicConnection`, or `QuicListener`, or that calls a member those types own, is in the map. Comment-only mentions are listed as no seam.

Exit: the map exists, and `git diff` of `src/Titanium.Web.Proxy` is empty.

### P3.1b Option adapters

`QuicClientOptionsAdapter` and `QuicListenerOptionsAdapter` copy platform option objects onto the managed types. The client adapter copies an IP endpoint, the TLS options, the handshake timeout, the keep-alive interval, the idle timeout, both inbound stream limits, and the stream error used when a stream is released early. The managed client advertises those limits, sends a PING at the keep-alive interval after the handshake, and sends that stream error on an early release. The adapter still refuses a host name, a connection close code, a receive window, and a stream-capacity callback. The listener adapter copies the listen endpoint and refuses application protocols and a backlog other than the platform default. The platform listener has no handshake timeout, so the managed listener keeps its 10 second default. `QuicConnectionFactory` calls the client adapter after it resolves the host. `QuicClientHandler` calls the listener adapter. Application protocols stay on the server authentication options the callback returns.

Exit: the compatibility tests pass. A proxy-shaped client options object that already has an IP endpoint is accepted. A host name is still refused, and the caller resolves it.

### P3.2 Flag-off seam at the creation sites

`ProxyServer.UseManagedTlsStack` and `ProxyServer.UseManagedQuicStack` exist. Both default to false. Reading them after `Start` is allowed. Assigning a different value after `Start` throws. No creation site reads them yet, so a running proxy still constructs `SslStream` and `System.Net.Quic`.

`TlsStream` calls `LocalCertificateSelectionCallback` when the server requests a client certificate. The certificate it returns replaces `ClientCertificates`. Issuer names from the request use the same string as `X509Certificate2.Issuer`. `TcpConnectionFactory`, `ExplicitClientHandler`, both transparent client handshakes, both TLS-terminate handshakes, the MASQUE handshake, and the WebTransport client handshake read `UseManagedTlsStack`. The WebTransport origin handshake receives that flag from its caller. The mirror dispatcher reads it when a copy opens TLS. `QuicConnectionFactory` reads `UseManagedQuicStack`. When that flag is set, the factory resolves the host and connects a `ManagedQuicConnection`. The existing `QuicConnection.ConnectAsync` call stays on the other branch. The translated options leave the connection close code at the platform default, and the holder sends the HTTP/3 no-error code when it closes. A request stream on that connection still throws, because HTTP/3 framing reads the platform stream. The listener binds a `ManagedQuicListener` when `UseManagedQuicStack` is set and keeps HTTP/3 on the server authentication options. An accepted connection is closed, because the same framing gap applies. The existing `QuicListener.ListenAsync` call stays on the other branch. Each managed TLS branch authenticates a `TlsStream`. The existing `SslStream` lines stay in the other branch. `HttpStream` treats a `TlsStream` as a network stream for the body-write hook. Client-certificate capture reads `RemoteCertificate` from either stream. A terminate half-close calls `ShutdownAsync` on either stream. The other creation sites still construct the platform types.

Exit: existing proxy tests stay green and unedited. A flag-off run does not construct `TlsStream` or `ManagedQuicConnection`.

### P3.3 Flags, defaults, and the FIPS guard

`server.managedStack` sets both proxy flags. `server.managedTls` and `server.managedQuic` override it. Omit all three and the applier leaves the library default, which is off. `titanium run --managed-stack` and `TITANIUM_MANAGED_STACK` override the file. The flag wins over the variable. The proxy and gateway charts write `server.managedStack: false`. Startup refuses the managed stacks while FIPS mode is on unless `server.managedStackFipsOverride` or `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1` is set. With the override the handshake uses FIPS-approved algorithms and logs that fingerprint fidelity is reduced. `clusters[].httpClient.fingerprint: chrome` is refused unless the managed TLS flag is on. While the managed ClientHello is still the built-in one, the handshake is refused instead of sending that hello. Inspector is still ahead.

### P3.4 Managed-mode test run

`TWP_MANAGED_TLS=1` sets both proxy flags when a `ProxyServer` is constructed. Unset, the library default stays off. The soft workflow `managed-stack-proxy.yml` runs the existing unit and integration suites with that variable and does not fail the pull request. Failures are split into a managed bug, a parity gap in `PARITY-DIFFS.md`, or a test assumption. None are skipped silently.

### P3.5 Extract `Titanium.Http3`

`Http3VarInt`, the frame types, the stream types, the error codes, the frame byte layout, the capsule reader, the SETTINGS payload, the Huffman coding, the QPACK tables, and the QPACK encoder and decoder now live in `Titanium.Http3`. The proxy keeps the same names. It still reads frames and writes them to a stream, including the platform QUIC finish bit. The proxy behavior is unchanged.

### P3.6 `TitaniumHttpHandler`

`Titanium.Net.Http` contains `TitaniumHttpHandler`. HTTP/1.1 and HTTP/2 go through an inner handler whose connect callback owns the managed TLS stream. The handler rewrites `https` to `http` and keeps port 443, and the response points at the caller's request. A redirect is followed by the handler, so an https Location stays on the managed stream. Cookies and decompression stay on the inner handler. A one-mebibyte upload and download round-trip keeps its SHA-256. HTTP/3 opens a managed QUIC connection, sends the control streams, and reads one response. The same connection is reused for that authority until 16 request streams, the idle timeout, or the connection lifetime. The call returns after the response headers, and the body is read as the caller reads it. Cancelling that read stops it while it is waiting for the next frame. A GOAWAY on the peer's control stream stops new requests on that connection. The dynamic table is capped at 4096 bytes. Requests use the static table, and a peer insert is decoded. When Alt-Svc is enabled, a later request that allows a higher version uses HTTP/3 after `Alt-Svc: h3` from the same origin. The cache holds 256 origins and honors `ma`. The handler always uses the managed stack.

### P3.7 UDP injection

A managed listener can take one datagram from the caller and hand each reply to a callback. It does not open a socket. When the managed QUIC stack is on, SOCKS5 UDP ASSOCIATE, CONNECT-UDP, and transparent interception feed a QUIC datagram to that listener. The transparent local address is the origin the client addressed. The reply goes back to the caller. Other datagrams stay on the relay. The platform path stays the default.

## Self-review

Checked against Part B and Part O. The kickoff does not add a pipe, a channel, a `Task` around crypto, a P/Invoke, or a second ClientHello parser. Listener fallback keeps the proxy's existing ClientHello parse. CUBIC stays off by default. The UDP offload project stays optional and is not wired here. Public QUIC names stay `Managed*`. The flag-off hot path is not wrapped.

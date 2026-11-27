This map is the survey for the Phase 3 seam. `ProxyServer.UseManagedTlsStack` and `ProxyServer.UseManagedQuicStack` default to false. `TcpConnectionFactory`, `ExplicitClientHandler`, `TransparentClientHandler`, and `TcpTlsTerminateHandler` read `UseManagedTlsStack`.

A file is a creation site when it constructs `SslStream`, calls `QuicConnection.ConnectAsync`, or calls `QuicListener.ListenAsync`. A use site calls a member those types own on an object it did not construct. A capability site branches on the runtime type. Comment-only mentions are not a seam.

## Creation sites

These are the only places a later flag branch belongs. The flag-off arm stays the lines that are there now.

| File | What it creates |
| --- | --- |
| `Network/TcpConnection/TcpConnectionFactory.cs` | Upstream handshake. When `UseManagedTlsStack` is set, both sites authenticate a `TlsStream`. The other branch still constructs `SslStream`. |
| `Handlers/ExplicitClientHandler.cs` | Client-facing handshake. When `UseManagedTlsStack` is set, the site authenticates a `TlsStream`. The other branch still constructs `SslStream`. |
| `Handlers/TransparentClientHandler.cs` | Client-facing handshake. When `UseManagedTlsStack` is set, both sites authenticate a `TlsStream`, including the per-name certificate callback. The other branch still constructs `SslStream`. |
| `Handlers/TcpTlsTerminateHandler.cs` | Client-facing and upstream handshakes. When `UseManagedTlsStack` is set, both authenticate a `TlsStream`. The other branch still constructs `SslStream`. |
| `Handlers/MasqueClientHandler.cs` | Client-facing `SslStream` around line 45. |
| `Handlers/WebTransportClientHandler.cs` | Client-facing `SslStream` around line 43. |
| `WebTransport/WebTransportOrigin.cs` | Upstream `SslStream` around line 31. |
| `Routing/MirrorDispatcher.cs` | Upstream `SslStream` around line 343. |
| `Network/Quic/QuicConnectionFactory.cs` | `QuicConnection.ConnectAsync` around line 112. The remote endpoint is a `DnsEndPoint`, which `QuicClientOptionsAdapter` still refuses. The keepalive, the three inbound unidirectional streams, and the stream error code are copied onto the managed client. |
| `Handlers/QuicClientHandler.cs` | `QuicListener.ListenAsync` around line 144, and `QuicServerConnectionOptions` around line 265. The listener sets application protocols. `QuicListenerOptionsAdapter` refuses that field and tells the callback to carry them on the server authentication options. |

## Capability sites

These branch on the platform type. A managed stream has to opt in without changing the flag-off object.

| File | Check |
| --- | --- |
| `Network/Streams/HttpStream.cs` | `baseStream is SslStream` sets `IsNetworkStream` and `SupportsBodyWriteHook` (lines 106 and 109). `ITransportCapableStream` already documents that hook. |
| `Handlers/TcpTlsTerminateHandler.cs` | A half-close calls `ShutdownAsync` on an `SslStream` or a `TlsStream`. |
| `Http3/Http3Frame.cs` | `stream is QuicStream` passes `completeWrites: true` (line 436). |
| `Certificates/ClientCertificateHandshake.cs` | `Capture` reads `RemoteCertificate`. The `SslStream` overload forwards to an overload that takes the certificate, which the managed handshake calls. |

## Use sites

These already hold a platform object. They should keep taking `Stream` or the existing proxy wrapper. They are not creation sites.

| File | Members it uses |
| --- | --- |
| `Network/Quic/QuicServerConnection.cs` | `OpenOutboundStreamAsync` on a connection the factory returned. |
| `Http3/Http3Connection.cs` | `OpenOutboundStreamAsync` and `AcceptInboundStreamAsync`. Stores `QuicConnection`. |
| `Http3/Http3OriginClientSession.cs` | Same pair of stream calls. Stores `QuicConnection`. |
| `Http3/Http3RequestStream.cs` | Reads and writes a `QuicStream`, including the write that finishes the stream. |
| `Http3/Http3TunnelStream.cs` | Holds a `QuicStream`. |
| `Http3/Http3RequestBodyForwarder.cs` | Copies bytes onto a `QuicStream`. |
| `Http3/Http3OriginBridge.Quic.cs` | Writes a `QuicStream` and calls `Flush`. |
| `Http3/Http3OriginBridge.Stall.cs` | Reads a `QuicStream`. |
| `Http3/Http3ExtendedConnectTunnel.cs` | Holds a `QuicStream`. |
| `Http3/Http3ConnectUdp.cs` | Reads and writes a `QuicStream`. |
| `Models/TransparentProxyEndPoint.cs` | Stores the `QuicListener` that `QuicClientHandler` created. |
| `Models/TransparentQuicProxyEndPoint.cs` | Same storage, and it checks `QuicListener.IsSupported`. |
| `Network/Quic/IQuicInboundEndPoint.cs` | The listener properties the endpoint implements. |

## No seam

`QuicClientConnection` records endpoints and the negotiated protocol. It does not hold a `QuicConnection`. `IQuicConnectionFactory` returns the proxy's `QuicServerConnection` wrapper. `QuicConnectionPool` only mentions `OpenOutboundStreamAsync` in a comment.

These files mention `SslStream` or `QuicListener` in comments or XML docs and do not construct one: `ProxyServer.cs`, `ProxyServer.ListenerTls.cs`, `Network/TcpConnection/TcpClientConnection.cs`, `Network/UpstreamTlsMaterial.cs`, `Network/Tcp/DecryptFailureLearning.cs`, `Network/Streams/HttpStream.Write.cs`, `Network/Streams/HttpStream.Fill.cs`, `Network/Streams/HttpStream.Body.cs`, `Http3/Http3OriginBridge.Tcp.cs`, `Http3/Http3NativeBootstrap.cs`, `Http2/Http2FrameWriter.cs`, `Handlers/Http2NegotiationHandler.cs`, `Handlers/CertificateHandler.cs`, `Certificates/CertificateManager.cs`, `Logging/ProxyDiagnostics.cs`, `Extensions/SslExtensions.cs`.

The plan's candidate list also names `RequestHandler` and `Http2ToHttp3BridgeHandler`. Neither constructs `SslStream` or a QUIC connection.

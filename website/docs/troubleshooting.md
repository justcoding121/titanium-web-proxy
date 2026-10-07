# Why did my request fail

> Start with the Error log. A limit rejection names the property, the configured value, the observed value, and the CLI key. See [Limits and escape hatches](/docs/limits).

Show logs with `titanium run -c twp.yaml -v`, or set `logging.minimumLevel` below Error. The default Error level already includes Enforce rejections. Observe lines need Debug.

## Status codes

| Code | Usual cause | What to change |
|------|-------------|----------------|
| 400 | Malformed HTTP/1 framing (obs-fold, a header line with no colon) | Fix the client. This is not a limit. |
| 413 | Buffered body exceeded `maxBufferedBodyBytes` | Raise `server.limits.maxBufferedBodyBytes`, or stop materializing the body and stream it. |
| 431 | Request header count or header bytes exceeded the header limits | Raise `server.limits.maxHeaderCount` or `server.limits.maxHeaderAggregateBytes`. |
| 502 | Origin headers, or the origin could not be reached (DNS failure, refused, reset, TLS failure) | For headers: `server.limits.maxDecodedHeaderListBytes` or the HTTP/1 header keys. The 502 body names the host and the reason; an unreachable origin is not a proxy setting. |
| 503 | Admission: too many client connections | Raise `server.pooling.maxConcurrentClientConnections` or `server.limits.maxConcurrentClients`. |
| 504 | A timeout fired | The log names the `server.timeouts.*` key and the configured value (`connectTimeOutSeconds`, `responseHeaderTimeoutSeconds`, `idleReadTimeoutSeconds`, `idleWriteTimeoutSeconds`, `requestTimeoutSeconds`, `clientHeaderTimeoutSeconds`). A connect or response timeout before any response byte is answered with 504, not a silent close. |

414 and 421 are not limit codes.

## Resets and protocol errors

| Symptom | Meaning | Key |
|---------|---------|-----|
| Connection reset while headers are large | HTTP/2 compressed header block or decoded list | `server.limits.maxHttp2CompressedHeaderBlockBytes`, `server.limits.maxDecodedHeaderListBytes` |
| `RST_STREAM` / `ENHANCE_YOUR_CALM` | Parked HTTP/2 DATA exceeded the per-stream queue | `server.limits.maxDeferredOutboundBytesPerStream` |
| HTTP/2 stream wait, then reset | Flow-control window was not updated in time | `server.limits.http2WindowUpdateTimeoutSeconds` |
| HTTP/3 `ExcessiveLoad` | A frame declared more than `maxHttp3FramePayloadBytes` | Raise that key (ceiling 64 MiB). DATA with no body hook streams past the cap; headers and hooks still need the whole frame. |
| HTTP/3 `FrameError` | The frame ended before the declared payload | The peer closed early. Not a limit. |
| WebSocket close 1009 | One frame exceeded `maxWebSocketFramePayloadBytes` | `server.limits.maxWebSocketFramePayloadBytes` |
| WebSocket close 1002 | Protocol error (bad opcode, fragmented control frame) | Fix the peer. |
| 401 / 407 after a few challenges | Auth rounds, upstream proxy attempts, or the Windows token size | `server.limits.maxAuthChallengeRounds`, `maxUpstreamProxyAuthenticationAttempts`, `maxWinAuthTokenBytes` |
| Many `103` / `100` then a failure | Interim responses | `server.limits.maxInterimResponses` |

PublicFacing's 60 s idle and 120 s request deadlines close SSE, gRPC streams, and WebSockets. Raise those timeouts or use Balanced when the client is trusted.

Decrypt-bypass learning is a Warning, not an Error: the current request was not failed. The line names `server.enableDecryptFailureBypass`. Later CONNECTs to that host tunnel without decryption until the TTL expires.

A client that rejects the Titanium certificate (pinned apps, or a client that does not trust the root) is cut off after a few handshake failures per host (`ProxyServer.ClientHandshakeRejectThreshold` within `ProxyServer.ClientHandshakeRejectWindow`), and that host is tunnelled without decryption for the learned-bypass TTL. The log shows one Warning naming the host and one throttled Debug line per host. Install the root CA in the client to decrypt that host again.

If several `Titanium Root Certificate Authority` roots are in the trust store, new roots carry a Subject Key Identifier and their leaves an Authority Key Identifier, so chains pick the right root. In the Inspector, **Remove old root CAs…** removes every one except the current root.

A CONNECT tunnel is answered with `200` before the origin is dialed, so an unreachable origin shows up in the browser as a closed connection. Set `EstablishServerConnectionBeforeResponse` in `BeforeTunnelConnectRequest` to dial first and answer `502`/`504` instead. The Inspector does this for opaque (non-decrypted) tunnels, and the dialed connection is reused for the tunnel.

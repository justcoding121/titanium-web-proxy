> Start with the Error log. A limit rejection names the property, the configured value, the observed value, and the CLI key. The key list is [Limits and escape hatches](Limits-and-Escape-Hatches).

Show logs with `titanium run -c twp.yaml -v`, or set `logging.minimumLevel`. Enforce rejections are already Error.

| Code | Usual cause | What to change |
|------|-------------|----------------|
| 400 | Malformed HTTP/1 framing | Fix the client. Not a limit. |
| 413 | Buffered body over `maxBufferedBodyBytes` | Raise `server.limits.maxBufferedBodyBytes`, or stream the body. |
| 431 | Request headers over the count or byte cap | `server.limits.maxHeaderCount` or `server.limits.maxHeaderAggregateBytes`. |
| 502 | Origin headers, or the origin could not be reached (DNS failure, refused, reset, TLS failure) | For headers: `server.limits.maxDecodedHeaderListBytes` or the HTTP/1 header keys. The 502 body names the host and the reason; an unreachable origin is not a proxy setting. |
| 503 | Too many client connections | `server.pooling.maxConcurrentClientConnections` or `server.limits.maxConcurrentClients`. |
| 504 | A timeout | The log names the `server.timeouts.*` key and the configured value. A connect or response timeout before any response byte is answered with 504, not a silent close. |

414 and 421 are not limit codes.

| Symptom | Key |
|---------|-----|
| HTTP/2 header GOAWAY or stream failure | `server.limits.maxHttp2CompressedHeaderBlockBytes`, `server.limits.maxDecodedHeaderListBytes` |
| `RST_STREAM` / `ENHANCE_YOUR_CALM` | `server.limits.maxDeferredOutboundBytesPerStream` |
| HTTP/2 flow-control wait | `server.limits.http2WindowUpdateTimeoutSeconds` |
| HTTP/3 `ExcessiveLoad` | `server.limits.maxHttp3FramePayloadBytes` |
| WebSocket 1009 | `server.limits.maxWebSocketFramePayloadBytes` |
| WebSocket 1002 | Protocol error. Fix the peer. |
| 401 / 407 after a few rounds | `server.limits.maxAuthChallengeRounds`, `maxUpstreamProxyAuthenticationAttempts`, `maxWinAuthTokenBytes` |

PublicFacing's 60 s idle and 120 s request deadlines close SSE, gRPC streams, and WebSockets. Decrypt-bypass learning is a Warning (`server.enableDecryptFailureBypass`): the current request was not failed.

A client that rejects the Titanium certificate (pinned apps, or a client that does not trust the root) is cut off after a few handshake failures per host (`ProxyServer.ClientHandshakeRejectThreshold` within `ProxyServer.ClientHandshakeRejectWindow`), and that host is tunnelled without decryption for the learned-bypass TTL. The log shows one Warning naming the host and one Debug line per abort. Install the root CA in the client to decrypt that host again.

A CONNECT tunnel is answered with `200` before the origin is dialed, so an unreachable origin shows up in the browser as a closed connection. Set `EstablishServerConnectionBeforeResponse` in `BeforeTunnelConnectRequest` to dial first and answer `502`/`504` instead. The Inspector does this for opaque (non-decrypted) tunnels, and the dialed connection is reused for the tunnel. Expected failures (DNS miss, reset, cancel) log one Debug line; the stack is kept at Trace.

If an origin closes the connection before sending its declared `Content-Length` bytes, the proxy does not rewrite the response as complete. It closes the client connection (a 502 if no response byte was sent yet, or `RST_STREAM` on HTTP/2 once headers are out), so the browser reports a failed load instead of keeping a short body.

If several `Titanium Root Certificate Authority` roots are in the trust store, new roots now carry a Subject Key Identifier and their leaves an Authority Key Identifier, so chains pick the right root. In the Inspector, **Remove old root CAs…** removes every one except the current root.

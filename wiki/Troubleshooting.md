> Start with the Error log. A limit rejection names the property, the configured value, the observed value, and the CLI key. The key list is [Limits and escape hatches](Limits-and-Escape-Hatches).

Show logs with `titanium run -c twp.yaml -v`, or set `logging.minimumLevel`. Enforce rejections are already Error.

| Code | Usual cause | What to change |
|------|-------------|----------------|
| 400 | Malformed HTTP/1 framing | Fix the client. Not a limit. |
| 413 | Buffered body over `maxBufferedBodyBytes` | Raise `server.limits.maxBufferedBodyBytes`, or stream the body. |
| 431 | Request headers over the count or byte cap | `server.limits.maxHeaderCount` or `server.limits.maxHeaderAggregateBytes`. |
| 502 | Origin headers or an origin failure | `server.limits.maxDecodedHeaderListBytes` or the HTTP/1 header keys. The log says which. |
| 503 | Too many client connections | `server.pooling.maxConcurrentClientConnections` or `server.limits.maxConcurrentClients`. |
| 504 | A timeout | The log names the `server.timeouts.*` key. |

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

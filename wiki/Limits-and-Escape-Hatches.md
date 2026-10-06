> For CLI reverse-proxy operators and library users. The website copy is [Limits and escape hatches](https://titaniumproxy.com/docs/limits).

The default log level is Error. Run `titanium run -c twp.yaml -v` or set `logging.minimumLevel` when you need Observe-mode lines (Debug). An Enforce rejection is an Error, so it shows up without `-v`.

## Behavior changes at the previous defaults

1. Origin HTTP/2 responses whose headers are between 8 KiB and 64 KiB now arrive intact. Above 64 KiB the stream fails with 502 and names `server.limits.maxDecodedHeaderListBytes`.
2. Under Balanced, an HTTP/1 request or origin response with more than 256 headers or 256 KiB of header bytes is rejected: 431 for a request, 502 for an origin response. LegacyCompatible keeps HeaderLimits at Observe.
3. Limit-driven failures appear in the default Error log, with the property, the configured limit, the observed value, and the CLI key.

## Policy modes and profiles

`server.policyModes.<family>` is `Enforce` (reject, Error), `Observe` (pass, Debug), or `Disabled` (skip). A partial block changes only the keys you set. PublicFacing keeps `http1ReplaySafety` at Enforce unless you override it.

Balanced (default) enforces header limits and allows 1000 concurrent streams, with timeouts disabled. PublicFacing caps streams at 256 and sets idle timeouts to 60 s and the request timeout to 120 s. Those deadlines cut SSE, gRPC streaming, and WebSocket. LegacyCompatible observes header limits.

`maxBufferedBodyBytes` applies when a body is materialized. Streaming relays do not use it. `CanBufferBody` reports whether a message fits. `webSocketFrameBudget` is the per-frame WebSocket cap. `maxEncodedBodyBytes`, `maxDecodedBodyBytes`, and `maxDecompressionRatio` are reserved and not enforced; startup warns when they are set. Expansion is bounded by `maxBufferedBodyBytes`.

## Keys

| What fails | Default | CLI key |
|------------|---------|---------|
| Buffered body | 4 MiB | `server.limits.maxBufferedBodyBytes` |
| Header count | 256 | `server.limits.maxHeaderCount` |
| Header block size | 256 KiB | `server.limits.maxHeaderAggregateBytes` |
| Header line | 64 KiB | `server.limits.maxHeaderLineBytes` |
| HTTP/3 frame payload | 4 MiB (ceiling 64 MiB) | `server.limits.maxHttp3FramePayloadBytes` |
| HTTP/2 deferred DATA per stream | 4 MiB + 16 KiB (ceiling 64 MiB) | `server.limits.maxDeferredOutboundBytesPerStream` |
| Decoded header list | 64 KiB | `server.limits.maxDecodedHeaderListBytes` |
| Trailer count / block | 100 / 16 KiB | `server.limits.maxTrailerHeaderCount`, `server.limits.maxTrailerHeaderBlockBytes` |
| Compressed HTTP/2 header block | 256 KiB | `server.limits.maxHttp2CompressedHeaderBlockBytes` |
| Interim responses | 20 | `server.limits.maxInterimResponses` |
| Auth rounds / upstream attempts / token | 3 / 5 / 12288 | `server.limits.maxAuthChallengeRounds`, `server.limits.maxUpstreamProxyAuthenticationAttempts`, `server.limits.maxWinAuthTokenBytes` |
| HTTP/2 flow-control wait | 60 s | `server.limits.http2WindowUpdateTimeoutSeconds` |
| Client connections | unset | `server.pooling.maxConcurrentClientConnections`, `server.limits.maxConcurrentClients` |
| Timeouts | 0 (off) unless PublicFacing | `server.timeouts.connectTimeOutSeconds`, `server.timeouts.responseHeaderTimeoutSeconds`, `server.timeouts.idleReadTimeoutSeconds`, `server.timeouts.idleWriteTimeoutSeconds`, `server.timeouts.requestTimeoutSeconds`, `server.timeouts.clientHeaderTimeoutSeconds` |
| WebSocket frame | 16 MiB | `server.limits.maxWebSocketFramePayloadBytes` |

`routes[].limits` can set `requestTimeoutSeconds`, `idleTimeoutSeconds`, `maxBufferedBodyBytes`, and `maxWebSocketFramePayloadBytes` for one route. A missing block does not touch the session.

HTTP/3 rents at most 256 KiB until payload bytes arrive. Worst-case parked HTTP/2 DATA is concurrent streams times `maxDeferredOutboundBytesPerStream`.

Fixed on purpose: HTTP/2 max frame 16384, HTTP/3 control frames 16 KiB, QPACK ack capacity, the 1 GiB chunk-size guard, SOCKS hostnames of 255 bytes. 431 is a real header-limit status. 414 and 421 are not limit codes.

Misspelled keys are warned at startup and on reload. `titanium test --strict` exits 1 when any unknown key is present.

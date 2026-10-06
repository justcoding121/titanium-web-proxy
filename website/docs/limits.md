# Limits and escape hatches

> For CLI reverse-proxy operators and library users. Every limit that can reject a legitimate request has a key. A rejection is logged at Error with the limit, the observed value, and the key that raises it.

The default log level is Error. Run `titanium run -c twp.yaml -v` or set `logging.minimumLevel` to `Debug` or `Information` when you need Observe-mode lines. Observe logs at Debug. A rejected request logs at Error so it shows up without `-v`.

## Behavior changes at the previous defaults

1. Origin HTTP/2 responses whose headers are between 8 KiB and 64 KiB now arrive intact. Above 64 KiB the stream fails with 502 and names `server.limits.maxDecodedHeaderListBytes`. Previously those headers were truncated in silence.
2. Under Balanced (the default profile), an HTTP/1 request or origin response with more than 256 headers or 256 KiB of header bytes is rejected: 431 for a request, 502 for an origin response. LegacyCompatible keeps HeaderLimits at Observe, so the same message is logged and passed.
3. Limit-driven failures appear in the default Error log. The line names the property, the configured limit, the observed value, and the CLI key.

## How a rejection looks

```text
headerLimits (Enforce): header count exceeded MaxHeaderCount=256; observed 300; client got 431. Raise server.limits.maxHeaderCount or set server.policyModes.headerLimits to observe or disabled. host cdn.example
```

The first line for a limit is immediate. Further hits of that same limit are folded into one line per 10 seconds, with a suppressed-count suffix. Metrics count every rejection. Logs never include URLs, header values, cookies, or bodies. The host name is the only request identity on the line.

## Policy modes

`server.policyModes.<family>` is `Enforce`, `Observe`, or `Disabled`.

| Mode | Effect |
|------|--------|
| Enforce | Reject the request and log at Error. |
| Observe | Let the request through and log at Debug. |
| Disabled | Skip the check. |

Families: `bodyBudget`, `decompressionRatio`, `headerLimits`, `admissionControl`, `http2AbuseBudget`, `http2RelayValidation`, `webSocketFrameBudget`, `http1ReplaySafety`. `allowAmbiguousFraming` is a separate boolean. A partial `policyModes` block changes only the keys you set. Under PublicFacing, leaving `http1ReplaySafety` unset keeps Enforce.

## Buffering and streaming

`maxBufferedBodyBytes` applies when something materializes a whole body (`GetRequestBody`, `GetResponseBody`, a hook that needs the bytes). A streaming relay does not buffer the body and does not consult that cap. `CanBufferBody` reports whether a message is small enough to buffer. `webSocketFrameBudget` is the per-frame WebSocket cap (`maxWebSocketFramePayloadBytes`); a frame over it closes with 1009. Filtering hooks still see a whole frame. The raw relay (no subscriber) does not decode frames.

`maxEncodedBodyBytes`, `maxDecodedBodyBytes`, and `maxDecompressionRatio` are accepted and **reserved, not enforced**. Body expansion is bounded by `maxBufferedBodyBytes`. Startup warns when you set them.

## Profiles

| Profile | Header limits | Streams / connection | Idle / request timeout | Who it is for |
|---------|---------------|----------------------|------------------------|---------------|
| Balanced (default) | Enforce, 256 headers / 256 KiB | 1000 | disabled (0) | Single-user and trusted clients |
| LegacyCompatible | Observe | 1000 | disabled | 4.x migrations |
| PublicFacing | Enforce | 256 | idle 60 s, request 120 s | Untrusted clients |

PublicFacing idle and request deadlines cut long-lived SSE, gRPC streaming, and WebSocket sessions. Raise `server.timeouts.idleReadTimeoutSeconds`, `idleWriteTimeoutSeconds`, and `requestTimeoutSeconds`, or set them to 0, when those protocols must stay open. 256 concurrent streams is the PublicFacing cap, not the Balanced default.

## Reference

| What fails | Default | Ceiling | CLI key |
|------------|---------|---------|---------|
| Buffered body | 4 MiB | — | `server.limits.maxBufferedBodyBytes` |
| Header count | 256 | — | `server.limits.maxHeaderCount` |
| Header block size | 256 KiB | — | `server.limits.maxHeaderAggregateBytes` |
| Header line | 64 KiB | — | `server.limits.maxHeaderLineBytes` |
| HTTP/3 frame payload | 4 MiB | 64 MiB | `server.limits.maxHttp3FramePayloadBytes` |
| HTTP/2 deferred DATA per stream | 4 MiB + 16 KiB | 64 MiB | `server.limits.maxDeferredOutboundBytesPerStream` |
| Decoded header list | 64 KiB | — | `server.limits.maxDecodedHeaderListBytes` |
| Trailer count | 100 | 10000 | `server.limits.maxTrailerHeaderCount` |
| Trailer block | 16 KiB | 1 MiB | `server.limits.maxTrailerHeaderBlockBytes` |
| Compressed HTTP/2 header block | 256 KiB | 16 MiB | `server.limits.maxHttp2CompressedHeaderBlockBytes` |
| Interim 1xx responses | 20 | 100 | `server.limits.maxInterimResponses` |
| Auth challenge rounds | 3 | 20 | `server.limits.maxAuthChallengeRounds` |
| Upstream proxy auth attempts | 5 | 20 | `server.limits.maxUpstreamProxyAuthenticationAttempts` |
| Windows auth token | 12288 bytes | 1 MiB | `server.limits.maxWinAuthTokenBytes` |
| HTTP/2 flow-control wait | 60 s | 3600 s | `server.limits.http2WindowUpdateTimeoutSeconds` |
| Client connections (process) | unset | — | `server.pooling.maxConcurrentClientConnections` |
| Client connections (endpoint) | unset | — | `server.limits.maxConcurrentClients` |
| Connect timeout | 0 (off) | — | `server.timeouts.connectTimeOutSeconds` |
| Response header timeout | 0 (off) | — | `server.timeouts.responseHeaderTimeoutSeconds` |
| Idle read timeout | 0 (off) | — | `server.timeouts.idleReadTimeoutSeconds` |
| Idle write timeout | 0 (off) | — | `server.timeouts.idleWriteTimeoutSeconds` |
| Request timeout | 0 (off) | — | `server.timeouts.requestTimeoutSeconds` |
| Client header timeout | 0 (off) | — | `server.timeouts.clientHeaderTimeoutSeconds` |
| WebSocket frame | 16 MiB | — | `server.limits.maxWebSocketFramePayloadBytes` |

Per-route overrides (request timeout, idle timeout, buffered body, WebSocket frame) live on `routes[].limits` and apply only to the matched session. Omit the block and the server limits are unchanged.

## Memory

HTTP/3 frame reads rent at most 256 KiB until bytes arrive, then grow. A peer that declares a huge length and sends nothing cannot reserve the ceiling times the stream count. Worst case for parked HTTP/2 DATA is concurrent streams times `maxDeferredOutboundBytesPerStream`.

## Fixed on purpose

These stay constant. They are protocol limits or anti-wrap guards, not traffic you should raise:

- HTTP/2 `MAX_FRAME_SIZE` we accept is 16384. We do not advertise a larger value.
- HTTP/3 control-stream frames are 16 KiB.
- QPACK acknowledgement capacity.
- The 1 GiB chunk-size guard (stops a wrapped chunk length).
- SOCKS hostnames are 255 bytes (RFC 1928).

414 and 421 are not produced by these limits. 431 is the request header-count or header-block rejection. 413 is the buffered-body rejection.

## See also

- [Why did my request fail](/docs/troubleshooting)
- [Configuration](/docs/configuration)
- [WebSocket large messages (design)](/docs/websocket-large-messages)

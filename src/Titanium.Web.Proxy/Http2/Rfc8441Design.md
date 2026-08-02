# RFC 8441 Extended CONNECT / WebSocket over HTTP/2

## Scope

Demand-driven compatibility: Chromium and Firefox can use RFC 8441 only after receiving
`SETTINGS_ENABLE_CONNECT_PROTOCOL=1` from the server. HTTP/1.1 fallback remains common.
Safari does not support RFC 8441 (as of 2026).

## Design decisions

### SETTINGS_ENABLE_CONNECT_PROTOCOL (setting ID 8)

The proxy manages `ENABLE_CONNECT_PROTOCOL` independently per leg:

- **Client leg**: the proxy advertises `ENABLE_CONNECT_PROTOCOL=1` to the client if and only if
  `ProxyServer.EnableRfc8441 = true`. The client's own preference (if it sends the setting) is
  suppressed from the frame before it is relayed to the origin.
- **Origin leg**: the proxy relays the origin's setting to the client (with the value overwritten to
  `1` when `EnableRfc8441=true` or to `0` otherwise) and records the origin's raw value in
  `Http2Settings.EnableConnectProtocol`. If the origin sends `SETTINGS_ENABLE_CONNECT_PROTOCOL`
  with a value other than 0 or 1, the proxy responds with `GOAWAY(PROTOCOL_ERROR)`. If the origin
  sends 0 after previously sending 1 (the RFC 8441 §3 forbidden 1→0 downgrade), the proxy also
  responds with `GOAWAY(PROTOCOL_ERROR)`.
- Neither leg's `ENABLE_CONNECT_PROTOCOL` value is forwarded to the other leg.
- `Http2ConnectionState.ServerSettingsRelayed` is awaited before the proxy checks the origin's
  `EnableConnectProtocol` value when processing a client extended CONNECT request, eliminating the
  race between the client sending HEADERS and the origin's initial SETTINGS being processed.

### Extended CONNECT pseudo-header validation (RFC 8441 §5)

An extended CONNECT request must have:
- `:method = CONNECT`
- `:protocol` (non-empty; identifies the application protocol, e.g. `websocket`)
- `:scheme` (`http` or `https` per RFC 8441 §5; NOT `ws`/`wss`)
- `:path`
- `:authority` (host and port)

A plain CONNECT requires exactly `:method` and `:authority` with no `:scheme`, `:path`, or `:protocol`.
Mixing the two forms is a stream-level `PROTOCOL_ERROR`.

`MyHeaderListener` enforces:
- No pseudo-header after a regular header field (connection error).
- No duplicate pseudo-headers, including duplicate `:protocol` (connection error).
- `:status` only on responses; request pseudo-fields only on requests (connection error).
- Response `:status` must be exactly three ASCII decimal digits in the range 100–999;
  malformed status is a stream-level `PROTOCOL_ERROR`.
- Extended CONNECT: `:protocol`, `:scheme`, `:path`, and `:authority` must all be present and non-empty.

### Header forwarding

For native h2↔h2 extended CONNECT, `SendHeader` emits pseudo-headers in the required order:
`:method`, `:authority`, `:scheme`, `:path`, then `:protocol`. The `:authority` value is taken from
the preserved `Request.Authority` rather than reconstructed through `RequestUri.Authority`, avoiding
port normalization side-effects.

`Request.ExtendedConnectProtocol` (public getter, internal setter) stores the `:protocol` value and
is populated from `MyHeaderListener.Protocol` in `ProcessCompleteHeaderBlockAsync`.
`Request.UpgradeToWebSocket` returns `true` for both RFC 8441 (`CONNECT :protocol=websocket`) and
HTTP/1.1 (`Upgrade: websocket`) requests.

### Translation matrix

| Client protocol | Origin protocol | Proxy behavior |
|---|---|---|
| HTTP/2 extended CONNECT | HTTP/2 extended CONNECT | Native h2↔h2 DATA relay (no translation) |
| HTTP/2 extended CONNECT | HTTP/1.1 | h2→h1 bridge: translate 200→WebSocket upgrade, relay DATA as WebSocket frames |
| HTTP/1.1 Upgrade | HTTP/2 | When `EnableRfc8441=true`: translate to extended CONNECT on the h2 origin (`Http11ToHttp2BridgeHandler` + `Http2TunnelStream`); if the origin does not advertise `SETTINGS_ENABLE_CONNECT_PROTOCOL=1`, fall back to a dedicated HTTP/1.1 origin connection and reuse `HandleWebSocketUpgrade`. When `EnableRfc8441=false` (default): synthetic `501 Not Implemented` |

### HTTP/1.1 Upgrade → HTTP/2 origin lifecycle

1. **Gate**: only reached on the H1→H2 translation bridge (`UpstreamHttpProtocol.Http2` +
   `AllowHttpProtocolTranslation`) when the client sends `Upgrade: websocket`. With
   `EnableRfc8441=false`, the historical synthetic `501` is preserved.
2. **Capability check**: after the origin's initial SETTINGS, if `EnableConnectProtocol` is true,
   the proxy translates the Upgrade into `CONNECT` + `:protocol=websocket` (stripping
   `Connection`/`Upgrade`/`Sec-WebSocket-Key`/`Host` per RFC 8441 §5) and opens a tunnel stream via
   `Http2OriginConnection.OpenTunnelAsync`.
3. **Client 101**: on origin 2xx, the proxy synthesizes `101 Switching Protocols` with a locally
   computed `Sec-WebSocket-Accept` and any negotiated `Sec-WebSocket-Protocol` /
   `Sec-WebSocket-Extensions` from the origin, then runs `BeforeResponse`.
4. **DATA relay**: the leased h2 stream is exposed as `Http2TunnelStream` so
   `TcpHelper.SendRaw` / `WebSocketInterceptRelay` treat it as a TCP connection (RFC 8441 §5).
5. **Fallback**: if the h2 origin does not advertise the setting, the proxy opens a dedicated
   HTTP/1.1 origin connection for that WebSocket only and reuses `HandleWebSocketUpgrade`
   (RFC 8441 §7 intermediary behavior).

### Native h2↔h2 tunnel lifecycle

1. **Capability gate**: before forwarding the extended CONNECT HEADERS to the origin, the proxy
   awaits `ServerSettingsRelayed` (i.e. the origin's initial SETTINGS have been processed). If the
   origin's `EnableConnectProtocol` is still false after that point, the stream is reset with
   `REFUSED_STREAM` and the HEADERS are never forwarded.
2. **Tunnel establishment**: a final 2xx response from the origin marks `ExtendedConnectEstablished =
   true` on the stream state. Non-2xx responses follow the normal HTTP response body path.
3. **DATA relay**: once established, DATA frames from either direction are forwarded unchanged (no
   HTTP body buffering). `DataSent`/`DataReceived` events fire with the unpadded payload.
4. **Half-close**: `END_STREAM` on a DATA frame marks that direction as half-closed. Subsequent DATA
   from a half-closed direction is rejected with `RST_STREAM(STREAM_CLOSED)`.
5. **Post-establishment HEADERS**: any HEADERS or CONTINUATION frame on an established tunnel stream
   (e.g. trailers) is rejected with `RST_STREAM(PROTOCOL_ERROR)`.
6. **Reset / teardown**: `RST_STREAM`, `GOAWAY`, or connection closure unblocks body waiters, tunnel
   work, flow-controller entries, and finalization paths exactly once. `AfterResponse` runs exactly
   once per stream.

### Body API restrictions

`GetRequestBody` and `GetResponseBody` throw `InvalidOperationException` when called on an
established extended CONNECT tunnel stream. These are unbounded duplex streams, not finite HTTP
bodies. Non-2xx extended CONNECT response bodies (tunnel not established) remain readable normally.

### Via header

Extended CONNECT streams inherit the proxy's existing `Via` header policy (`ViaHeaderPseudonym`).
The relayed request and response each receive a `Via` field appended in the same way as ordinary h2
requests and responses.

### Fallback behavior

If the origin does not advertise `SETTINGS_ENABLE_CONNECT_PROTOCOL=1`, the proxy resets the affected
stream with `REFUSED_STREAM` so the client can fall back to HTTP/1.1. The proxy does NOT
transparently re-open a new h1 origin connection for the extended CONNECT in this case; the h2→h1
bridge is used only when the origin is configured or discovered to be HTTP/1.1.

### Flow-control limitation

The h2↔h2 relay uses the existing shared relay loop. If one stream's outbound DATA is blocked on
flow-control credit, it can block other streams' DATA on the same relay direction (head-of-line
blocking at the TCP send buffer level). Per-stream outbound scheduler is out of scope.

### Scope limitation

Only `:protocol = websocket` is supported in this release. Non-WebSocket extended CONNECT
(gRPC bidirectional, WebTransport, etc.) is deferred. Unsupported `:protocol` values cause the
proxy to return a synthetic `501 Not Implemented` response after running `BeforeRequest` (allowing
handlers to synthesize their own response before the fallback fires).

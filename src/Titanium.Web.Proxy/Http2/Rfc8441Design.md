# RFC 8441 Extended CONNECT / WebSocket over HTTP/2

## Scope
Demand-driven compatibility: Chromium and Firefox can use RFC 8441 only after receiving
`SETTINGS_ENABLE_CONNECT_PROTOCOL=1` from the server. HTTP/1.1 fallback remains common.
Safari does not support RFC 8441 (as of 2026).

## Design decisions

### SETTINGS_ENABLE_CONNECT_PROTOCOL (setting ID 8)
- The main relay transparently mirrors SETTINGS between legs, EXCEPT for ENABLE_PUSH (already
  intercepted) and ENABLE_CONNECT_PROTOCOL.
- ENABLE_CONNECT_PROTOCOL must be intercepted: the proxy decides whether it can accept extended
  CONNECT, not the client. The proxy should not forward a client's ENABLE_CONNECT_PROTOCOL=1
  to the server, nor a server's ENABLE_CONNECT_PROTOCOL=1 directly to the client.
- The proxy advertises ENABLE_CONNECT_PROTOCOL=1 to clients IFF it is configured to accept it.
- The proxy negotiates independently with the server.

### Extended CONNECT pseudo-header validation (RFC 8441 §5)
- An extended CONNECT request must have: `:method = CONNECT`, `:protocol`, `:scheme`, `:path`, `:authority`
- It must NOT have both `:method = CONNECT` and `:protocol` without `:scheme`/`:path` (plain CONNECT
  requires exactly `:method, :authority` with no `:scheme`/`:path`/`:protocol`)
- If `:protocol` is present on a non-CONNECT request, reject with RST_STREAM(PROTOCOL_ERROR)

### Translation matrix
1. **h2 extended CONNECT ↔ h2 extended CONNECT**: relay DATA frames directly (no translation needed)
2. **h2 extended CONNECT → h1 Upgrade**: translate the 200 response to h1 101, then relay DATA as WebSocket frames
3. **h1 Upgrade → h2 extended CONNECT**: translate the 101 response to h2 200, then relay WebSocket frames as DATA

### Fallback
If the server does not advertise SETTINGS_ENABLE_CONNECT_PROTOCOL=1, the proxy must:
- Either open a new h1 origin connection for the WebSocket upgrade (preferred)
- Or fail the stream deterministically with RST_STREAM(CONNECT_ERROR or REFUSED_STREAM)

### Scope limitation
- Only `:protocol = websocket` is supported in this release
- Non-WebSocket extended CONNECT (gRPC bidirectional, WebTransport, etc.) is deferred

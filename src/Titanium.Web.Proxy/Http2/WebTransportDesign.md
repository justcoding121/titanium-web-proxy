# WebTransport on HTTP/1.1 and HTTP/2

Opaque capsule bridge. Both sides that already speak capsules are copied as bytes after the session is established (`200` on HTTP/2, `101` on HTTP/1.1).

`SETTINGS_WT_MAX_SESSIONS` (identifier `0x2b60`, draft-ietf-webtrans-http2) is advertised only on a connection whose listener opted in. The value `1` is this proxy's enabled bit. The client's setting is cleared before it is forwarded to the origin. The origin's setting is what allows the stream to proceed. Otherwise the stream is `501`.

HTTP/3 WebTransport stays refused until a QUIC datagram channel exists. Do not parse capsules on the HTTP/3 tiny-GET path.

`type: webtransport` terminates the client session and opens `Upgrade: webtransport` on `Options.OriginHost`. It does not call `MasqueTunnel` or parse `ConnectUdpPath`. Extended CONNECT is enabled only inside that listener's `SendHttp2` call.

This is not MASQUE. Do not parse `ConnectUdpPath` and do not call `MasqueTunnel` for `:protocol=webtransport`.

# Titanium.Net.Http

An HTTP client handler that always uses the managed TLS stack.

HTTP/1.1 and HTTP/2 go through an inner handler. Its connect callback opens the TCP socket and authenticates a `TlsStream`. The handler rewrites `https` to `http` and keeps port 443 so the inner handler does not open its own TLS stream. A redirect is followed here, and each hop is rewritten the same way, so an https Location stays on the managed stream. Cookies and decompression stay on the inner handler. `Host` stays the original host. The response's request is the caller's request, with the `https` URI.

The inner handler sends `:scheme http` on HTTP/2. There is no hook to change that. A `400` or `421` on a GET, HEAD, OPTIONS, or TRACE is retried once over HTTP/1.1 when the request allows a lower version. A server that selects HTTP/1.1 during ALPN is retried over HTTP/1.1 before any request bytes, for any method, when a lower version is allowed.

HTTP/3 opens a managed QUIC connection and reuses it for the same authority until 16 request streams, the idle timeout, or the connection lifetime. A further request opens another connection, up to `MaxConnectionsPerServer`. A GOAWAY on the peer's control stream stops new requests on that connection. The request uses the static QPACK table. The dynamic table is capped at 4096 bytes and is used to decode a peer insert. The call returns after the response headers, and the body is read as the caller reads it. Cancelling that read stops it while it is waiting for the next frame. When `EnableAltSvc` is set, a later request that allows a higher version uses HTTP/3 after `Alt-Svc: h3` from the same origin. The cache holds 256 origins. An entry ends when its `ma` elapses. A missing `ma` lasts 24 hours. `ma=0` and `clear` drop the entry. A different host is ignored.

`samples/HttpClientManagedStack` sends one HTTPS request as HTTP/1.1, one as HTTP/2, and one as HTTP/3:

```
dotnet run --project samples/HttpClientManagedStack -- https://example.com/
```

HTTP/1.1 must succeed for a zero exit code. HTTP/3 is still attempted, and the program reports a failure when that server does not accept QUIC.

The package is experimental. It is not a FIPS-validated module. While FIPS mode is on, `SendAsync` throws unless `AllowManagedStackUnderFips` is set or `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1`.

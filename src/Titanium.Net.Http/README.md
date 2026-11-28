# Titanium.Net.Http

An HTTP client handler that always uses the managed TLS stack.

HTTP/1.1 and HTTP/2 go through an inner handler. Its connect callback opens the TCP socket and authenticates a `TlsStream`. The handler rewrites `https` to `http` and keeps port 443 so the inner handler does not open its own TLS stream. `Host` stays the original host. The response's request is the caller's request, with the `https` URI.

The inner handler sends `:scheme http` on HTTP/2. There is no hook to change that. A `400` or `421` on a GET, HEAD, OPTIONS, or TRACE is retried once over HTTP/1.1 when the request allows a lower version. A server that selects HTTP/1.1 during ALPN is retried over HTTP/1.1 before any request bytes, for any method, when a lower version is allowed.

HTTP/3 opens a managed QUIC connection and reuses it for the same authority until 16 request streams, the idle timeout, or the connection lifetime. A further request opens another connection, up to `MaxConnectionsPerServer`. The request uses the static QPACK table. The call returns after the response headers, and the body is read as the caller reads it. `EnableAltSvc` is stored and is not read yet.

The package is experimental. It is not a FIPS-validated module. While FIPS mode is on, `SendAsync` throws unless `AllowManagedStackUnderFips` is set or `TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1`.

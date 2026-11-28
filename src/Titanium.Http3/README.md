# Titanium.Http3

HTTP/3 framing for .NET. This package holds the pieces that do not know about a proxy or a socket. The first piece is the QUIC variable-length integer.

The codec is experimental. It is not a FIPS-validated module. It also holds the HTTP/3 frame types, stream types, and error codes. QPACK, frame readers, and capsules still live in Titanium Web Proxy until they no longer depend on proxy types.

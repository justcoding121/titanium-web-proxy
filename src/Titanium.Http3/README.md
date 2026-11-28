# Titanium.Http3

HTTP/3 framing for .NET. This package holds the pieces that do not know about a proxy or a socket. The first piece is the QUIC variable-length integer.

The codec is experimental. It is not a FIPS-validated module. It also holds the HTTP/3 frame types, stream types, error codes, the RFC 9297 capsule reader, the SETTINGS payload, the Huffman coding shared with HTTP/2, the QPACK tables, and the QPACK encoder and decoder. The proxy still turns its own request and response objects into header fields, and frame readers still write through the platform QUIC stream.

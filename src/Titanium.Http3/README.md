# Titanium.Http3

HTTP/3 framing for .NET. This package holds the pieces that do not know about a proxy or a socket. The first piece is the QUIC variable-length integer.

The codec is experimental. It is not a FIPS-validated module. It also holds the HTTP/3 frame types, stream types, error codes, the frame byte layout, the RFC 9297 capsule reader, the SETTINGS payload, the Huffman coding shared with HTTP/2, the QPACK tables, and the QPACK encoder and decoder. The proxy still reads frames and writes them to a stream, including the platform QUIC finish bit.

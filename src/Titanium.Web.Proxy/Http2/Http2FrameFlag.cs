using System;

namespace Titanium.Web.Proxy.Http2;

[Flags]
internal enum Http2FrameFlag : byte // NOSONAR S2342 -- Singular wire-protocol type name is intentional.
{
    Ack = 0x01,
    EndStream = 0x01, // NOSONAR CA1069 -- HTTP/2 reuses bit 0 by frame type.
    EndHeaders = 0x04,
    Padded = 0x08,
    Priority = 0x20
}
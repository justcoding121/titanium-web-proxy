#if NET6_0_OR_GREATER
using System;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Exception carrying an HTTP/3 error code, used to abort a stream or connection.
/// </summary>
internal sealed class Http3ConnectionException : Exception
{
    public Http3ConnectionException(Http3ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public Http3ErrorCode ErrorCode { get; }
}

/// <summary>
///     Exception for stream-level errors (RST_STREAM equivalent — QUIC STOP_SENDING + RESET_STREAM).
/// </summary>
internal sealed class Http3StreamException : Exception
{
    public Http3StreamException(Http3ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public Http3ErrorCode ErrorCode { get; }
}
#endif

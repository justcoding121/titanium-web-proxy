namespace Titanium.Http3;

/// <summary>HTTP/3 application error codes (RFC 9114 §8.1).</summary>
public enum Http3ErrorCode : long
{
    /// <summary>No error. The connection or stream is closing without a more specific cause.</summary>
    NoError = 0x100,

    /// <summary>The peer violated the protocol and no more specific code applies.</summary>
    GeneralProtocolError = 0x101,

    /// <summary>An internal error occurred in the HTTP stack.</summary>
    InternalError = 0x102,

    /// <summary>The peer created a stream that this endpoint will not accept.</summary>
    StreamCreationError = 0x103,

    /// <summary>A stream required by the connection was closed or reset.</summary>
    ClosedCriticalStream = 0x104,

    /// <summary>A frame arrived in a state or on a stream where it is not allowed.</summary>
    FrameUnexpected = 0x105,

    /// <summary>A frame failed its layout or size requirements.</summary>
    FrameError = 0x106,

    /// <summary>The peer is behaving in a way that may create excessive load.</summary>
    ExcessiveLoad = 0x107,

    /// <summary>A stream ID or push ID was used incorrectly.</summary>
    IdError = 0x108,

    /// <summary>The SETTINGS payload was invalid.</summary>
    SettingsError = 0x109,

    /// <summary>The control stream did not start with SETTINGS.</summary>
    MissingSettings = 0x10A,

    /// <summary>The server rejected a request without application processing.</summary>
    RequestRejected = 0x10B,

    /// <summary>The request or its response was cancelled.</summary>
    RequestCancelled = 0x10C,

    /// <summary>The stream ended before a complete request.</summary>
    RequestIncomplete = 0x10D,

    /// <summary>The message was well formed and is not supported.</summary>
    MessageError = 0x10E,

    /// <summary>The TCP connection from a CONNECT request was reset or closed abnormally.</summary>
    ConnectError = 0x10F,

    /// <summary>The operation cannot be served over HTTP/3. The peer should retry over HTTP/1.1.</summary>
    VersionFallback = 0x110,

    /// <summary>QPACK decompression failed.</summary>
    QpackDecompressionFailed = 0x200,

    /// <summary>The QPACK encoder stream failed.</summary>
    QpackEncoderStreamError = 0x201,

    /// <summary>The QPACK decoder stream failed.</summary>
    QpackDecoderStreamError = 0x202,
}

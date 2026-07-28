#if NET6_0_OR_GREATER
namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     HTTP/3 application error codes (RFC 9114 §8.1).
/// </summary>
internal enum Http3ErrorCode : long
{
    /// <summary>No error. This is used when the connection or stream needs to be closed, but there is no error to signal.</summary>
    NoError = 0x100,

    /// <summary>Peer violated protocol requirements in a way which doesn't match a more specific error code, or endpoint declines to use the more specific error code.</summary>
    GeneralProtocolError = 0x101,

    /// <summary>An internal error has occurred in the HTTP stack.</summary>
    InternalError = 0x102,

    /// <summary>The endpoint detected that its peer created a stream that it will not accept.</summary>
    StreamCreationError = 0x103,

    /// <summary>A stream required by the HTTP/3 connection was closed or reset.</summary>
    ClosedCriticalStream = 0x104,

    /// <summary>A frame was received which was not permitted in the current state or on the current stream type.</summary>
    FrameUnexpected = 0x105,

    /// <summary>A frame that fails to satisfy layout requirements or with an invalid size was received.</summary>
    FrameError = 0x106,

    /// <summary>The endpoint detected that its peer is exhibiting a behavior that might be generating excessive load.</summary>
    ExcessiveLoad = 0x107,

    /// <summary>A Stream ID or Push ID was used incorrectly, such as exceeding a limit, reducing a limit, or being reused.</summary>
    IdError = 0x108,

    /// <summary>An endpoint detected an error in the payload of a SETTINGS frame.</summary>
    SettingsError = 0x109,

    /// <summary>No SETTINGS frame was received at the beginning of the control stream.</summary>
    MissingSettings = 0x10a,

    /// <summary>A server rejected a request without performing any application processing.</summary>
    RequestRejected = 0x10b,

    /// <summary>The request or its response (including pushed response) is cancelled.</summary>
    RequestCancelled = 0x10c,

    /// <summary>The client's stream terminated without containing a fully-formed request.</summary>
    RequestIncomplete = 0x10d,

    /// <summary>An HTTP message was well-formed but is not supported by this implementation.</summary>
    MessageError = 0x10e,

    /// <summary>The TCP connection established in response to a CONNECT request was reset or abnormally closed.</summary>
    ConnectError = 0x10f,

    /// <summary>The requested operation cannot be served over HTTP/3. The peer should retry over HTTP/1.1.</summary>
    VersionFallback = 0x110,

    /// <summary>QPACK header block decompression failure.</summary>
    QpackDecompressionFailed = 0x200,

    /// <summary>An error on the QPACK encoder stream.</summary>
    QpackEncoderStreamError = 0x201,

    /// <summary>An error on the QPACK decoder stream.</summary>
    QpackDecoderStreamError = 0x202
}
#endif

#if NET6_0_OR_GREATER
namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     HTTP/3 frame type values (RFC 9114 §7.2).
/// </summary>
internal static class Http3FrameType
{
    /// <summary>DATA frame. Carries arbitrary, variable-length sequences of octets associated with an HTTP request or response payload.</summary>
    public const ulong Data = 0x0;

    /// <summary>HEADERS frame. Used to carry an HTTP field section, encoded using QPACK.</summary>
    public const ulong Headers = 0x1;

    /// <summary>CANCEL_PUSH frame. Requests cancellation of a server push prior to the push stream being received.</summary>
    public const ulong CancelPush = 0x3;

    /// <summary>SETTINGS frame. Conveys configuration parameters that affect how endpoints communicate.</summary>
    public const ulong Settings = 0x4;

    /// <summary>PUSH_PROMISE frame. Used to carry a promised request header section from server to client.</summary>
    public const ulong PushPromise = 0x5;

    /// <summary>GOAWAY frame. Initiates graceful shutdown of a connection by either endpoint.</summary>
    public const ulong GoAway = 0x7;

    /// <summary>MAX_PUSH_ID frame. Used by clients to control the number of server pushes that the server can initiate.</summary>
    public const ulong MaxPushId = 0xD;
}

/// <summary>
///     HTTP/3 unidirectional stream type codes (RFC 9114 §6.2).
/// </summary>
internal static class Http3StreamType
{
    /// <summary>Control stream. Carries control messages for the connection.</summary>
    public const ulong Control = 0x0;

    /// <summary>Push stream. Carries server push data.</summary>
    public const ulong Push = 0x1;

    /// <summary>QPACK encoder stream.</summary>
    public const ulong QpackEncoder = 0x2;

    /// <summary>QPACK decoder stream.</summary>
    public const ulong QpackDecoder = 0x3;
}
#endif

namespace Titanium.Http3;

/// <summary>HTTP/3 frame type values (RFC 9114 §7.2).</summary>
public static class Http3FrameType
{
    /// <summary>DATA frame. Carries the request or response payload.</summary>
    public const ulong Data = 0x0;

    /// <summary>HEADERS frame. Carries a QPACK field section.</summary>
    public const ulong Headers = 0x1;

    /// <summary>CANCEL_PUSH frame. Cancels a server push before its stream arrives.</summary>
    public const ulong CancelPush = 0x3;

    /// <summary>SETTINGS frame. Carries connection parameters.</summary>
    public const ulong Settings = 0x4;

    /// <summary>PUSH_PROMISE frame. Carries a promised request header section.</summary>
    public const ulong PushPromise = 0x5;

    /// <summary>GOAWAY frame. Starts a graceful shutdown.</summary>
    public const ulong GoAway = 0x7;

    /// <summary>MAX_PUSH_ID frame. Limits how many pushes the server may start.</summary>
    public const ulong MaxPushId = 0xD;
}

/// <summary>HTTP/3 unidirectional stream type codes (RFC 9114 §6.2).</summary>
public static class Http3StreamType
{
    /// <summary>Control stream.</summary>
    public const ulong Control = 0x0;

    /// <summary>Push stream.</summary>
    public const ulong Push = 0x1;

    /// <summary>QPACK encoder stream.</summary>
    public const ulong QpackEncoder = 0x2;

    /// <summary>QPACK decoder stream.</summary>
    public const ulong QpackDecoder = 0x3;
}

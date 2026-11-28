namespace Titanium.Web.Proxy.Http3;

/// <summary>HTTP/3 frame type values. The numbers live in <see cref="Titanium.Http3.Http3FrameType"/>.</summary>
internal static class Http3FrameType
{
    /// <summary>DATA frame.</summary>
    public const ulong Data = Titanium.Http3.Http3FrameType.Data;

    /// <summary>HEADERS frame.</summary>
    public const ulong Headers = Titanium.Http3.Http3FrameType.Headers;

    /// <summary>CANCEL_PUSH frame.</summary>
    public const ulong CancelPush = Titanium.Http3.Http3FrameType.CancelPush;

    /// <summary>SETTINGS frame.</summary>
    public const ulong Settings = Titanium.Http3.Http3FrameType.Settings;

    /// <summary>PUSH_PROMISE frame.</summary>
    public const ulong PushPromise = Titanium.Http3.Http3FrameType.PushPromise;

    /// <summary>GOAWAY frame.</summary>
    public const ulong GoAway = Titanium.Http3.Http3FrameType.GoAway;

    /// <summary>MAX_PUSH_ID frame.</summary>
    public const ulong MaxPushId = Titanium.Http3.Http3FrameType.MaxPushId;
}

/// <summary>HTTP/3 unidirectional stream types. The numbers live in <see cref="Titanium.Http3.Http3StreamType"/>.</summary>
internal static class Http3StreamType
{
    /// <summary>Control stream.</summary>
    public const ulong Control = Titanium.Http3.Http3StreamType.Control;

    /// <summary>Push stream.</summary>
    public const ulong Push = Titanium.Http3.Http3StreamType.Push;

    /// <summary>QPACK encoder stream.</summary>
    public const ulong QpackEncoder = Titanium.Http3.Http3StreamType.QpackEncoder;

    /// <summary>QPACK decoder stream.</summary>
    public const ulong QpackDecoder = Titanium.Http3.Http3StreamType.QpackDecoder;
}

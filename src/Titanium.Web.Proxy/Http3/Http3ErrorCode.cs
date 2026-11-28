namespace Titanium.Web.Proxy.Http3;

/// <summary>HTTP/3 application error codes. The numbers live in <see cref="Titanium.Http3.Http3ErrorCode"/>.</summary>
internal enum Http3ErrorCode : long
{
    /// <summary>No error.</summary>
    NoError = (long)Titanium.Http3.Http3ErrorCode.NoError,

    /// <summary>General protocol error.</summary>
    GeneralProtocolError = (long)Titanium.Http3.Http3ErrorCode.GeneralProtocolError,

    /// <summary>Internal error.</summary>
    InternalError = (long)Titanium.Http3.Http3ErrorCode.InternalError,

    /// <summary>Stream creation error.</summary>
    StreamCreationError = (long)Titanium.Http3.Http3ErrorCode.StreamCreationError,

    /// <summary>Closed critical stream.</summary>
    ClosedCriticalStream = (long)Titanium.Http3.Http3ErrorCode.ClosedCriticalStream,

    /// <summary>Unexpected frame.</summary>
    FrameUnexpected = (long)Titanium.Http3.Http3ErrorCode.FrameUnexpected,

    /// <summary>Frame error.</summary>
    FrameError = (long)Titanium.Http3.Http3ErrorCode.FrameError,

    /// <summary>Excessive load.</summary>
    ExcessiveLoad = (long)Titanium.Http3.Http3ErrorCode.ExcessiveLoad,

    /// <summary>ID error.</summary>
    IdError = (long)Titanium.Http3.Http3ErrorCode.IdError,

    /// <summary>Settings error.</summary>
    SettingsError = (long)Titanium.Http3.Http3ErrorCode.SettingsError,

    /// <summary>Missing settings.</summary>
    MissingSettings = (long)Titanium.Http3.Http3ErrorCode.MissingSettings,

    /// <summary>Request rejected.</summary>
    RequestRejected = (long)Titanium.Http3.Http3ErrorCode.RequestRejected,

    /// <summary>Request cancelled.</summary>
    RequestCancelled = (long)Titanium.Http3.Http3ErrorCode.RequestCancelled,

    /// <summary>Request incomplete.</summary>
    RequestIncomplete = (long)Titanium.Http3.Http3ErrorCode.RequestIncomplete,

    /// <summary>Message error.</summary>
    MessageError = (long)Titanium.Http3.Http3ErrorCode.MessageError,

    /// <summary>CONNECT error.</summary>
    ConnectError = (long)Titanium.Http3.Http3ErrorCode.ConnectError,

    /// <summary>Version fallback.</summary>
    VersionFallback = (long)Titanium.Http3.Http3ErrorCode.VersionFallback,

    /// <summary>QPACK decompression failed.</summary>
    QpackDecompressionFailed = (long)Titanium.Http3.Http3ErrorCode.QpackDecompressionFailed,

    /// <summary>QPACK encoder stream error.</summary>
    QpackEncoderStreamError = (long)Titanium.Http3.Http3ErrorCode.QpackEncoderStreamError,

    /// <summary>QPACK decoder stream error.</summary>
    QpackDecoderStreamError = (long)Titanium.Http3.Http3ErrorCode.QpackDecoderStreamError,
}

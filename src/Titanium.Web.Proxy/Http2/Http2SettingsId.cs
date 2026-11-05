namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     SETTINGS frame parameter identifiers (RFC 7540 §6.5.2 / RFC 9113 §6.5.2).
/// </summary>
internal enum Http2SettingsId
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6,

    /// <summary>RFC 8441 §3: enables extended CONNECT (WebSocket-over-HTTP/2) on this connection.</summary>
    EnableConnectProtocol = 0x8,

    /// <summary>
    ///     draft-ietf-webtrans-http2 SETTINGS_WT_MAX_SESSIONS. A positive value advertises WebTransport.
    ///     The proxy sends 1 on connections that opt in.
    /// </summary>
    WtMaxSessions = 0x2b60
}

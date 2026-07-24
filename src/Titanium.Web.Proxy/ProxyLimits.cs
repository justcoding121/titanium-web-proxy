namespace Titanium.Web.Proxy;

/// <summary>
/// Default resource-limit thresholds used by various proxy components.
/// These defaults can be overridden by assigning them before starting the proxy.
/// Phase 0 policy decision: document defaults here so later phases can expose
/// them as configurable properties on ProxyServer.
/// </summary>
public static class ProxyLimits
{
    /// <summary>
    /// Maximum total decoded HTTP/2 header list size (sum of name + value + 32 per field).
    /// Streams with header lists exceeding this limit are rejected with RST_STREAM(ENHANCE_YOUR_CALM).
    /// Default: 64 KiB.
    /// </summary>
    public const int DefaultMaxDecodedHeaderListBytes = 64 * 1024;

    /// <summary>
    /// Maximum buffered request or response body size for proxied exchanges where full
    /// buffering is required (body mutation, authentication retry, etc.).
    /// Default: 4 MiB.
    /// </summary>
    public const long DefaultMaxBufferedBodyBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Maximum WebSocket frame payload size the proxy will accept during frame-level interception.
    /// Frames exceeding this limit are dropped and the connection is closed.
    /// Raw relay (no interception) is not subject to this limit.
    /// Default: 16 MiB.
    /// </summary>
    public const int DefaultMaxWebSocketFramePayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum WebSocket message size (sum of all fragment payloads) the proxy will
    /// reassemble. Messages exceeding this limit are dropped and the connection is closed.
    /// Default: 64 MiB.
    /// </summary>
    public const long DefaultMaxWebSocketMessageBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Pseudonym to use in Via header fields appended by this proxy.
    /// Empty string means Via headers are not appended (the default, for privacy and compatibility).
    /// Must be set to a non-empty token before Via header support is enabled in a later phase.
    /// </summary>
    public const string DefaultViaPseudonym = "";

    /// <summary>
    /// Maximum number of authentication challenge rounds allowed per request
    /// (e.g. NTLM three-way handshake counts as one round; additional 401/407 responses count separately).
    /// Default: 3.
    /// </summary>
    public const int DefaultMaxAuthRounds = 3;
}

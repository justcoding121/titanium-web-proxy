namespace Titanium.Web.Proxy;

/// <summary>
/// Default resource-limit thresholds used by various proxy components.
/// These defaults can be overridden by assigning them before starting the proxy.
/// Phase 0 policy decision: document defaults here so later phases can expose
/// them as configurable properties on ProxyServer.
/// </summary>
/// <remarks>
/// These are <see langword="static readonly" />, not <see langword="const" />, even though every
/// current value is a compile-time constant. A <see langword="const" /> field is copied by value
/// into every consumer assembly at their compile time; if a later release changes the default, a
/// consumer that has not recompiled keeps the stale inlined value instead of picking up the new
/// one from the referenced <c>Titanium.Web.Proxy.dll</c>. <see langword="static readonly" /> is
/// resolved at load time, so a binary-only upgrade of this library takes effect for callers.
/// </remarks>
public static class ProxyLimits
{
    /// <summary>
    /// Maximum total decoded HTTP/2 header list size (sum of name + value + 32 per field).
    /// Streams with header lists exceeding this limit are rejected with RST_STREAM(ENHANCE_YOUR_CALM).
    /// Default: 64 KiB.
    /// </summary>
    public static readonly int DefaultMaxDecodedHeaderListBytes = 64 * 1024;

    /// <summary>
    /// Maximum buffered request or response body size for proxied exchanges where full
    /// buffering is required (body mutation, authentication retry, etc.).
    /// Default: 4 MiB.
    /// </summary>
    public static readonly long DefaultMaxBufferedBodyBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Maximum WebSocket frame payload size the proxy will accept during frame-level interception.
    /// Frames exceeding this limit are dropped and the connection is closed.
    /// Raw relay (no interception) is not subject to this limit.
    /// Default: 16 MiB.
    /// </summary>
    public static readonly int DefaultMaxWebSocketFramePayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum WebSocket message size (sum of all fragment payloads) the proxy will
    /// reassemble. Messages exceeding this limit are dropped and the connection is closed.
    /// Default: 64 MiB.
    /// </summary>
    public static readonly long DefaultMaxWebSocketMessageBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Pseudonym to use in Via header fields appended by this proxy.
    /// Empty string means Via headers are not appended (the default, for privacy and compatibility).
    /// Must be set to a non-empty token before Via header support is enabled in a later phase.
    /// </summary>
    public static readonly string DefaultViaPseudonym = "";

    /// <summary>
    /// Maximum number of authentication challenge rounds allowed per request
    /// (e.g. NTLM three-way handshake counts as one round; additional 401/407 responses count separately).
    /// Default: 3.
    /// </summary>
    public static readonly int DefaultMaxAuthRounds = 3;

    /// <summary>
    /// Maximum accepted value of a single HTTP/1 <c>chunk-size</c> line (RFC 9112 §7.1), in bytes.
    /// The chunk-size grammar itself (<c>1*HEXDIG</c>) has no length ceiling, so this is a proxy-owned
    /// safety bound rather than a protocol requirement: it exists to reject the two's-complement chunk-
    /// size wrap (an attacker-supplied value like "ffffffff" must not decode to a small/negative sentinel)
    /// and to avoid ever attempting to allocate or forward a chunk of unbounded size. Set high enough
    /// that no legitimate chunk from real-world traffic should reach it.
    /// Default: 1 GiB.
    /// </summary>
    public static readonly long DefaultMaxChunkSizeBytes = 1024L * 1024 * 1024;
}

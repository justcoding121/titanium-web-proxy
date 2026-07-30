namespace Titanium.Web.Proxy;

/// <summary>
/// Reference values for a handful of resource-limit defaults, kept here for documentation and
/// cross-checking purposes.
/// </summary>
/// <remarks>
/// <para>
/// These fields are <see langword="static readonly" />, not <see langword="const" />, so that if a
/// later release changes one of these reference values, a consumer that has not recompiled still
/// picks up the new value from the referenced <c>Titanium.Web.Proxy.dll</c> at load time rather than
/// keeping a stale value inlined by the compiler into their own assembly.
/// </para>
/// <para>
/// <b>These fields cannot be "overridden by assigning them before starting the proxy":</b> they are
/// <see langword="readonly" />, so external code cannot assign to them at all (that would be a
/// compile error). Only <see cref="DefaultMaxChunkSizeBytes" /> is actually read by any live code
/// path (by <c>ChunkSizeParser.TryParse</c>'s call sites in <c>HttpStream</c>/<c>LimitedStream</c>);
/// every other field below documents a value that is independently hardcoded as the real default on
/// the corresponding <c>ProxyServer</c> (or, for authentication rounds, <c>WinAuthHandler</c>)
/// property, and is not consulted by that property's own default or by any enforcement path. Treat
/// the fields below other than <see cref="DefaultMaxChunkSizeBytes" /> as documentation of what each
/// corresponding property's shipped default currently is, not as the mechanism that produces it: to
/// change a limit at runtime, set the actual <c>ProxyServer</c> property (e.g.
/// <c>ProxyServer.MaxBufferedBodyBytes</c>), never the field here.
/// </para>
/// </remarks>
public static class ProxyLimits
{
    /// <summary>
    /// Reference value only - see the type-level remarks. Matches the current default of
    /// <c>ProxyServer.MaxDecodedHeaderListBytes</c>: maximum total decoded HTTP/2 header list size
    /// (sum of name + value + 32 per field). Streams with header lists exceeding that property's
    /// configured value are rejected with RST_STREAM(ENHANCE_YOUR_CALM).
    /// Default: 64 KiB.
    /// </summary>
    public static readonly int DefaultMaxDecodedHeaderListBytes = 64 * 1024;

    /// <summary>
    /// Reference value only - see the type-level remarks. Matches the current default of
    /// <c>ProxyServer.MaxBufferedBodyBytes</c>: maximum buffered request or response body size for
    /// proxied exchanges where full buffering is required (body mutation, authentication retry,
    /// etc.).
    /// Default: 4 MiB.
    /// </summary>
    public static readonly long DefaultMaxBufferedBodyBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Reference value only - see the type-level remarks. Matches the current default of
    /// <c>ProxyServer.MaxWebSocketFramePayloadBytes</c>: maximum WebSocket frame payload size the
    /// proxy will accept during frame-level interception (validated against the declared length
    /// before any payload is buffered). Raw relay (no interception) is not subject to this limit.
    /// Default: 16 MiB.
    /// </summary>
    public static readonly int DefaultMaxWebSocketFramePayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reference value only - see the type-level remarks. Unlike the other fields on this type, this
    /// one does not correspond to any enforced <c>ProxyServer</c> property today: there is no
    /// cumulative-reassembled-message-size cap on the WebSocket path, only the per-frame
    /// <see cref="DefaultMaxWebSocketFramePayloadBytes" />/<c>MaxWebSocketFramePayloadBytes</c> limit.
    /// This value is retained as a documented target for that still-unimplemented cumulative budget.
    /// Default: 64 MiB.
    /// </summary>
    public static readonly long DefaultMaxWebSocketMessageBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Unused legacy field. <c>ProxyServer.ViaHeaderPseudonym</c> defaults to
    /// <c>"titanium-web-proxy"</c> (not empty) and does not read this field; nothing in the codebase
    /// references it. Retained only so removing it is not itself an undocumented breaking change;
    /// do not treat it as authoritative for the real default.
    /// </summary>
    public static readonly string DefaultViaPseudonym = "";

    /// <summary>
    /// Reference value only - see the type-level remarks. Matches
    /// <c>WinAuthHandler.MaxAuthChallengeRounds</c>: maximum number of authentication challenge
    /// rounds allowed per request (e.g. NTLM three-way handshake counts as one round; additional
    /// 401/407 responses count separately). The two values are independently maintained, not linked.
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
    /// <para>
    /// Unlike every other field on this type, this one is actually read live, by every
    /// <c>ChunkSizeParser.TryParse</c> call site in <c>HttpStream</c> and <c>LimitedStream</c>.
    /// </para>
    /// Default: 1 GiB.
    /// </summary>
    public static readonly long DefaultMaxChunkSizeBytes = 1024L * 1024 * 1024;
}

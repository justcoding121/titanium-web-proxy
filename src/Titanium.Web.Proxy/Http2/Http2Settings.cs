#if NET6_0_OR_GREATER
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     The subset of one peer's advertised SETTINGS this relay leg needs to remember - i.e. this instance
///     represents what a given peer has told the proxy about itself and constrains how the proxy must
///     encode/frame data destined for that peer.
/// </summary>
internal class Http2Settings
{
    public int HeaderTableSize { get; set; } = 4096;

    public int MaxFrameSize { get; set; } = 16384;

    /// <summary>
    ///     RFC 7540 §6.5.2: the maximum number of streams this peer is willing to have opened toward it
    ///     concurrently. Absent a SETTINGS_MAX_CONCURRENT_STREAMS entry, the RFC-default meaning is
    ///     "unlimited", represented here as <see cref="int.MaxValue" />.
    /// </summary>
    public int MaxConcurrentStreams { get; set; } = int.MaxValue;

    /// <summary>
    ///     RFC 7540 §6.5.2 SETTINGS_MAX_HEADER_LIST_SIZE — advisory limit on the size of header lists
    ///     that the sender of this SETTINGS is willing to receive. The RFC default is unlimited.
    /// </summary>
    public int MaxHeaderListSize { get; set; } = int.MaxValue;

    /// <summary>
    ///     The HPACK encoder (and its dynamic table) used for header blocks sent in the direction this
    ///     settings instance represents the peer for. Lazily created and persisted for the life of the
    ///     connection - see the comment in <c>Http2Helper.SendHeader</c>.
    /// </summary>
    public Encoder? Encoder { get; set; }
}
#endif

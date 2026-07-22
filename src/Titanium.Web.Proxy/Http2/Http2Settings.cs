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
    ///     The HPACK encoder (and its dynamic table) used for header blocks sent in the direction this
    ///     settings instance represents the peer for. Lazily created and persisted for the life of the
    ///     connection - see the comment in <c>Http2Helper.SendHeader</c>.
    /// </summary>
    public Encoder? Encoder { get; set; }
}
#endif

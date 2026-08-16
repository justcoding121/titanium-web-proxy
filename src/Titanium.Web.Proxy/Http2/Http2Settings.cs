using System.IO;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     The subset of one peer's advertised SETTINGS this relay leg needs to remember - i.e. this instance
///     represents what a given peer has told the proxy about itself and constrains how the proxy must
///     encode/frame data destined for that peer.
/// </summary>
internal class Http2Settings
{
    /// <summary>
    ///     Current HPACK dynamic table size ceiling advertised by the peer (RFC 7541 §6.3).
    ///     Defaults to the RFC-mandated 4096-byte initial value.
    ///     Use <see cref="UpdateHeaderTableSize"/> to change this so the minimum-tracking invariant is maintained.
    /// </summary>
    public int HeaderTableSize { get; private set; } = 4096;

    /// <summary>
    ///     The smallest <see cref="HeaderTableSize"/> value seen since the most recent call to
    ///     <see cref="NotifyHeaderBlockEncoded"/> (i.e. since the last HEADERS or trailers frame was sent
    ///     in this direction). RFC 7541 §6.3 requires that when multiple SETTINGS_HEADER_TABLE_SIZE updates
    ///     arrive between two header blocks the encoder MUST signal the intermediate minimum before signaling
    ///     the final value, so that the peer's decoder can evict any entries it could not have kept.
    ///     If no update arrived since the last encode, this equals <see cref="HeaderTableSize"/>.
    /// </summary>
    public int MinHeaderTableSizeSinceLastEncode { get; private set; } = 4096;

    /// <summary>Updates the header-table size ceiling, keeping the minimum-since-last-encode in sync.</summary>
    public void UpdateHeaderTableSize(int value)
    {
        HeaderTableSize = value;
        if (value < MinHeaderTableSizeSinceLastEncode)
            MinHeaderTableSizeSinceLastEncode = value;
    }

    /// <summary>
    ///     Called by <c>SendHeader</c>/<c>SendTrailer</c> after a Dynamic Table Size Update (if any) has
    ///     been emitted. Resets the minimum tracker so that only updates arriving <em>after</em> the last
    ///     encode are rolled up into the next header block.
    /// </summary>
    public void NotifyHeaderBlockEncoded() => MinHeaderTableSizeSinceLastEncode = HeaderTableSize;

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
    ///     RFC 8441: whether the endpoint supports extended CONNECT (WebSocket-over-HTTP/2).
    ///     Set to <see langword="true"/> when the peer sends SETTINGS_ENABLE_CONNECT_PROTOCOL=1.
    ///     Once set to <see langword="true"/>, it MUST NOT be set back to <see langword="false"/>:
    ///     RFC 8441 §3 forbids the 1→0 transition.
    /// </summary>
    public bool EnableConnectProtocol { get; set; } = false;

    /// <summary>
    ///     <see langword="true"/> once this peer has ever sent SETTINGS_ENABLE_CONNECT_PROTOCOL=1.
    ///     Used to detect the forbidden 1→0 downgrade (RFC 8441 §3).
    /// </summary>
    public bool EnableConnectProtocolEverSet { get; set; } = false;

    /// <summary>
    ///     The HPACK encoder (and its dynamic table) used for header blocks sent in the direction this
    ///     settings instance represents the peer for. Lazily created and persisted for the life of the
    ///     connection - see the comment in <c>Http2Helper.SendHeader</c>.
    /// </summary>
    public Encoder? Encoder { get; set; }

    /// <summary>
    ///     Scratch buffer for HPACK encoding on this direction. Only touched under the connection write
    ///     lock for this peer, so reuse is race-free and avoids per-HEADERS <c>MemoryStream</c> allocs.
    /// </summary>
    private MemoryStream? encodeStream;

    /// <summary>Returns a zeroed encode scratch stream for the next header block on this direction.</summary>
    public MemoryStream GetEncodeStream()
    {
        if (encodeStream == null)
            encodeStream = new MemoryStream(256);
        else
            encodeStream.SetLength(0);

        return encodeStream;
    }
}

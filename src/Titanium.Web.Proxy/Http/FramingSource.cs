namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Identifies where a <see cref="RequestResponseBase" />'s headers originated from, so
///     <see cref="Http1FramingValidator.Validate" /> can be routed to the correct rule set. There is
///     deliberately no default value and no parameterless <c>Validate</c> overload: a new call site
///     cannot compile without picking a member of this enum explicitly, which is what makes the
///     HTTP/1-wire-only boundary enforceable at compile time rather than by convention alone.
/// </summary>
internal enum FramingSource
{
    /// <summary>
    ///     Bytes were read directly off the wire via <c>HttpStream</c>/<c>HeaderParser</c> for an
    ///     explicit-proxy HTTP/1 request or its origin's HTTP/1 response.
    /// </summary>
    Http1Wire,

    /// <summary>
    ///     Bytes were read directly off the wire for an HTTP/1 request or response on a transparent
    ///     proxy endpoint.
    /// </summary>
    Http1WireTransparent,

    /// <summary>
    ///     Bytes were read directly off the wire for an HTTP/1 request or response behind a SOCKS
    ///     tunnel.
    /// </summary>
    Http1WireSocks,

    /// <summary>
    ///     The message was constructed synthetically by the HTTP/1-to/from-HTTP/2 bridge from decoded
    ///     HTTP/2 pseudo-headers and frames, never from bytes read by <c>HttpStream</c>. HTTP/1 framing
    ///     rules (chunked transfer-coding, <c>Content-Length</c> ambiguity) do not apply: length framing
    ///     is authoritative from the HTTP/2 frame layer, and <c>Transfer-Encoding</c> is forbidden
    ///     outright there except <c>trailers</c> per RFC 9113 §8.2.2.
    /// </summary>
    SynthesizedFromH2,

    /// <summary>
    ///     The message was constructed synthetically by the HTTP/2-to-HTTP/3 bridge from decoded
    ///     HTTP/3 frames, never from bytes read by <c>HttpStream</c>. Same rationale as
    ///     <see cref="SynthesizedFromH2" />, for the HTTP/3 frame layer.
    /// </summary>
    SynthesizedFromH3
}

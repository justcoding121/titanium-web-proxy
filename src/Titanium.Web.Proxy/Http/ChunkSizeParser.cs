using System;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Parses an HTTP/1 chunk-size line per RFC 9112 §7.1: <c>chunk-size = 1*HEXDIG</c>, a grammar with
///     no length ceiling of its own (arbitrary leading zeros are legal) and an optional <c>chunk-ext</c>
///     after a <c>;</c> that must be tolerated, not decoded.
///     <para>
///         <c>int.TryParse(chunkHead, NumberStyles.HexNumber, ...)</c> - the pattern this type replaces -
///         reinterprets the hex digits as a two's-complement 32-bit value, so a peer-supplied "ffffffff"
///         silently becomes <c>-1</c>. Several call sites in this codebase use <c>-1</c>/negative as a
///         sentinel for "no more chunks" or skip a body-read loop guarded by <c>while (remaining &gt; 0)</c>
///         entirely for a negative value. Either way, an attacker-chosen 8-hex-digit chunk size can make
///         the parser believe the chunk (and the body) already ended while the peer's actual chunk bytes
///         remain unconsumed on the wire, desynchronizing everything read afterwards on that connection -
///         a request-smuggling primitive on a connection that gets reused/pooled. This parser accumulates
///         digits as an unsigned magnitude with no two's-complement reinterpretation, so it can only ever
///         produce a value in <c>[0, maxChunkSizeBytes]</c> or fail outright; it never produces a negative
///         or wrapped result.
///     </para>
/// </summary>
internal static class ChunkSizeParser
{
    /// <summary>
    ///     Attempts to parse a chunk-size line (already stripped of its trailing CRLF, but not of any
    ///     <c>chunk-ext</c>) into a bounded, non-negative chunk size.
    /// </summary>
    /// <param name="line">The raw chunk-size line, e.g. <c>"1a3"</c> or <c>"1a3;foo=bar"</c>.</param>
    /// <param name="maxChunkSizeBytes">
    ///     The largest value this call site will accept (see <see cref="ProxyLimits.DefaultMaxChunkSizeBytes" />).
    ///     Rejecting early avoids ever attempting to allocate or forward a chunk larger than any call site
    ///     could actually bound.
    /// </param>
    /// <param name="chunkSize">The parsed size in bytes, valid only when this method returns <see langword="true" />.</param>
    /// <returns>
    ///     <see langword="false" /> if <paramref name="line" /> is empty, contains a character before the
    ///     optional <c>;chunk-ext</c> that is not a hex digit, or decodes to a value greater than
    ///     <paramref name="maxChunkSizeBytes" />.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<char> line, long maxChunkSizeBytes, out long chunkSize)
    {
        chunkSize = 0;

        var semicolon = line.IndexOf(';');
        var sizePart = semicolon >= 0 ? line[..semicolon] : line;

        if (sizePart.Length == 0) return false;

        long value = 0;
        foreach (var c in sizePart)
        {
            var digit = HexDigitValue(c);
            if (digit < 0) return false;

            // Bail out the moment another hex digit would overflow a 64-bit accumulator, rather than
            // wrapping into a small or negative value the way the two's-complement int parse used to.
            // Any sane maxChunkSizeBytes is reached long before this ever triggers, so it is a pure
            // overflow guard, not the primary bound.
            if (value > (long.MaxValue - digit) >> 4) return false;

            value = (value << 4) | (uint)digit;
        }

        if (value > maxChunkSizeBytes) return false;

        chunkSize = value;
        return true;
    }

    private static int HexDigitValue(char c)
    {
        if (c is >= '0' and <= '9') return c - '0';
        if (c is >= 'a' and <= 'f') return c - 'a' + 10;
        if (c is >= 'A' and <= 'F') return c - 'A' + 10;
        return -1;
    }
}

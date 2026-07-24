using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Strict, size-bounded reading and writing of the optional trailer header block that follows the
///     terminating zero-length chunk of a chunked message body (RFC 9112 §7.1.2 / RFC 9110 §6.5).
///     Centralized here so every read/write code path (pass-through relay, per-chunk body-write hook,
///     buffered/decompressing reads, and buffered/streamed writes) parses and emits trailers identically,
///     and - critically - always fully consumes through the terminating blank line even when the caller
///     does not care about the parsed result, so a pooled keep-alive connection never retains stray
///     trailer bytes that would corrupt the next message.
/// </summary>
internal static class ChunkedTrailerHelper
{
    /// <summary>
    ///     Maximum number of trailer header lines accepted from the wire. Bounds worst-case parsing time
    ///     for a hostile/broken peer that never sends the terminating blank line.
    /// </summary>
    internal const int MaxTrailerHeaderCount = 100;

    /// <summary>
    ///     Maximum total size (in characters) of the trailer header block accepted from the wire. Bounds
    ///     worst-case memory use for the same scenario.
    /// </summary>
    internal const int MaxTrailerHeaderBlockSize = 16 * 1024;

    /// <summary>
    ///     Header field names that RFC 9110 §6.5.1 says a sender should not (and Titanium will not) generate
    ///     in a trailer, because they are either framing-critical or only meaningful before the body starts.
    ///     Reading/relaying an origin's own (possibly non-compliant) trailer is not gated by this list -
    ///     this only guards the write side, i.e. trailers Titanium itself emits.
    /// </summary>
    private static readonly HashSet<string> ForbiddenTrailerFields = new(StringComparer.OrdinalIgnoreCase)
    {
        KnownHeaders.TransferEncoding.String,
        KnownHeaders.ContentLength.String,
        KnownHeaders.Trailer.String,
        KnownHeaders.Host.String
    };

    /// <summary>
    ///     Reads the trailer block following a zero-length chunk, strictly through the terminating blank
    ///     line. Parsed header lines are added to <paramref name="into" />; when <paramref name="rawLines" />
    ///     is supplied, the exact (unparsed) line text is also collected so a pure pass-through relay can
    ///     forward the trailer byte-for-byte instead of re-serializing a normalized <see cref="HeaderCollection" />.
    /// </summary>
    /// <param name="reader">The line-oriented stream positioned right after the zero-length chunk's size line.</param>
    /// <param name="into">Collection to populate with the parsed trailer headers (usually a request/response's <see cref="RequestResponseBase.TrailingHeaders" />).</param>
    /// <param name="rawLines">Optional list to receive the raw (unparsed) trailer lines, in order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ProxyHttpException">The trailer block is malformed, or exceeds the count/size bounds above.</exception>
    internal static async ValueTask ReadTrailingHeaders(ILineStream reader, HeaderCollection into,
        List<string>? rawLines, CancellationToken cancellationToken = default)
    {
        var count = 0;
        var totalSize = 0;

        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
        {
            count++;
            if (count > MaxTrailerHeaderCount)
                throw new ProxyHttpException(
                    $"Chunked trailer has too many header lines (> {MaxTrailerHeaderCount}).", null, null);

            totalSize += line!.Length;
            if (totalSize > MaxTrailerHeaderBlockSize)
                throw new ProxyHttpException(
                    $"Chunked trailer exceeds the maximum allowed size of {MaxTrailerHeaderBlockSize} bytes.",
                    null, null);

            var colonIndex = line.IndexOf(':');
            if (colonIndex == -1)
                throw new ProxyHttpException($"Invalid trailer header line: '{line}'.", null, null);

            rawLines?.Add(line);

            var name = line.AsSpan(0, colonIndex).ToString();
            var value = line.AsSpan(colonIndex + 1).TrimStart().ToString();
            into.AddHeader(name, value);
        }
    }

    /// <summary>
    ///     Writes the trailer block for <paramref name="trailingHeaders" /> (if any) followed by the
    ///     terminating blank line. Always writes the blank line, even when there are no trailers, so the
    ///     chunked framing is valid either way. Used for buffered/modified/synthetic writes, where the
    ///     header collection is necessarily re-serialized rather than forwarded byte-for-byte.
    /// </summary>
    /// <exception cref="ProxyHttpException">
    ///     <paramref name="trailingHeaders" /> contains a header field that is forbidden in a trailer.
    /// </exception>
    internal static async ValueTask WriteTrailingHeadersAsync(IHttpStreamWriter writer,
        HeaderCollection? trailingHeaders, CancellationToken cancellationToken = default)
    {
        if (trailingHeaders != null)
            foreach (var header in trailingHeaders)
            {
                EnsureTrailerFieldPermitted(header.Name);
                await writer.WriteLineAsync($"{header.Name}: {header.Value}", cancellationToken);
            }

        await writer.WriteLineAsync(cancellationToken);
    }

    /// <summary>
    ///     Writes previously-collected raw trailer lines (see <paramref name="rawLines" /> in
    ///     <see cref="ReadTrailingHeaders" />) verbatim, followed by the terminating blank line. Used by a
    ///     pure pass-through relay to preserve the origin's exact trailer bytes/order instead of
    ///     re-serializing them through a parsed <see cref="HeaderCollection" />. Not subject to the
    ///     forbidden-field check in <see cref="WriteTrailingHeadersAsync" />: the proxy is transparently
    ///     forwarding the origin's own wire data here, not generating it.
    /// </summary>
    internal static async ValueTask WriteRawTrailingLinesAsync(IHttpStreamWriter writer, List<string>? rawLines,
        CancellationToken cancellationToken = default)
    {
        if (rawLines != null)
            foreach (var line in rawLines)
                await writer.WriteLineAsync(line, cancellationToken);

        await writer.WriteLineAsync(cancellationToken);
    }

    private static void EnsureTrailerFieldPermitted(string headerName)
    {
        if (ForbiddenTrailerFields.Contains(headerName))
            throw new ProxyHttpException(
                $"'{headerName}' is not permitted as a trailer field (RFC 9110 §6.5.1).", null, null);
    }
}

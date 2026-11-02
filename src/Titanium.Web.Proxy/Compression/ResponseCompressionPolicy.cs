using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Titanium.Web.Proxy.Abstractions.Compression;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     Compiled <c>server.responseCompression</c>: decides whether one buffered origin response is
///     compressed for its client, compresses it, and rewrites the headers. Built once per config.
/// </summary>
internal sealed class ResponseCompressionPolicy
{
    private static readonly string[] DefaultMimeTypes =
    [
        "text/*",
        "application/json",
        "application/javascript",
        "application/xml",
        "image/svg+xml"
    ];

    private readonly HttpCompression[] algorithms;
    private readonly string[] exactTypes;
    private readonly string[] familyPrefixes;

    private ResponseCompressionPolicy(ResponseCompressionConfig config)
    {
        algorithms = ParseAlgorithms(config.Algorithms);
        Streaming = IsStreamingMode(config.Mode);
        MinBytes = config.MinBytes;
        MaxBytes = config.MaxBytes;
        Level = ParseLevel(config.Level);

        var exact = new List<string>();
        var families = new List<string>();
        foreach (var raw in config.MimeTypes is { Count: > 0 } configured ? configured : DefaultMimeTypes)
        {
            var type = raw.Trim().ToLowerInvariant();
            if (type.Length == 0)
                continue;
            if (type.EndsWith("/*", StringComparison.Ordinal))
                families.Add(type[..^1]);
            else
                exact.Add(type);
        }

        exactTypes = [.. exact];
        familyPrefixes = [.. families];
    }

    internal int MinBytes { get; }

    internal int MaxBytes { get; }

    internal CompressionLevel Level { get; }

    /// <summary>True for <c>mode: streaming</c>: the body is compressed while it is relayed, never buffered.</summary>
    internal bool Streaming { get; }

    /// <summary>True when <paramref name="mode" /> is unset (buffered) or names a known mode.</summary>
    internal static bool TryParseMode(string? mode, out bool streaming)
    {
        streaming = false;
        if (string.IsNullOrWhiteSpace(mode)
            || mode.Trim().Equals(ResponseCompressionConfig.ModeBuffered, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!mode.Trim().Equals(ResponseCompressionConfig.ModeStreaming, StringComparison.OrdinalIgnoreCase))
            return false;

        streaming = true;
        return true;
    }

    private static bool IsStreamingMode(string? mode) => TryParseMode(mode, out var streaming) && streaming;

    /// <summary>Null (off) unless the config is present and enabled.</summary>
    internal static ResponseCompressionPolicy? Create(ResponseCompressionConfig? config)
    {
        if (config is not { Enabled: true })
            return null;

        ValidateConfiguredCodecs(config);
        return new ResponseCompressionPolicy(config);
    }

    /// <summary>
    ///     Ensures every configured algorithm that needs a registered codec (for example <c>zstd</c>) is present.
    /// </summary>
    internal static void ValidateConfiguredCodecs(ResponseCompressionConfig config)
    {
        if (config.Algorithms is not { Count: > 0 } list)
            return;

        foreach (var raw in list)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (raw.Trim().Equals("zstd", StringComparison.OrdinalIgnoreCase)
                && !ContentCodecs.IsRegistered("zstd"))
            {
                throw new InvalidOperationException(
                    "server.responseCompression lists zstd but no zstd codec is registered. " +
                    "Register one through ContentCodecs (the CLI does this when zstd is configured) " +
                    "or remove zstd from algorithms.");
            }
        }
    }

    /// <summary>True when the matched route opted out with <c>compression: off</c>.</summary>
    internal static bool IsRouteOff(RouteConfig? route) =>
        route?.Compression is { Length: > 0 } mode && mode.Equals("off", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseLevel(string? level, out CompressionLevel parsed)
    {
        parsed = CompressionLevel.Fastest;
        if (string.IsNullOrWhiteSpace(level) || level.Equals("fastest", StringComparison.OrdinalIgnoreCase))
            return true;
        if (level.Equals("optimal", StringComparison.OrdinalIgnoreCase))
        {
            parsed = CompressionLevel.Optimal;
            return true;
        }

        if (level.Equals("smallestSize", StringComparison.OrdinalIgnoreCase))
        {
            parsed = CompressionLevel.SmallestSize;
            return true;
        }

        return false;
    }

    private static CompressionLevel ParseLevel(string? level) =>
        TryParseLevel(level, out var parsed) ? parsed : CompressionLevel.Fastest;

    private static HttpCompression[] ParseAlgorithms(IList<string>? configured)
    {
        if (configured is not { Count: > 0 })
            return [HttpCompression.Brotli, HttpCompression.Gzip];

        var result = new List<HttpCompression>(2);
        foreach (var name in configured)
        {
            if (!HttpCompressionNames.TryParse(name, out var algorithm))
                continue;
            if (!result.Contains(algorithm))
                result.Add(algorithm);
        }

        return result.Count > 0 ? [.. result] : [HttpCompression.Brotli, HttpCompression.Gzip];
    }

    /// <summary>
    ///     Applies every eligibility rule. All must hold: the client accepts the encoding; status 200 (not 206);
    ///     a known Content-Length between <see cref="MinBytes" /> and <see cref="MaxBytes" />; no
    ///     Content-Encoding; no <c>Cache-Control: no-transform</c>; not HEAD, Range, event-stream, gRPC or a
    ///     WebSocket upgrade; and a configured media type.
    /// </summary>
    internal bool TrySelect(Request request, Response response, out HttpCompression algorithm)
    {
        algorithm = HttpCompression.Unsupported;

        if (response.IsChunked)
            return false;

        var length = response.ContentLength;
        if (length < MinBytes || length > MaxBytes)
            return false;

        return TrySelectCore(request, response, out algorithm);
    }

    /// <summary>
    ///     Streaming variant of <see cref="TrySelect" />: the same rules, except that a chunked or close-delimited
    ///     body is eligible, a known Content-Length is only held to <see cref="MinBytes" />, and there is no upper
    ///     bound because nothing is buffered. The body has to be relayed by the caller.
    /// </summary>
    internal bool TrySelectStreaming(Request request, Response response, out HttpCompression algorithm)
    {
        algorithm = HttpCompression.Unsupported;

        var length = response.ContentLength;
        if (!response.IsChunked && length >= 0 && length < MinBytes)
            return false;

        return TrySelectCore(request, response, out algorithm);
    }

    private bool TrySelectCore(Request request, Response response, out HttpCompression algorithm)
    {
        algorithm = HttpCompression.Unsupported;

        if (response.StatusCode != 200)
            return false;

        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || request.UpgradeToWebSocket
            || request.Headers.HeaderExists("Range"))
            return false;

        if (response.Headers.HeaderExists("Content-Encoding") || response.Headers.HeaderExists("Content-Range"))
            return false;

        if (HasToken(response.Headers.GetHeaders("Cache-Control"), "no-transform"))
            return false;

        if (!IsCompressibleType(response.ContentType))
            return false;

        var accept = request.Headers.GetHeaders("Accept-Encoding");
        if (accept is null)
            return false;

        // The highest client weight wins; the configured order only breaks a tie.
        var best = 0d;
        foreach (var candidate in algorithms)
        {
            var token = HttpCompressionNames.WireToken(candidate);
            if (token.Length == 0)
                continue;

            var weight = Weight(accept, token);
            if (weight > best)
            {
                best = weight;
                algorithm = candidate;
            }
        }

        return algorithm != HttpCompression.Unsupported;
    }

    /// <summary>Compresses <paramref name="body" /> with the configured level.</summary>
    internal byte[] Compress(HttpCompression algorithm, byte[] body)
    {
        using var buffer = new MemoryStream(Math.Max(256, body.Length / 2));
        using (var encoder = CompressionFactory.Create(algorithm, buffer, Level))
        {
            encoder.Write(body, 0, body.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Stores the compressed body and rewrites the headers: sets Content-Encoding, replaces Content-Length,
    ///     adds <c>Vary: Accept-Encoding</c>, and weakens a strong ETag.
    /// </summary>
    internal static void Apply(Response response, HttpCompression algorithm, byte[] compressed)
    {
        response.Body = compressed;
        response.BodyIsWireEncoded = true;

        response.Headers.RemoveHeader(KnownHeaders.ContentLength);
        response.ContentLength = compressed.Length;

        RewriteHeaders(response, algorithm);
    }

    /// <summary>
    ///     Rewrites the headers for a body that is compressed while it is relayed: sets Content-Encoding, drops
    ///     Content-Length (the compressed length is unknown), frames the body as chunked, adds
    ///     <c>Vary: Accept-Encoding</c>, and weakens a strong ETag.
    /// </summary>
    internal static void ApplyStreaming(Response response, HttpCompression algorithm)
    {
        response.Headers.RemoveHeader(KnownHeaders.ContentLength);
        response.IsChunked = true;

        RewriteHeaders(response, algorithm);
    }

    private static void RewriteHeaders(Response response, HttpCompression algorithm)
    {
        response.Headers.SetOrAddHeaderValue(KnownHeaders.ContentEncoding,
            HttpCompressionNames.WireToken(algorithm));

        if (!HasToken(response.Headers.GetHeaders("Vary"), "accept-encoding")
            && !HasToken(response.Headers.GetHeaders("Vary"), "*"))
        {
            response.Headers.AddHeader("Vary", "Accept-Encoding");
        }

        WeakenETag(response);
    }

    private static void WeakenETag(Response response)
    {
        if (response.Headers.GetFirstHeader("ETag") is not { } etag)
            return;

        var value = etag.Value.Trim();
        if (value.Length == 0 || value.StartsWith("W/", StringComparison.Ordinal))
            return;

        response.Headers.RemoveHeader("ETag");
        response.Headers.AddHeader(etag.Name, "W/" + value);
    }

    private bool IsCompressibleType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var semicolon = contentType.IndexOf(';');
        var type = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
        if (type.Length == 0
            || type == "text/event-stream"
            || type.StartsWith("application/grpc", StringComparison.Ordinal))
            return false;

        if (exactTypes.Any(exact => type == exact))
        {
            return true;
        }

        return familyPrefixes.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>True when a comma-separated header value list contains <paramref name="token" /> (case-insensitive).</summary>
    private static bool HasToken(List<HttpHeader>? headers, string token)
    {
        if (headers is null)
            return false;

        foreach (var header in headers)
        {
            foreach (var part in header.Value.Split(','))
            {
                var directive = part.Trim();
                var equals = directive.IndexOf('=');
                if (equals >= 0)
                    directive = directive[..equals].TrimEnd();
                if (directive.Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     RFC 9110 §12.5.3: an explicit coding beats <c>*</c>, and a zero weight forbids it.
    /// </summary>
    internal static bool Accepts(List<HttpHeader> acceptEncoding, string coding) =>
        Weight(acceptEncoding, coding) > 0;

    /// <summary>
    ///     The client's weight for <paramref name="coding" />: its explicit <c>q</c>, else the <c>*</c> weight,
    ///     else 0. A coding the client does not list (or lists with <c>q=0</c>) has weight 0.
    /// </summary>
    internal static double Weight(List<HttpHeader> acceptEncoding, string coding) // NOSONAR S3776 -- Accept-Encoding q parsing shares one pass over comma-separated tokens.
    {
        var explicitWeight = -1d;
        var starWeight = -1d;
        foreach (var header in acceptEncoding)
        {
            foreach (var part in header.Value.Split(','))
            {
                var fields = part.Split(';');
                var name = fields[0].Trim();
                if (name.Length == 0)
                    continue;

                var weight = 1d;
                for (var i = 1; i < fields.Length; i++)
                {
                    var parameter = fields[i].Trim();
                    if (parameter.Length > 2 && (parameter[0] is 'q' or 'Q') && parameter[1] == '='
                        && double.TryParse(parameter.AsSpan(2), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        weight = parsed;
                    }
                }

                if (name.Equals(coding, StringComparison.OrdinalIgnoreCase))
                    explicitWeight = Math.Max(explicitWeight, weight);
                else if (name == "*")
                    starWeight = Math.Max(starWeight, weight);
            }
        }

        return Math.Max(0d, explicitWeight >= 0 ? explicitWeight : starWeight);
    }
}

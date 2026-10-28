using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    /// <summary>Null (off) unless the config is present and enabled.</summary>
    internal static ResponseCompressionPolicy? Create(ResponseCompressionConfig? config) =>
        config is { Enabled: true } ? new ResponseCompressionPolicy(config) : null;

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
            var algorithm = name.Trim().ToLowerInvariant() switch
            {
                "br" => HttpCompression.Brotli,
                "gzip" => HttpCompression.Gzip,
                _ => HttpCompression.Unsupported
            };
            if (algorithm != HttpCompression.Unsupported && !result.Contains(algorithm))
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

        if (response.StatusCode != 200)
            return false;

        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || request.UpgradeToWebSocket
            || request.Headers.HeaderExists("Range"))
            return false;

        if (response.IsChunked || response.Headers.HeaderExists("Content-Encoding"))
            return false;

        var length = response.ContentLength;
        if (length < MinBytes || length > MaxBytes)
            return false;

        if (HasToken(response.Headers.GetHeaders("Cache-Control"), "no-transform"))
            return false;

        if (!IsCompressibleType(response.ContentType))
            return false;

        var accept = request.Headers.GetHeaders("Accept-Encoding");
        if (accept is null)
            return false;

        foreach (var candidate in algorithms)
        {
            if (Accepts(accept, candidate == HttpCompression.Brotli ? "br" : "gzip"))
            {
                algorithm = candidate;
                return true;
            }
        }

        return false;
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

        response.Headers.SetOrAddHeaderValue(KnownHeaders.ContentEncoding,
            algorithm == HttpCompression.Brotli ? KnownHeaders.ContentEncodingBrotli : KnownHeaders.ContentEncodingGzip);

        response.Headers.RemoveHeader(KnownHeaders.ContentLength);
        response.ContentLength = compressed.Length;

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

        foreach (var exact in exactTypes)
        {
            if (type == exact)
                return true;
        }

        foreach (var prefix in familyPrefixes)
        {
            if (type.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
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
    internal static bool Accepts(List<HttpHeader> acceptEncoding, string coding)
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

        return explicitWeight >= 0 ? explicitWeight > 0 : starWeight > 0;
    }
}

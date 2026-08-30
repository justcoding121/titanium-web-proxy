using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Titanium.Cli;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.StaticFiles;

/// <summary>Serves files from a root directory with ETag/Range and optional gzip/brotli (prefix-gated).</summary>
internal static class StaticFileHost
{
    private static readonly ConcurrentDictionary<string, CachedStaticFile> ContentCache = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterIfNeeded(ProxyServer proxy, StaticFilesConfig? config, bool sessionPathEnabled)
    {
        if (config is null || string.IsNullOrEmpty(config.Root))
        {
            return;
        }

        if (!sessionPathEnabled)
        {
            throw new InvalidOperationException("Static files require session path; internal inconsistency.");
        }

        var root = Path.GetFullPath(config.Root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Static files root not found: {root}");
        }

        AsyncConsole.WriteLine($"Static files root: {root} (gzip={config.EnableGzip}, brotli={config.EnableBrotli})");

        proxy.BeforeRequest += (_, e) => HandleStaticRequestAsync(e, root, config);
    }

    private static async Task HandleStaticRequestAsync(
        SessionEventArgs e, string root, StaticFilesConfig config)
    {
        var requestPath = e.HttpClient.Request.RequestUri?.AbsolutePath ?? "/";
        if (requestPath.Contains("..", StringComparison.Ordinal))
        {
            e.GenericResponse("Invalid path", HttpStatusCode.BadRequest);
            return;
        }

        var resolved = await TryResolveCachedAsync(requestPath, root).ConfigureAwait(false);
        if (resolved is null)
        {
            return;
        }

        var (full, bytes, length, lastWrite) = resolved.Value;
        var etag = $"W/\"{length:x}-{lastWrite.Ticks:x}\"";
        if (TryNotModified(e, etag))
        {
            return;
        }

        // Range/compression may replace the array; never mutate the cache entry.
        var responseBytes = bytes;
        var status = HttpStatusCode.OK;
        var headers = CreateBaseHeaders(full, etag);

        if (TryApplyRange(e, ref responseBytes, length, headers, out var partial))
        {
            status = partial;
        }

        ApplyCompression(config, e, ref responseBytes, headers);

        if (status == HttpStatusCode.OK)
        {
            e.Ok(responseBytes, (IEnumerable<HttpHeader>)headers);
        }
        else
        {
            e.GenericResponse(responseBytes, status, headers);
        }
    }

    private static async Task<(string Full, byte[] Bytes, long Length, DateTime LastWriteUtc)?> TryResolveCachedAsync(
        string requestPath, string root)
    {
        var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(relative))
        {
            relative = "index.html";
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (ContentCache.TryGetValue(candidate, out var cached))
        {
            return (candidate, cached.Bytes, cached.Length, cached.LastWriteTimeUtc);
        }

        if (!File.Exists(candidate))
        {
            return null;
        }

        var info = new FileInfo(candidate);
        var bytes = await File.ReadAllBytesAsync(candidate).ConfigureAwait(false);
        ContentCache[candidate] = new CachedStaticFile(bytes, info.Length, info.LastWriteTimeUtc);
        return (candidate, bytes, info.Length, info.LastWriteTimeUtc);
    }

    private sealed record CachedStaticFile(byte[] Bytes, long Length, DateTime LastWriteTimeUtc);

    private static bool TryNotModified(SessionEventArgs e, string etag)
    {
        var ifNoneMatch = e.HttpClient.Request.Headers.GetHeaders("If-None-Match");
        if (ifNoneMatch is not { Count: > 0 } ||
            !ifNoneMatch.Any(h => ETagMatches(h.Value, etag)))
        {
            return false;
        }

        e.GenericResponse(
            Array.Empty<byte>(),
            HttpStatusCode.NotModified,
            [
                new HttpHeader("ETag", etag),
                new HttpHeader("Cache-Control", "public, max-age=60"),
                new HttpHeader("Accept-Ranges", "bytes"),
            ]);
        return true;
    }

    private static List<HttpHeader> CreateBaseHeaders(string fullPath, string etag) =>
    [
        new("Content-Type", GuessContentType(fullPath)),
        new("Cache-Control", "public, max-age=60"),
        new("ETag", etag),
        new("Accept-Ranges", "bytes"),
    ];

    private static bool TryApplyRange(
        SessionEventArgs e,
        ref byte[] bytes,
        long totalLength,
        List<HttpHeader> headers,
        out HttpStatusCode status)
    {
        status = HttpStatusCode.OK;
        var rangeHeaders = e.HttpClient.Request.Headers.GetHeaders("Range");
        if (rangeHeaders is not { Count: > 0 } ||
            !TryParseBytesRange(rangeHeaders[0].Value, bytes.Length, out var start, out var end))
        {
            return false;
        }

        var length = end - start + 1;
        var slice = new byte[length];
        Buffer.BlockCopy(bytes, start, slice, 0, length);
        bytes = slice;
        status = HttpStatusCode.PartialContent;
        headers.Add(new HttpHeader("Content-Range", $"bytes {start}-{end}/{totalLength}"));
        return true;
    }

    private static void ApplyCompression(
        StaticFilesConfig config,
        SessionEventArgs e,
        ref byte[] bytes,
        List<HttpHeader> headers)
    {
        var acceptHeaders = e.HttpClient.Request.Headers.GetHeaders("Accept-Encoding");
        var accept = acceptHeaders is null
            ? ""
            : string.Join(",", acceptHeaders.Select(h => h.Value));

        if (config.EnableBrotli && accept.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            bytes = BrotliCompress(bytes);
            headers.Add(new HttpHeader("Content-Encoding", "br"));
        }
        else if (config.EnableGzip && accept.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            bytes = GzipCompress(bytes);
            headers.Add(new HttpHeader("Content-Encoding", "gzip"));
        }
    }

    private static bool ETagMatches(string clientValue, string etag)
    {
        return clientValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("*", StringComparison.Ordinal) ||
                         string.Equals(part, etag, StringComparison.Ordinal));
    }

    private static bool TryParseBytesRange(string? rangeHeader, int totalLength, out int start, out int end)
    {
        start = 0;
        end = totalLength - 1;
        if (string.IsNullOrEmpty(rangeHeader) || totalLength <= 0)
        {
            return false;
        }

        const string prefix = "bytes=";
        if (!rangeHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = rangeHeader[prefix.Length..].Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var startPart = spec[..dash];
        var endPart = spec[(dash + 1)..];

        if (string.IsNullOrEmpty(startPart))
        {
            if (!int.TryParse(endPart, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, totalLength - suffix);
            end = totalLength - 1;
            return start <= end;
        }

        if (!int.TryParse(startPart, out start) || start < 0 || start >= totalLength)
        {
            return false;
        }

        if (string.IsNullOrEmpty(endPart))
        {
            end = totalLength - 1;
        }
        else if (!int.TryParse(endPart, out end) || end < start)
        {
            return false;
        }
        else
        {
            end = Math.Min(end, totalLength - 1);
        }

        return true;
    }

    internal static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static byte[] GzipCompress(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(input);
        }

        return ms.ToArray();
    }

    private static byte[] BrotliCompress(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            br.Write(input);
        }

        return ms.ToArray();
    }
}

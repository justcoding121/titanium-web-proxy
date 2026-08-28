using System.IO.Compression;
using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.StaticFiles;

/// <summary>Serves files from a root directory with ETag/Range and optional gzip/brotli (prefix-gated).</summary>
internal static class StaticFileHost
{
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

        Console.WriteLine($"Static files root: {root} (gzip={config.EnableGzip}, brotli={config.EnableBrotli})");

        proxy.BeforeRequest += async (_, e) =>
        {
            var path = e.HttpClient.Request.RequestUri?.AbsolutePath ?? "/";
            if (path.Contains("..", StringComparison.Ordinal))
            {
                e.GenericResponse("Invalid path", HttpStatusCode.BadRequest);
                return;
            }

            var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative))
            {
                relative = "index.html";
            }

            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return; // fall through to reverse proxy / origin
            }

            var info = new FileInfo(full);
            var etag = $"W/\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";

            var ifNoneMatch = e.HttpClient.Request.Headers.GetHeaders("If-None-Match");
            if (ifNoneMatch is { Count: > 0 } &&
                ifNoneMatch.Any(h => ETagMatches(h.Value, etag)))
            {
                e.GenericResponse(
                    Array.Empty<byte>(),
                    HttpStatusCode.NotModified,
                    new[]
                    {
                        new HttpHeader("ETag", etag),
                        new HttpHeader("Cache-Control", "public, max-age=60"),
                        new HttpHeader("Accept-Ranges", "bytes"),
                    });
                return;
            }

            var bytes = await File.ReadAllBytesAsync(full);
            var status = HttpStatusCode.OK;
            var headers = new List<HttpHeader>
            {
                new("Content-Type", GuessContentType(full)),
                new("Cache-Control", "public, max-age=60"),
                new("ETag", etag),
                new("Accept-Ranges", "bytes"),
            };

            var rangeHeaders = e.HttpClient.Request.Headers.GetHeaders("Range");
            if (rangeHeaders is { Count: > 0 } &&
                TryParseBytesRange(rangeHeaders[0].Value, bytes.Length, out var start, out var end))
            {
                var length = end - start + 1;
                var slice = new byte[length];
                Buffer.BlockCopy(bytes, start, slice, 0, length);
                bytes = slice;
                status = HttpStatusCode.PartialContent;
                headers.Add(new HttpHeader("Content-Range", $"bytes {start}-{end}/{info.Length}"));
            }

            // Compression applied after range so Content-Range describes the identity representation slice.
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

            if (status == HttpStatusCode.OK)
            {
                e.Ok(bytes, (IEnumerable<HttpHeader>)headers);
            }
            else
            {
                e.GenericResponse(bytes, status, headers);
            }
        };
    }

    private static bool ETagMatches(string clientValue, string etag)
    {
        foreach (var part in clientValue.Split(',',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("*", StringComparison.Ordinal) ||
                string.Equals(part, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
        // Only single range supported.
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
            // suffix: bytes=-N
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

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
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

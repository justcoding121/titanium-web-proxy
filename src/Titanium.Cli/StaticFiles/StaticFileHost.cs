using System.IO.Compression;
using System.Net;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.StaticFiles;

/// <summary>Serves files from a root directory with optional gzip/brotli (prefix-gated).</summary>
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

            var bytes = await File.ReadAllBytesAsync(full);
            var acceptHeaders = e.HttpClient.Request.Headers.GetHeaders("Accept-Encoding");
            var accept = acceptHeaders is null
                ? ""
                : string.Join(",", acceptHeaders.Select(h => h.Value));
            var headers = new List<HttpHeader>
            {
                new("Content-Type", GuessContentType(full)),
                new("Cache-Control", "public, max-age=60"),
            };

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

            e.Ok(bytes, (IEnumerable<HttpHeader>)headers);
        };
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

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>Headers/cookies/query + raw/JSON/hex body views with optional decompress.</summary>
public static class SessionInspectors
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static IReadOnlyDictionary<string, string> ParseHeaderBlock(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            map[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return map;
    }

    public static IReadOnlyDictionary<string, string> ParseCookies(IReadOnlyDictionary<string, string> headers)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!headers.TryGetValue("Cookie", out var cookie) && !headers.TryGetValue("Set-Cookie", out cookie))
        {
            return cookies;
        }

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            cookies[part[..eq]] = part[(eq + 1)..];
        }

        return cookies;
    }

    public static IReadOnlyDictionary<string, string> ParseQuery(string url)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0 || q == url.Length - 1)
        {
            return map;
        }

        foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                map[Uri.UnescapeDataString(pair)] = "";
            }
            else
            {
                map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return map;
    }

    public static string ToHex(byte[]? bytes, int maxBytes = 4096)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }

        var len = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(len * 3);
        for (var i = 0; i < len; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("X2"));
        }

        if (bytes.Length > maxBytes)
        {
            sb.Append(" …");
        }

        return sb.ToString();
    }

    public static string TryFormatJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? "";
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static byte[]? TryDecompress(byte[]? body, string? contentEncoding)
    {
        if (body is null || body.Length == 0 || string.IsNullOrEmpty(contentEncoding))
        {
            return body;
        }

        try
        {
            using var input = new MemoryStream(body);
            Stream codec = contentEncoding.ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => null!,
            };
            if (codec is null)
            {
                return body;
            }

            using (codec)
            using (var output = new MemoryStream())
            {
                codec.CopyTo(output);
                return output.ToArray();
            }
        }
        catch
        {
            return body;
        }
    }
}

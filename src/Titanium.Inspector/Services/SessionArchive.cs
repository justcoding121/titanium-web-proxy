using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>HAR + native session archive zip import/export.</summary>
public static class SessionArchive
{
    private static readonly JsonSerializerOptions HarJson = new() { WriteIndented = true };

    public static async Task ExportHarAsync(IEnumerable<SessionSnapshot> sessions, string path, CancellationToken ct = default)
    {
        var entries = sessions.Select(ToHarEntry).ToList();
        var har = new
        {
            log = new
            {
                version = "1.2",
                creator = new { name = "Titanium Inspector", version = "7.0.0" },
                entries,
            },
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(har, HarJson), ct);
    }

    public static async Task<List<SessionSnapshot>> ImportHarAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
        var list = new List<SessionSnapshot>();
        if (!doc.RootElement.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        long id = 1;
        foreach (var entry in entries.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var snap = FromHarEntry(entry, id++);
            if (snap is not null)
            {
                list.Add(snap);
            }
        }

        return list;
    }

    public static async Task ExportNativeArchiveAsync(IEnumerable<SessionSnapshot> sessions, string zipPath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            zipPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = 0;
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry($"session-{index:D5}.json");
                await using var stream = await entry.OpenAsync(ct);
                await JsonSerializer.SerializeAsync(stream, session, cancellationToken: ct);
                index++;
            }
        }

        await fs.FlushAsync(ct);
    }

    public static async Task<List<SessionSnapshot>> ImportNativeArchiveAsync(string zipPath, CancellationToken ct = default)
    {
        var list = new List<SessionSnapshot>();
        await using var fs = new FileStream(
            zipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var stream = await entry.OpenAsync(ct);
            var snap = await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, cancellationToken: ct);
            if (snap is not null)
            {
                list.Add(snap);
            }
        }

        return list;
    }

    private static object ToHarEntry(SessionSnapshot s)
    {
        var duration = s.DurationMs ?? 0;
        var wait = s.TtfbMs ?? Math.Max(0, duration * 0.6);
        var receive = Math.Max(0, duration - wait);
        var send = Math.Max(0, duration * 0.05);
        var mime = GuessMime(s);
        var postText = s.RequestBodyText;
        var contentText = s.ResponseBodyText;

        return new
        {
            startedDateTime = s.StartedUtc.UtcDateTime.ToString("o"),
            time = duration,
            request = new
            {
                method = s.Method,
                url = s.Url,
                httpVersion = "HTTP/1.1",
                headers = ParseHeaders(s.RequestHeadersText),
                queryString = ParseQueryString(s.Url),
                cookies = Array.Empty<object>(),
                headersSize = -1,
                bodySize = s.RequestBodyBytes?.Length ?? s.RequestBodyText?.Length ?? -1,
                postData = string.IsNullOrEmpty(postText)
                    ? null
                    : new { mimeType = s.ContentType ?? "text/plain", text = postText },
            },
            response = new
            {
                status = s.StatusCode ?? 0,
                statusText = "",
                httpVersion = "HTTP/1.1",
                headers = ParseHeaders(s.ResponseHeadersText),
                cookies = Array.Empty<object>(),
                content = new
                {
                    size = s.ResponseBodyBytes?.Length ?? s.ResponseBodyText?.Length ?? 0,
                    mimeType = mime,
                    text = contentText ?? "",
                },
                redirectURL = "",
                headersSize = -1,
                bodySize = s.ResponseBodyBytes?.Length ?? s.ResponseBodyText?.Length ?? -1,
            },
            timings = new
            {
                blocked = -1,
                dns = -1,
                connect = -1,
                ssl = -1,
                send,
                wait,
                receive,
            },
            cache = new { },
        };
    }

    private static SessionSnapshot? FromHarEntry(JsonElement entry, long id)
    {
        try
        {
            if (!TryReadHarRequest(entry, out var method, out var url, out var host, out var reqHeaders, out var reqBody, out var contentType))
            {
                return null;
            }

            TryReadHarResponse(entry, out var status, out var respHeaders, out var respBody, out var respMime);
            ReadHarTiming(entry, out var durationMs, out var ttfbMs, out var started);

            return new SessionSnapshot
            {
                Id = id,
                Method = method,
                Url = url,
                Host = host,
                StartedUtc = started,
                StatusCode = status,
                RequestHeadersText = reqHeaders,
                ResponseHeadersText = respHeaders,
                RequestBodyText = reqBody,
                ResponseBodyText = respBody,
                ContentType = contentType ?? respMime,
                DurationMs = durationMs,
                TtfbMs = ttfbMs,
                BodySize = respBody?.Length,
                Protocol = "HAR",
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadHarRequest(
        JsonElement entry,
        out string method,
        out string url,
        out string? host,
        out string reqHeaders,
        out string? reqBody,
        out string? contentType)
    {
        method = "GET";
        url = "";
        host = null;
        reqHeaders = "";
        reqBody = null;
        contentType = null;

        if (!entry.TryGetProperty("request", out var req))
        {
            return false;
        }

        method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
        url = req.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }

        reqHeaders = FormatHarHeaders(req);
        if (req.TryGetProperty("postData", out var post) && post.ValueKind == JsonValueKind.Object)
        {
            if (post.TryGetProperty("text", out var pt))
            {
                reqBody = pt.GetString();
            }

            if (post.TryGetProperty("mimeType", out var mt))
            {
                contentType = mt.GetString();
            }
        }

        return true;
    }

    private static void TryReadHarResponse(
        JsonElement entry,
        out int? status,
        out string? respHeaders,
        out string? respBody,
        out string? respMime)
    {
        status = null;
        respHeaders = null;
        respBody = null;
        respMime = null;

        if (!entry.TryGetProperty("response", out var resp))
        {
            return;
        }

        if (resp.TryGetProperty("status", out var st) && st.TryGetInt32(out var code))
        {
            status = code;
        }

        respHeaders = FormatHarHeaders(resp);
        if (resp.TryGetProperty("content", out var content))
        {
            if (content.TryGetProperty("text", out var ct))
            {
                respBody = ct.GetString();
            }

            if (content.TryGetProperty("mimeType", out var mime))
            {
                respMime = mime.GetString();
            }
        }
    }

    private static void ReadHarTiming(
        JsonElement entry,
        out double? durationMs,
        out double? ttfbMs,
        out DateTimeOffset started)
    {
        durationMs = null;
        ttfbMs = null;
        started = DateTimeOffset.UtcNow;

        if (entry.TryGetProperty("time", out var timeEl) && timeEl.TryGetDouble(out var time))
        {
            durationMs = time;
        }

        if (entry.TryGetProperty("timings", out var timings) &&
            timings.TryGetProperty("wait", out var waitEl) &&
            waitEl.TryGetDouble(out var wait) &&
            wait >= 0)
        {
            ttfbMs = wait;
        }

        if (entry.TryGetProperty("startedDateTime", out var startedEl) &&
            DateTimeOffset.TryParse(
                startedEl.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            started = parsed;
        }
    }

    private static string FormatHarHeaders(JsonElement parent)
    {
        if (!parent.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var h in headers.EnumerateArray())
        {
            var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = h.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (!string.IsNullOrEmpty(name))
            {
                sb.Append(name).Append(": ").Append(value ?? "").AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string GuessMime(SessionSnapshot s)
    {
        var contentType = s.ContentType;
        if (!string.IsNullOrEmpty(contentType))
        {
            return contentType;
        }

        var headers = SessionInspectors.ParseHeaderBlock(s.ResponseHeadersText);
        return headers.TryGetValue("Content-Type", out var ct) ? ct : "application/octet-stream";
    }

    private static object[] ParseQueryString(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0 || q == url.Length - 1)
        {
            return [];
        }

        return url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    return (object)new { name = Decode(pair), value = "" };
                }

                return (object)new { name = Decode(pair[..eq]), value = Decode(pair[(eq + 1)..]) };
            })
            .ToArray();
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch
        {
            return value;
        }
    }

    private static object[] ParseHeaders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var i = line.IndexOf(':');
                return i <= 0
                    ? new { name = line, value = "" }
                    : new { name = line[..i].Trim(), value = line[(i + 1)..].Trim() };
            })
            .Cast<object>()
            .ToArray();
    }
}

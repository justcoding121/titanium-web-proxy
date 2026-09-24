using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Titanium.Inspector.Services;

/// <summary>HAR 1.2 + native session archive zip import/export.</summary>
public static class SessionArchive
{
    private static readonly JsonSerializerOptions HarPretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions HarCompact = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions InspectorJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Task ExportHarAsync(IEnumerable<SessionSnapshot> sessions, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WriteHarDocument(path, sessions, indented: true);
        return Task.CompletedTask;
    }

    /// <summary>Writes a HAR 1.2 document (one or more entries). Used by Export and disk spill.</summary>
    public static void WriteHarDocument(string path, IEnumerable<SessionSnapshot> sessions, bool indented)
    {
        var json = BuildHarDocumentJson(sessions, indented);
        File.WriteAllText(path, json);
    }

    /// <summary>Serializes a single-session HAR 1.2 document (compact) for the disk cache.</summary>
    public static void WriteHarDocument(Stream stream, SessionSnapshot session)
    {
        var node = BuildHarDocument([session]);
        JsonSerializer.Serialize(stream, node, HarCompact);
    }

    public static string BuildHarDocumentJson(IEnumerable<SessionSnapshot> sessions, bool indented)
    {
        var node = BuildHarDocument(sessions);
        return node.ToJsonString(indented ? HarPretty : HarCompact);
    }

    public static JsonObject BuildHarDocument(IEnumerable<SessionSnapshot> sessions)
    {
        var creatorVersion = typeof(SessionArchive).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";
        var entries = new JsonArray();
        foreach (var s in sessions)
        {
            entries.Add(ToHarEntry(s));
        }

        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject
                {
                    ["name"] = "Titanium Inspector",
                    ["version"] = creatorVersion,
                },
                ["entries"] = entries,
            },
        };
    }

    public static async Task<List<SessionSnapshot>> ImportHarAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return await ImportHarAsync(fs, ct).ConfigureAwait(false);
    }

    public static async Task<List<SessionSnapshot>> ImportHarAsync(Stream stream, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseHarDocument(doc.RootElement, startId: 1);
    }

    /// <summary>Imports one or more HAR files (each may contain many entries).</summary>
    public static async Task<List<SessionSnapshot>> ImportHarFilesAsync(
        IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = new List<SessionSnapshot>();
        long nextId = 1;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            await using var fs = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
            var batch = ParseHarDocument(doc.RootElement, nextId);
            list.AddRange(batch);
            nextId += batch.Count;
        }

        return list;
    }

    public static List<SessionSnapshot> ParseHarDocument(JsonElement root, long startId)
    {
        var list = new List<SessionSnapshot>();
        if (!root.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var id = startId;
        foreach (var entry in entries.EnumerateArray())
        {
            var snap = FromHarEntry(entry, id);
            if (snap is not null)
            {
                list.Add(snap);
                id++;
            }
        }

        return list;
    }

    /// <summary>HAR entry object including Titanium <c>_inspector</c> extension fields.</summary>
    public static JsonObject ToHarEntry(SessionSnapshot s)
    {
        var duration = s.DurationMs ?? 0;
        var wait = s.TtfbMs ?? Math.Max(0, duration * 0.6);
        var receive = Math.Max(0, duration - wait);
        var send = Math.Max(0, duration * 0.05);
        var mime = GuessMime(s);

        JsonNode? postDataNode = null;
        if (!string.IsNullOrEmpty(s.RequestBodyText))
        {
            postDataNode = JsonSerializer.SerializeToNode(new
            {
                mimeType = s.ContentType ?? "text/plain",
                text = s.RequestBodyText,
            }, HarCompact);
        }
        else if (s.RequestBodyBytes is { Length: > 0 } reqBytes)
        {
            postDataNode = JsonSerializer.SerializeToNode(new
            {
                mimeType = s.ContentType ?? "application/octet-stream",
                text = Convert.ToBase64String(reqBytes),
            }, HarCompact);
        }

        string? contentText;
        string? contentEncoding = null;
        long contentSize;
        if (s.ResponseBodyBytes is { Length: > 0 } respBytes)
        {
            contentText = Convert.ToBase64String(respBytes);
            contentEncoding = "base64";
            contentSize = respBytes.Length;
        }
        else
        {
            contentText = s.ResponseBodyText ?? "";
            contentSize = contentText.Length;
        }

        var request = new JsonObject
        {
            ["method"] = s.Method,
            ["url"] = s.Url,
            ["httpVersion"] = "HTTP/1.1",
            ["headers"] = JsonSerializer.SerializeToNode(ParseHeaders(s.RequestHeadersText), HarCompact),
            ["queryString"] = JsonSerializer.SerializeToNode(ParseQueryString(s.Url), HarCompact),
            ["cookies"] = new JsonArray(),
            ["headersSize"] = -1,
            ["bodySize"] = s.RequestBodyBytes?.Length ?? s.RequestBodyText?.Length ?? -1,
        };
        if (postDataNode is not null)
        {
            request["postData"] = postDataNode;
        }

        var content = new JsonObject
        {
            ["size"] = contentSize,
            ["mimeType"] = mime,
            ["text"] = contentText,
        };
        if (contentEncoding is not null)
        {
            content["encoding"] = contentEncoding;
        }

        return new JsonObject
        {
            ["startedDateTime"] = s.StartedUtc.UtcDateTime.ToString("o"),
            ["time"] = duration,
            ["request"] = request,
            ["response"] = new JsonObject
            {
                ["status"] = s.StatusCode ?? 0,
                ["statusText"] = "",
                ["httpVersion"] = "HTTP/1.1",
                ["headers"] = JsonSerializer.SerializeToNode(ParseHeaders(s.ResponseHeadersText), HarCompact),
                ["cookies"] = new JsonArray(),
                ["content"] = content,
                ["redirectURL"] = "",
                ["headersSize"] = -1,
                ["bodySize"] = s.ResponseBodyBytes?.Length ?? s.ResponseBodyText?.Length ?? -1,
            },
            ["timings"] = new JsonObject
            {
                ["blocked"] = -1,
                ["dns"] = -1,
                ["connect"] = -1,
                ["ssl"] = -1,
                ["send"] = send,
                ["wait"] = wait,
                ["receive"] = receive,
            },
            ["cache"] = new JsonObject(),
            ["_inspector"] = BuildInspectorExtension(s),
        };
    }

    public static SessionSnapshot? FromHarEntry(JsonElement entry, long id)
    {
        try
        {
            if (!TryReadHarRequest(entry, out var method, out var url, out var host, out var reqHeaders, out var reqBody, out var contentType, out var reqBytes))
            {
                return null;
            }

            TryReadHarResponse(entry, out var status, out var respHeaders, out var respBody, out var respMime, out var respBytes);
            ReadHarTiming(entry, out var durationMs, out var ttfbMs, out var started);

            var snap = new SessionSnapshot
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
                RequestBodyBytes = reqBytes,
                ResponseBodyBytes = respBytes,
                ContentType = contentType ?? respMime,
                DurationMs = durationMs,
                TtfbMs = ttfbMs,
                BodySize = respBytes?.LongLength ?? respBody?.Length,
                Protocol = "HAR",
            };

            ApplyInspectorExtension(entry, snap);
            return snap;
        }
        catch
        {
            return null;
        }
    }

    public static Task ExportNativeArchiveAsync(IEnumerable<SessionSnapshot> sessions, string zipPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var fs = new FileStream(
            zipPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var index = 0;
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry($"session-{index:D5}.json");
                using var stream = entry.Open();
                JsonSerializer.Serialize(stream, session);
                index++;
            }
        }

        fs.Flush();
        return Task.CompletedTask;
    }

    public static Task<List<SessionSnapshot>> ImportNativeArchiveAsync(string zipPath, CancellationToken ct = default)
    {
        var list = new List<SessionSnapshot>();
        using var fs = new FileStream(
            zipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = entry.Open();
            var snap = JsonSerializer.Deserialize<SessionSnapshot>(stream);
            if (snap is not null)
            {
                list.Add(snap);
            }
        }

        return Task.FromResult(list);
    }

    private static JsonObject BuildInspectorExtension(SessionSnapshot s)
    {
        var ext = new InspectorExtension
        {
            Id = s.Id,
            Protocol = s.Protocol,
            Host = s.Host,
            BodySize = s.BodySize,
            ProcessId = s.ProcessId,
            ProcessName = s.ProcessName,
            SentBytes = s.SentBytes,
            ReceivedBytes = s.ReceivedBytes,
            RequestBodyCapture = s.RequestBodyCapture.ToString(),
            ResponseBodyCapture = s.ResponseBodyCapture.ToString(),
            RequestBodyOriginalSize = s.RequestBodyOriginalSize,
            ResponseBodyOriginalSize = s.ResponseBodyOriginalSize,
            IsWebSocket = s.IsWebSocket,
            IsGrpc = s.IsGrpc,
            IsTranscoded = s.IsTranscoded,
            IsTunnel = s.IsTunnel,
            OpaqueReason = s.OpaqueReason.ToString(),
            IsMultipart = s.IsMultipart,
            IsServerSentEvents = s.IsServerSentEvents,
            ClientMethod = s.ClientMethod,
            ClientPathAndQuery = s.ClientPathAndQuery,
            ClientContentType = s.ClientContentType,
            UpstreamMethod = s.UpstreamMethod,
            UpstreamPath = s.UpstreamPath,
            UpstreamContentType = s.UpstreamContentType,
            RequestBodyText = s.RequestBodyText,
            ResponseBodyText = s.ResponseBodyText,
            RequestBodyBase64 = s.RequestBodyBytes is { Length: > 0 } rb ? Convert.ToBase64String(rb) : null,
            ResponseBodyBase64 = s.ResponseBodyBytes is { Length: > 0 } sb ? Convert.ToBase64String(sb) : null,
            UpstreamRequestBodyBase64 = s.UpstreamRequestBodyBytes is { Length: > 0 } ur
                ? Convert.ToBase64String(ur)
                : null,
            UpstreamResponseBodyBase64 = s.UpstreamResponseBodyBytes is { Length: > 0 } us
                ? Convert.ToBase64String(us)
                : null,
            ProtobufDecodedText = s.ProtobufDecodedText,
            WebSocketFrames = s.WebSocketFrames,
            SseEvents = s.SseEvents,
            GrpcFrames = s.GrpcFrames,
            MultipartParts = s.MultipartParts,
        };

        return JsonSerializer.SerializeToNode(ext, InspectorJson)!.AsObject();
    }

    private static void ApplyInspectorExtension(JsonElement entry, SessionSnapshot snap)
    {
        if (!entry.TryGetProperty("_inspector", out var ext) || ext.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (ext.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var savedId) && savedId > 0)
        {
            snap.Id = savedId;
        }

        if (ext.TryGetProperty("protocol", out var proto))
        {
            snap.Protocol = proto.GetString() ?? snap.Protocol;
        }

        if (ext.TryGetProperty("host", out var hostEl))
        {
            snap.Host = hostEl.GetString() ?? snap.Host;
        }

        if (ext.TryGetProperty("bodySize", out var bs) && bs.TryGetInt64(out var bodySize))
        {
            snap.BodySize = bodySize;
        }

        if (ext.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out var processId))
        {
            snap.ProcessId = processId;
        }

        if (ext.TryGetProperty("processName", out var pn))
        {
            snap.ProcessName = pn.GetString();
        }

        if (ext.TryGetProperty("sentBytes", out var sent) && sent.TryGetInt64(out var sentBytes))
        {
            snap.SentBytes = sentBytes;
        }

        if (ext.TryGetProperty("receivedBytes", out var recv) && recv.TryGetInt64(out var receivedBytes))
        {
            snap.ReceivedBytes = receivedBytes;
        }

        if (ext.TryGetProperty("requestBodyCapture", out var rbc)
            && Enum.TryParse<BodyCaptureState>(rbc.GetString(), ignoreCase: true, out var reqCap))
        {
            snap.RequestBodyCapture = reqCap;
        }

        if (ext.TryGetProperty("responseBodyCapture", out var sbc)
            && Enum.TryParse<BodyCaptureState>(sbc.GetString(), ignoreCase: true, out var respCap))
        {
            snap.ResponseBodyCapture = respCap;
        }

        if (ext.TryGetProperty("requestBodyOriginalSize", out var rbos) && rbos.TryGetInt64(out var reqOrig))
        {
            snap.RequestBodyOriginalSize = reqOrig;
        }

        if (ext.TryGetProperty("responseBodyOriginalSize", out var sbos) && sbos.TryGetInt64(out var respOrig))
        {
            snap.ResponseBodyOriginalSize = respOrig;
        }

        snap.IsWebSocket = ReadBool(ext, "isWebSocket") ?? snap.IsWebSocket;
        snap.IsGrpc = ReadBool(ext, "isGrpc") ?? snap.IsGrpc;
        snap.IsTranscoded = ReadBool(ext, "isTranscoded") ?? snap.IsTranscoded;
        snap.IsTunnel = ReadBool(ext, "isTunnel") ?? snap.IsTunnel;
        snap.IsMultipart = ReadBool(ext, "isMultipart") ?? snap.IsMultipart;
        snap.IsServerSentEvents = ReadBool(ext, "isServerSentEvents") ?? snap.IsServerSentEvents;

        if (ext.TryGetProperty("opaqueReason", out var or)
            && Enum.TryParse<OpaqueTunnelReason>(or.GetString(), ignoreCase: true, out var opaque))
        {
            snap.OpaqueReason = opaque;
        }

        snap.ClientMethod = ReadString(ext, "clientMethod") ?? snap.ClientMethod;
        snap.ClientPathAndQuery = ReadString(ext, "clientPathAndQuery") ?? snap.ClientPathAndQuery;
        snap.ClientContentType = ReadString(ext, "clientContentType") ?? snap.ClientContentType;
        snap.UpstreamMethod = ReadString(ext, "upstreamMethod") ?? snap.UpstreamMethod;
        snap.UpstreamPath = ReadString(ext, "upstreamPath") ?? snap.UpstreamPath;
        snap.UpstreamContentType = ReadString(ext, "upstreamContentType") ?? snap.UpstreamContentType;
        snap.ProtobufDecodedText = ReadString(ext, "protobufDecodedText") ?? snap.ProtobufDecodedText;

        // Prefer _inspector body payloads when present (lossless for Inspect).
        if (TryDecodeBase64(ext, "requestBodyBase64", out var reqBytes))
        {
            snap.RequestBodyBytes = reqBytes;
        }

        if (TryDecodeBase64(ext, "responseBodyBase64", out var respBytes))
        {
            snap.ResponseBodyBytes = respBytes;
        }

        if (ReadString(ext, "requestBodyText") is { } reqText)
        {
            snap.RequestBodyText = reqText;
        }

        if (ReadString(ext, "responseBodyText") is { } respText)
        {
            snap.ResponseBodyText = respText;
        }

        if (TryDecodeBase64(ext, "upstreamRequestBodyBase64", out var upReq))
        {
            snap.UpstreamRequestBodyBytes = upReq;
        }

        if (TryDecodeBase64(ext, "upstreamResponseBodyBase64", out var upResp))
        {
            snap.UpstreamResponseBodyBytes = upResp;
        }

        snap.WebSocketFrames = DeserializeList<WebSocketFrameSnapshot>(ext, "webSocketFrames") ?? snap.WebSocketFrames;
        snap.SseEvents = DeserializeList<SseEventSnapshot>(ext, "sseEvents") ?? snap.SseEvents;
        snap.GrpcFrames = DeserializeList<GrpcFrameSnapshot>(ext, "grpcFrames") ?? snap.GrpcFrames;
        snap.MultipartParts = DeserializeList<MultipartPartSnapshot>(ext, "multipartParts") ?? snap.MultipartParts;
    }

    private static bool? ReadBool(JsonElement ext, string name) =>
        ext.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : null;

    private static string? ReadString(JsonElement ext, string name) =>
        ext.TryGetProperty(name, out var el) ? el.GetString() : null;

    private static bool TryDecodeBase64(JsonElement ext, string name, out byte[] bytes)
    {
        bytes = [];
        if (!ext.TryGetProperty(name, out var el))
        {
            return false;
        }

        var s = el.GetString();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<T>? DeserializeList<T>(JsonElement ext, string name)
    {
        if (!ext.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(el.GetRawText(), InspectorJson);
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
        out string? contentType,
        out byte[]? reqBytes)
    {
        method = "GET";
        url = "";
        host = null;
        reqHeaders = "";
        reqBody = null;
        contentType = null;
        reqBytes = null;

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
        out string? respMime,
        out byte[]? respBytes)
    {
        status = null;
        respHeaders = null;
        respBody = null;
        respMime = null;
        respBytes = null;

        if (!entry.TryGetProperty("response", out var resp))
        {
            return;
        }

        if (resp.TryGetProperty("status", out var st) && st.TryGetInt32(out var code))
        {
            status = code;
        }

        respHeaders = FormatHarHeaders(resp);
        if (!resp.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.TryGetProperty("mimeType", out var mime))
        {
            respMime = mime.GetString();
        }

        if (!content.TryGetProperty("text", out var ct))
        {
            return;
        }

        var text = ct.GetString();
        var encoding = content.TryGetProperty("encoding", out var enc) ? enc.GetString() : null;
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(text))
        {
            try
            {
                respBytes = Convert.FromBase64String(text);
                return;
            }
            catch
            {
                // Fall through to text.
            }
        }

        respBody = text;
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

    private sealed class InspectorExtension
    {
        public long Id { get; set; }
        public string? Protocol { get; set; }
        public string? Host { get; set; }
        public long? BodySize { get; set; }
        public int ProcessId { get; set; }
        public string? ProcessName { get; set; }
        public long SentBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public string? RequestBodyCapture { get; set; }
        public string? ResponseBodyCapture { get; set; }
        public long? RequestBodyOriginalSize { get; set; }
        public long? ResponseBodyOriginalSize { get; set; }
        public bool IsWebSocket { get; set; }
        public bool IsGrpc { get; set; }
        public bool IsTranscoded { get; set; }
        public bool IsTunnel { get; set; }
        public string? OpaqueReason { get; set; }
        public bool IsMultipart { get; set; }
        public bool IsServerSentEvents { get; set; }
        public string? ClientMethod { get; set; }
        public string? ClientPathAndQuery { get; set; }
        public string? ClientContentType { get; set; }
        public string? UpstreamMethod { get; set; }
        public string? UpstreamPath { get; set; }
        public string? UpstreamContentType { get; set; }
        public string? RequestBodyText { get; set; }
        public string? ResponseBodyText { get; set; }
        public string? RequestBodyBase64 { get; set; }
        public string? ResponseBodyBase64 { get; set; }
        public string? UpstreamRequestBodyBase64 { get; set; }
        public string? UpstreamResponseBodyBase64 { get; set; }
        public string? ProtobufDecodedText { get; set; }
        public IReadOnlyList<WebSocketFrameSnapshot>? WebSocketFrames { get; set; }
        public IReadOnlyList<SseEventSnapshot>? SseEvents { get; set; }
        public IReadOnlyList<GrpcFrameSnapshot>? GrpcFrames { get; set; }
        public IReadOnlyList<MultipartPartSnapshot>? MultipartParts { get; set; }
    }
}

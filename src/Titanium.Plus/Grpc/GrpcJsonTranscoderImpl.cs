using System.Linq;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Grpc;

/// <summary>REST/JSON ↔ gRPC transcoder (unary + server/client streaming frames; optional gzip).</summary>
internal sealed class GrpcJsonTranscoderImpl : IGrpcJsonTranscoder
{
    private readonly HttpRuleRouter _router;
    private readonly Dictionary<string, MessageDescriptor> _messagesByFullName;
    private readonly bool _convertGrpcStatus;
    private readonly bool _ignoreUnknownQueryParameters;
    private readonly bool _preserveProtoFieldNames;
    private readonly bool _alwaysPrintPrimitiveFields;
    private readonly bool _enableGzipCompression;

    public GrpcJsonTranscoderImpl(
        HttpRuleRouter router,
        Dictionary<string, MessageDescriptor> messagesByFullName,
        bool convertGrpcStatus,
        bool ignoreUnknownQueryParameters,
        bool preserveProtoFieldNames,
        bool alwaysPrintPrimitiveFields,
        bool enableGzipCompression = false)
    {
        _router = router;
        _messagesByFullName = messagesByFullName;
        _convertGrpcStatus = convertGrpcStatus;
        _ignoreUnknownQueryParameters = ignoreUnknownQueryParameters;
        _preserveProtoFieldNames = preserveProtoFieldNames;
        _alwaysPrintPrimitiveFields = alwaysPrintPrimitiveFields;
        _enableGzipCompression = enableGzipCompression;
    }

    public static GrpcJsonTranscoderImpl LoadFromDescriptorSet(
        string descriptorSetPath,
        IEnumerable<string> services,
        bool convertGrpcStatus,
        bool ignoreUnknownQueryParameters,
        bool preserveProtoFieldNames,
        bool alwaysPrintPrimitiveFields,
        bool enableGzipCompression = false)
    {
        var bytes = File.ReadAllBytes(descriptorSetPath);
        var set = FileDescriptorSet.Parser.ParseFrom(bytes);
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()).ToList());
        var allow = services
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (allow.Length == 0)
            throw new InvalidOperationException("grpc.transcode.services must list at least one fully-qualified service name.");

        var router = HttpRuleRouter.Build(files, allow);
        var messages = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        foreach (var file in files)
            IndexMessages(file.MessageTypes, messages);

        return new GrpcJsonTranscoderImpl(
            router,
            messages,
            convertGrpcStatus,
            ignoreUnknownQueryParameters,
            preserveProtoFieldNames,
            alwaysPrintPrimitiveFields,
            enableGzipCompression);
    }

    private static void IndexMessages(IList<MessageDescriptor> types, Dictionary<string, MessageDescriptor> map)
    {
        foreach (var type in types)
        {
            map[type.FullName] = type;
            if (type.NestedTypes.Count > 0)
                IndexMessages(type.NestedTypes, map);
        }
    }

    public ValueTask<bool> TryRewriteRequestAsync(object session, CancellationToken cancellationToken = default)
    {
        if (session is not SessionEventArgs args)
            return ValueTask.FromResult(false);

        var request = args.HttpClient.Request;
        var method = request.Method ?? "GET";
        var pathAndQuery = GetPathAndQuery(request);
        if (!_router.TryMatch(method, pathAndQuery, out var route, out var pathVars) || route is null)
            return ValueTask.FromResult(false);

        return RewriteRequestAsync(args, route, pathVars, pathAndQuery, cancellationToken);
    }

    private async ValueTask<bool> RewriteRequestAsync(
        SessionEventArgs args,
        TranscodedRoute route,
        Dictionary<string, string> pathVars,
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        var request = args.HttpClient.Request;
        var message = new DescriptorMessage(route.Method.InputType);
        ApplyPathVariables(message, pathVars);
        ApplyQueryParameters(message, pathAndQuery, pathVars);
        var clientRequestBody = await ApplyRequestBodyAsync(args, route, message, cancellationToken)
            .ConfigureAwait(false);

        var protoBytes = message.ToByteArray();
        if (_enableGzipCompression)
        {
            protoBytes = Gzip(protoBytes);
            request.Headers.RemoveHeader("grpc-encoding");
            request.Headers.AddHeader("grpc-encoding", "gzip");
        }

        var framed = GrpcFrames.Encode(protoBytes, compressed: _enableGzipCompression);
        var upstreamPath = "/" + route.Method.Service.FullName + "/" + route.Method.Name;

        var mark = new GrpcJsonTranscodeSessionMark
        {
            PreviousUserData = args.UserData,
            ClientMethod = request.Method ?? "GET",
            ClientPathAndQuery = pathAndQuery,
            ClientContentType = request.ContentType,
            UpstreamMethod = "POST",
            UpstreamPath = upstreamPath,
            UpstreamContentType = "application/grpc",
            OutputMessageType = route.Method.OutputType.FullName,
            RpcFullName = route.Method.Service.FullName + "/" + route.Method.Name,
            UpstreamRequestBody = framed,
            ClientRequestBody = clientRequestBody
        };
        args.UserData = mark;

        ApplyGrpcUpstreamRequest(args, request, framed, upstreamPath);
        return true;
    }

    private static void ApplyPathVariables(DescriptorMessage message, Dictionary<string, string> pathVars)
    {
        foreach (var (name, value) in pathVars)
            SetScalarField(message, name, value);
    }

    private void ApplyQueryParameters(
        DescriptorMessage message, string pathAndQuery, Dictionary<string, string> pathVars)
    {
        foreach (var (name, value) in QueryString.Parse(pathAndQuery))
        {
            if (pathVars.ContainsKey(name))
                continue;

            var field = FindField(message.Descriptor, name);
            if (field is null)
            {
                if (!_ignoreUnknownQueryParameters)
                    throw new InvalidOperationException($"Unknown query parameter '{name}'.");
                continue;
            }

            SetScalarField(message, field.Name, value);
        }
    }

    private static async ValueTask<byte[]?> ApplyRequestBodyAsync(
        SessionEventArgs args,
        TranscodedRoute route,
        DescriptorMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(route.Body))
            return null;

        var bodyBytes = await args.GetRequestBody(cancellationToken).ConfigureAwait(false);
        var json = bodyBytes.Length == 0 ? "{}" : Encoding.UTF8.GetString(bodyBytes);
        if (route.Body == "*")
        {
            var parsed = ProtoJson.Parse(json, route.Method.InputType, ignoreUnknown: true);
            foreach (var (num, val) in parsed.Values.Where(pair => !message.Values.ContainsKey(pair.Key)))
                message.Values[num] = val;
        }
        else
        {
            var field = FindField(message.Descriptor, route.Body)
                        ?? throw new InvalidOperationException($"Unknown body field '{route.Body}'.");
            if (field.FieldType is FieldType.Message or FieldType.Group)
                message.Values[field.FieldNumber] = ProtoJson.Parse(json, field.MessageType, ignoreUnknown: true);
            else
                SetScalarField(message, field.Name, json.Trim().Trim('"'));
        }

        return bodyBytes;
    }

    private static void ApplyGrpcUpstreamRequest(
        SessionEventArgs args, Request request, byte[] framed, string upstreamPath)
    {
        request.Method = "POST";
        RewritePath(request, upstreamPath);
        request.ContentType = "application/grpc";
        request.Headers.RemoveHeader("Content-Length");
        request.Headers.RemoveHeader("Transfer-Encoding");
        // Do not force `te: trailers` here — on HTTP/1.1 that can leave body reads waiting for
        // trailers the origin may never send. HTTP/2 gRPC origins still accept unary POSTs.
        args.SetRequestBody(framed);
    }

    public ValueTask<bool> TryRewriteResponseAsync(object session, CancellationToken cancellationToken = default)
    {
        if (session is not SessionEventArgs args)
            return ValueTask.FromResult(false);

        if (!GrpcJsonTranscodeSessionMark.TryGet(args.UserData, out var mark) || mark is null)
            return ValueTask.FromResult(false);

        return RewriteResponseAsync(args, mark, cancellationToken);
    }

    private async ValueTask<bool> RewriteResponseAsync(
        SessionEventArgs args,
        GrpcJsonTranscodeSessionMark mark,
        CancellationToken cancellationToken)
    {
        var response = args.HttpClient.Response;
        var body = await args.GetResponseBody(cancellationToken).ConfigureAwait(false);
        mark.UpstreamResponseBody = body;

        var (grpcStatus, grpcMessage) = ReadGrpcStatusAndMessage(response);
        ApplyJsonResponseHeaders(response, grpcStatus);

        if (grpcStatus != 0)
        {
            var errorJson = _convertGrpcStatus
                ? GrpcStatusMapping.FormatStatusJson(grpcStatus, grpcMessage)
                : "{}";
            args.SetResponseBody(Encoding.UTF8.GetBytes(errorJson));
            args.Respond(args.HttpClient.Response);
            return true;
        }

        args.SetResponseBody(Encoding.UTF8.GetBytes(FormatSuccessJson(body, mark)));
        args.Respond(args.HttpClient.Response);
        return true;
    }

    private static (int Status, string? Message) ReadGrpcStatusAndMessage(Response response)
    {
        var grpcStatus = TryReadGrpcStatus(response);
        var grpcMessage = response.Headers.GetFirstHeader("grpc-status") is null
            ? response.TrailingHeaders?.GetFirstHeader("grpc-message")?.Value
            : response.Headers.GetFirstHeader("grpc-message")?.Value;
        grpcMessage ??= response.TrailingHeaders?.GetFirstHeader("grpc-message")?.Value;

        if (grpcStatus is null)
        {
            grpcStatus = response.TrailingHeaders?.GetFirstHeader("grpc-status")?.Value is { } ts
                && int.TryParse(ts, out var code)
                ? code
                : 0;
        }

        return (grpcStatus.Value, grpcMessage);
    }

    private static void ApplyJsonResponseHeaders(Response response, int grpcStatus)
    {
        var httpStatus = GrpcStatusMapping.ToHttpStatus(grpcStatus);
        response.StatusCode = httpStatus;
        response.StatusDescription = HttpStatusDescription(httpStatus);
        response.Headers.RemoveHeader("Content-Type");
        response.ContentType = "application/json";
        response.Headers.RemoveHeader("grpc-status");
        response.Headers.RemoveHeader("grpc-message");
    }

    private static string HttpStatusDescription(int httpStatus) => httpStatus switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        429 => "Too Many Requests",
        499 => "Client Closed Request",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error"
    };

    private string FormatSuccessJson(byte[] body, GrpcJsonTranscodeSessionMark mark)
    {
        if (!TryReadAllFrames(body, out var payloads) ||
            mark.OutputMessageType is null ||
            !_messagesByFullName.TryGetValue(mark.OutputMessageType, out var outputType))
        {
            return "{}";
        }

        var jsonParts = payloads.Select(payload =>
        {
            var message = new DescriptorMessage(outputType);
            message.MergeFrom(payload);
            return ProtoJson.Format(message, _preserveProtoFieldNames, _alwaysPrintPrimitiveFields);
        }).ToList();

        return jsonParts.Count <= 1
            ? (jsonParts.Count == 0 ? "{}" : jsonParts[0])
            : "[" + string.Join(",", jsonParts) + "]";
    }

    private bool TryReadAllFrames(byte[] body, out List<byte[]> payloads)
    {
        payloads = [];
        var offset = 0;
        while (offset < body.Length)
        {
            if (!GrpcFrames.TryRead(body.AsSpan(offset), out var compressed, out var payload))
            {
                return payloads.Count > 0;
            }

            var bytes = payload.ToArray();
            if (compressed)
            {
                bytes = Gunzip(bytes);
            }

            payloads.Add(bytes);
            offset += 5 + payload.Length;
        }

        return payloads.Count > 0;
    }

    private static byte[] Gzip(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(input);
        }

        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] input)
    {
        using var inputMs = new MemoryStream(input);
        using var gzip = new System.IO.Compression.GZipStream(inputMs, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static int? TryReadGrpcStatus(Response response)
    {
        var header = response.Headers.GetFirstHeader("grpc-status")?.Value
                     ?? response.TrailingHeaders?.GetFirstHeader("grpc-status")?.Value;
        return header is not null && int.TryParse(header, out var code) ? code : null;
    }

    private static string GetPathAndQuery(Request request)
    {
        var url = request.RequestUriString;
        if (string.IsNullOrEmpty(url))
            return "/";

        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
            return abs.PathAndQuery;

        return url.StartsWith('/') ? url : "/" + url;
    }

    private static void RewritePath(Request request, string upstreamPath)
    {
        var url = request.RequestUriString;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
        {
            var builder = new UriBuilder(abs) { Path = upstreamPath, Query = string.Empty };
            request.RequestUri = builder.Uri;
            return;
        }

        request.RequestUriString = upstreamPath;
    }

    private static FieldDescriptor? FindField(MessageDescriptor descriptor, string name) =>
        descriptor.FindFieldByName(name) ??
        descriptor.Fields.InDeclarationOrder().FirstOrDefault(f =>
            string.Equals(f.JsonName, name, StringComparison.Ordinal) ||
            string.Equals(f.Name, name, StringComparison.Ordinal));

    private static void SetScalarField(DescriptorMessage message, string fieldName, string value)
    {
        var field = FindField(message.Descriptor, fieldName);
        if (field is null)
            return;

        message.Values[field.FieldNumber] = field.FieldType switch
        {
            FieldType.String => value,
            FieldType.Bool => bool.Parse(value),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 or FieldType.Enum =>
                int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.UInt32 or FieldType.Fixed32 =>
                uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 =>
                long.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.UInt64 or FieldType.Fixed64 =>
                ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Float => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Double => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Bytes => ByteString.FromBase64(value),
            _ => value
        };
    }
}

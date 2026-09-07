using System.Text;
using Google.Api;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Titanium.Plus.Grpc;

internal sealed class TranscodedRoute
{
    public required string HttpMethod { get; init; }
    public required PathTemplate Template { get; init; }
    public required MethodDescriptor Method { get; init; }
    public required string Body { get; init; }
    public string? ResponseBody { get; init; }
}

internal sealed class PathTemplate
{
    private readonly List<Segment> _segments;

    private PathTemplate(List<Segment> segments) => _segments = segments;

    public static PathTemplate Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern[0] != '/')
            throw new ArgumentException($"HTTP path template must start with '/': {pattern}");

        var segments = new List<Segment>();
        var parts = pattern.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith('{') && part.EndsWith('}'))
            {
                var inner = part[1..^1];
                var eq = inner.IndexOf('=');
                var name = eq < 0 ? inner : inner[..eq];
                var matcher = eq < 0 ? "*" : inner[(eq + 1)..];
                segments.Add(new Segment(name, matcher is "**" ? SegmentKind.Multi : SegmentKind.Single));
            }
            else
            {
                segments.Add(new Segment(part, SegmentKind.Literal));
            }
        }

        return new PathTemplate(segments);
    }

    public bool TryMatch(string path, out Dictionary<string, string> vars)
    {
        vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathParts = path.Split('?', 2)[0].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        for (var s = 0; s < _segments.Count; s++)
        {
            var seg = _segments[s];
            if (seg.Kind == SegmentKind.Literal)
            {
                if (i >= pathParts.Length || !string.Equals(pathParts[i], seg.Value, StringComparison.Ordinal))
                    return false;
                i++;
                continue;
            }

            if (seg.Kind == SegmentKind.Single)
            {
                if (i >= pathParts.Length) return false;
                vars[seg.Value] = Uri.UnescapeDataString(pathParts[i]);
                i++;
                continue;
            }

            // Multi (**) must be last
            if (i >= pathParts.Length)
            {
                vars[seg.Value] = string.Empty;
                return s == _segments.Count - 1;
            }

            vars[seg.Value] = string.Join('/', pathParts.Skip(i).Select(Uri.UnescapeDataString));
            i = pathParts.Length;
            return s == _segments.Count - 1;
        }

        return i == pathParts.Length;
    }

    private enum SegmentKind { Literal, Single, Multi }

    private readonly record struct Segment(string Value, SegmentKind Kind);
}

internal sealed class HttpRuleRouter
{
    private readonly List<TranscodedRoute> _routes;

    private HttpRuleRouter(List<TranscodedRoute> routes) => _routes = routes;

    public static HttpRuleRouter Build(
        IReadOnlyList<FileDescriptor> fileDescriptors,
        IReadOnlyCollection<string> serviceAllowList)
    {
        var registry = new ExtensionRegistry { AnnotationsExtensions.Http };
        var routes = new List<TranscodedRoute>();
        var allow = new HashSet<string>(serviceAllowList, StringComparer.Ordinal);

        foreach (var file in fileDescriptors)
        {
            foreach (var service in file.Services)
            {
                if (allow.Count > 0 && !allow.Contains(service.FullName))
                    continue;

                foreach (var method in service.Methods)
                {
                    var options = method.GetOptions();
                    if (options is null) continue;

                    var reparsed = MethodOptions.Parser.WithExtensionRegistry(registry)
                        .ParseFrom(options.ToByteString());
                    if (!reparsed.HasExtension(AnnotationsExtensions.Http))
                        continue;

                    var rule = reparsed.GetExtension(AnnotationsExtensions.Http);
                    AddRule(routes, method, rule);
                    foreach (var extra in rule.AdditionalBindings)
                        AddRule(routes, method, extra);
                }
            }
        }

        return new HttpRuleRouter(routes);
    }

    private static void AddRule(List<TranscodedRoute> routes, MethodDescriptor method, HttpRule rule)
    {
        string httpMethod;
        string pattern;
        switch (rule.PatternCase)
        {
            case HttpRule.PatternOneofCase.Get:
                httpMethod = "GET"; pattern = rule.Get; break;
            case HttpRule.PatternOneofCase.Put:
                httpMethod = "PUT"; pattern = rule.Put; break;
            case HttpRule.PatternOneofCase.Post:
                httpMethod = "POST"; pattern = rule.Post; break;
            case HttpRule.PatternOneofCase.Delete:
                httpMethod = "DELETE"; pattern = rule.Delete; break;
            case HttpRule.PatternOneofCase.Patch:
                httpMethod = "PATCH"; pattern = rule.Patch; break;
            case HttpRule.PatternOneofCase.Custom:
                httpMethod = rule.Custom.Kind; pattern = rule.Custom.Path; break;
            default:
                return;
        }

        if (string.IsNullOrWhiteSpace(pattern))
            return;

        routes.Add(new TranscodedRoute
        {
            HttpMethod = httpMethod,
            Template = PathTemplate.Parse(pattern),
            Method = method,
            Body = rule.Body ?? string.Empty,
            ResponseBody = string.IsNullOrEmpty(rule.ResponseBody) ? null : rule.ResponseBody
        });
    }

    public bool TryMatch(string httpMethod, string pathAndQuery, out TranscodedRoute? route, out Dictionary<string, string> pathVars)
    {
        pathVars = new Dictionary<string, string>(StringComparer.Ordinal);
        route = null;
        var path = pathAndQuery.Split('?', 2)[0];
        foreach (var candidate in _routes)
        {
            if (!string.Equals(candidate.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!candidate.Template.TryMatch(path, out var vars))
                continue;

            route = candidate;
            pathVars = vars;
            return true;
        }

        return false;
    }
}

internal static class QueryString
{
    public static Dictionary<string, string> Parse(string pathAndQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = pathAndQuery.IndexOf('?');
        if (q < 0 || q == pathAndQuery.Length - 1)
            return result;

        foreach (var part in pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                result[Uri.UnescapeDataString(part)] = string.Empty;
                continue;
            }

            result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }

        return result;
    }
}

internal static class GrpcFrames
{
    public static byte[] Encode(ReadOnlySpan<byte> payload, bool compressed = false)
    {
        var result = new byte[5 + payload.Length];
        result[0] = compressed ? (byte)1 : (byte)0;
        result[1] = (byte)((payload.Length >> 24) & 0xff);
        result[2] = (byte)((payload.Length >> 16) & 0xff);
        result[3] = (byte)((payload.Length >> 8) & 0xff);
        result[4] = (byte)(payload.Length & 0xff);
        payload.CopyTo(result.AsSpan(5));
        return result;
    }

    public static bool TryRead(ReadOnlySpan<byte> buffer, out bool compressed, out ReadOnlySpan<byte> payload)
    {
        compressed = false;
        payload = default;
        if (buffer.Length < 5) return false;
        compressed = (buffer[0] & 1) != 0;
        var length = (buffer[1] << 24) | (buffer[2] << 16) | (buffer[3] << 8) | buffer[4];
        if (length < 0 || buffer.Length < 5 + length) return false;
        payload = buffer.Slice(5, length);
        return true;
    }
}

internal static class GrpcStatusMapping
{
    // google.rpc.Code → HTTP
    public static int ToHttpStatus(int grpcStatus) => grpcStatus switch
    {
        0 => 200,   // OK
        1 => 499,   // CANCELLED
        2 => 500,   // UNKNOWN
        3 => 400,   // INVALID_ARGUMENT
        4 => 504,   // DEADLINE_EXCEEDED
        5 => 404,   // NOT_FOUND
        6 => 409,   // ALREADY_EXISTS
        7 => 403,   // PERMISSION_DENIED
        8 => 429,   // RESOURCE_EXHAUSTED
        9 => 400,   // FAILED_PRECONDITION
        10 => 409,  // ABORTED
        11 => 400,  // OUT_OF_RANGE
        12 => 501,  // UNIMPLEMENTED
        13 => 500,  // INTERNAL
        14 => 503,  // UNAVAILABLE
        15 => 500,  // DATA_LOSS
        16 => 401,  // UNAUTHENTICATED
        _ => 500
    };

    public static string FormatStatusJson(int code, string? message)
    {
        var sb = new StringBuilder();
        sb.Append("{\"code\":").Append(code);
        if (!string.IsNullOrEmpty(message))
        {
            sb.Append(",\"message\":");
            sb.Append(JsonEscape(message));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string JsonEscape(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    }
}

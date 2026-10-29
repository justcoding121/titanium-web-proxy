using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// The attribute set <c>proxy_get_property</c> reads, named as Envoy names them. Strings are raw UTF-8, integers are
/// 8-byte little-endian, booleans one byte, and times and durations are nanoseconds. Attributes Titanium has no value for
/// but that SDKs commonly read (metadata, node, certificate details) answer empty rather than NotFound.
/// </summary>
internal static class WasmProperties
{
    private static readonly HashSet<string> EmptyAttributes = new(StringComparer.Ordinal)
    {
        "node", "listener_metadata", "listener_direction", "cluster_metadata", "route_metadata", "upstream_host_metadata",
        "metadata", "filter_state", "connection.subject_local_certificate", "connection.subject_peer_certificate",
        "connection.dns_san_local_certificate", "connection.dns_san_peer_certificate", "connection.uri_san_local_certificate",
        "connection.uri_san_peer_certificate", "connection.sha256_peer_certificate_digest", "connection.termination_details",
        "upstream.local_address", "upstream.transport_failure_reason", "upstream.tls_version", "response.code_details",
        "request.total_size", "response.total_size",
    };

    /// <summary>Normalizes a guest path: NUL-separated segments (the SDK form) or one dotted string.</summary>
    public static string NormalizePath(string raw) => raw.Replace('\0', '.').TrimEnd('.');

    public static byte[]? Get(WasmContext context, string path)
    {
        if (context.Properties.TryGetValue(path, out var stored))
        {
            return stored;
        }

        var value = context.GetContextProperty(path);
        if (value is not null)
        {
            return value;
        }

        var plugin = context.Plugin;
        switch (path)
        {
            case "plugin_name":
            case "xds.filter_chain_name":
                return Utf8(plugin.Name);
            case "plugin_root_id":
                return Utf8(plugin.RootIdName);
            case "plugin_vm_id":
                return Utf8(plugin.VmId ?? string.Empty);
        }

        return EmptyAttributes.Contains(path) ? [] : null;
    }

    public static byte[]? ForHttp(WasmHttpContext context, string path)
    {
        var phase = context.Last;
        var request = context.RequestHeaders;
        var response = context.ResponseHeaders;
        switch (path)
        {
            case "request.path":
                return Utf8(request?.Get(":path") ?? phase?.Path);
            case "request.url_path":
            {
                var full = request?.Get(":path") ?? phase?.Path ?? string.Empty;
                var query = full.IndexOf('?');
                return Utf8(query < 0 ? full : full[..query]);
            }
            case "request.query":
            {
                var full = request?.Get(":path") ?? phase?.Path ?? string.Empty;
                var query = full.IndexOf('?');
                return Utf8(query < 0 ? string.Empty : full[(query + 1)..]);
            }
            case "request.host":
                return Utf8(request?.Get(":authority") ?? phase?.Host);
            case "request.scheme":
                return Utf8(request?.Get(":scheme") ?? (phase?.IsHttps == true ? "https" : "http"));
            case "request.method":
                return Utf8(request?.Get(":method") ?? phase?.Method);
            case "request.headers":
                return request is null ? null : WasmHeaderMap.Serialize(request.Pairs);
            case "request.referer":
                return Utf8(request?.Get("referer") ?? string.Empty);
            case "request.useragent":
                return Utf8(request?.Get("user-agent") ?? string.Empty);
            case "request.id":
                return Utf8(request?.Get("x-request-id") ?? string.Empty);
            case "request.protocol":
                return Utf8("HTTP/1.1");
            case "request.time":
                return Int64(context.StartTime.ToUnixTimeMilliseconds() * 1_000_000);
            case "request.duration":
                return Int64((phase?.Elapsed ?? TimeSpan.Zero).Ticks * 100);
            case "request.size":
                return Int64(long.TryParse(request?.Get("content-length"), out var length) ? length : context.RequestBodyBytes);
            case "response.code":
                return phase is { StatusCode: > 0 } ? Int64(phase.StatusCode) : null;
            case "response.flags":
                return Int64(0);
            case "response.grpc_status":
                return context.ResponseTrailers?.Get("grpc-status") is { } grpc && long.TryParse(grpc, out var status)
                    ? Int64(status)
                    : null;
            case "response.headers":
                return response is null ? null : WasmHeaderMap.Serialize(response.Pairs);
            case "response.trailers":
                return context.ResponseTrailers is null ? null : WasmHeaderMap.Serialize(context.ResponseTrailers.Pairs);
            case "response.size":
                return Int64(context.ResponseBodyBytes);
            case "source.address":
                return phase?.ClientEndPoint is { } client ? Utf8(client.ToString()) : null;
            case "source.port":
                return phase?.ClientEndPoint is { } clientPort ? Int64(clientPort.Port) : null;
            case "connection.mtls":
                return [0];
            case "connection.tls_version":
                return Utf8(phase?.IsHttps == true ? "TLS" : string.Empty);
            case "cluster_name":
            case "xds.cluster_name":
                return Utf8(phase?.ClusterId ?? string.Empty);
            case "route_name":
            case "xds.route_name":
                return Utf8(phase?.RouteId ?? string.Empty);
        }

        return null;
    }

    public static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Utf8(string? value) => value is null ? [] : Encoding.UTF8.GetBytes(value);
}

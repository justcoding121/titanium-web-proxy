namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>Status codes a proxy-wasm host function returns (<c>WasmResult</c>).</summary>
internal static class WasmResult
{
    public const int Ok = 0;
    public const int NotFound = 1;
    public const int BadArgument = 2;
    public const int SerializationFailure = 3;
    public const int ParseFailure = 4;
    public const int InvalidMemoryAccess = 6;
    public const int Empty = 7;
    public const int CasMismatch = 8;
    public const int InternalFailure = 10;
    public const int Unimplemented = 12;
}

internal enum ProxyBufferType
{
    HttpRequestBody = 0,
    HttpResponseBody = 1,
    DownstreamData = 2,
    UpstreamData = 3,
    HttpCallResponseBody = 4,
    GrpcReceiveBuffer = 5,
    VmConfiguration = 6,
    PluginConfiguration = 7,
    CallData = 8,
}

internal enum ProxyMapType
{
    HttpRequestHeaders = 0,
    HttpRequestTrailers = 1,
    HttpResponseHeaders = 2,
    HttpResponseTrailers = 3,
    GrpcReceiveInitialMetadata = 4,
    GrpcReceiveTrailingMetadata = 5,
    HttpCallResponseHeaders = 6,
    HttpCallResponseTrailers = 7,
}

internal enum ProxyStreamType
{
    HttpRequest = 0,
    HttpResponse = 1,
    Downstream = 2,
    Upstream = 3,
}

internal enum ProxyLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>What a proxy-wasm stream callback returns.</summary>
internal static class ProxyAction
{
    public const int Continue = 0;
    public const int Pause = 1;
}

/// <summary>The proxy-wasm ABI versions this host serves, keyed by the marker export a module carries.</summary>
internal static class ProxyWasmAbi
{
    public const string MarkerPrefix = "proxy_abi_version_";

    public const string V010 = "proxy_abi_version_0_1_0";
    public const string V020 = "proxy_abi_version_0_2_0";
    public const string V021 = "proxy_abi_version_0_2_1";

    public static readonly string[] Supported = [V021, V020, V010];

    /// <summary>
    /// Picks the ABI from the module's exports. Fails when the module carries no marker, or only markers this host does not
    /// serve, naming them so the operator can rebuild against a supported SDK.
    /// </summary>
    public static bool TryDetect(IEnumerable<string> exports, out string? version, out string? error)
    {
        var markers = exports.Where(e => e.StartsWith(MarkerPrefix, StringComparison.Ordinal)).ToArray();
        var matched = Supported.FirstOrDefault(candidate => markers.Contains(candidate, StringComparer.Ordinal));
        if (matched is not null)
        {
            version = matched;
            error = null;
            return true;
        }

        version = null;
        error = markers.Length == 0
            ? $"the module exports no {MarkerPrefix}* function, so it is not a proxy-wasm module (supported: {string.Join(", ", Supported)})"
            : $"unsupported proxy-wasm ABI {string.Join(", ", markers)} (supported: {string.Join(", ", Supported)})";
        return false;
    }
}

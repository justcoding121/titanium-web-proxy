using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Plus.Audit;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
namespace Titanium.Plus.ControlPlane;

/// <summary>Snapshot PUT validation strictness for the control plane.</summary>
public enum SnapshotValidationMode
{
    Off,
    Warn,
    Enforce,
}

/// <summary>
/// Loopback-default control plane with shared-secret header auth (GET/PUT snapshot, cache purge).
/// A non-loopback host needs <see cref="ControlPlaneTlsOptions"/> unless <c>allowInsecureNonLoopback</c> is set.
/// Every login failure and mutation lands in <see cref="ControlPlaneSecurity.Audit"/>; repeated failures from one
/// remote are answered 429 (<see cref="ControlPlaneSecurity.Throttle"/>).
/// </summary>
public sealed class ControlPlaneServer : IDisposable
{
    public const string SharedSecretHeader = "X-Titanium-Control-Secret";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new LoadBalanceAlgorithmConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true),
        },
    };

    private readonly IClusterManager? _clusters;
    private readonly IList<RouteConfig>? _routes;
    private readonly Action? _refresh;
    private readonly IHttpResponseCache? _responseCache;
    private readonly string _host;
    private readonly int _port;
    private readonly string _sharedSecret;
    private readonly ControlPlaneTlsOptions? _tls;
    private readonly bool _allowInsecureNonLoopback;
    private readonly IConfigEditor? _configEditor;
    private readonly SnapshotValidationMode _snapshotValidation;
    private readonly ILogger? _logger;
    private ControlHttpServer? _server;

    public ControlPlaneServer(IClusterManager? clusters, string host, int port, string sharedSecret)
        : this(clusters, host, port, sharedSecret, routes: null, refresh: null, responseCache: null)
    {
    }

    public ControlPlaneServer( // NOSONAR S107 -- Control plane wiring keeps dependencies explicit for host composition.
        IClusterManager? clusters,
        string host,
        int port,
        string sharedSecret,
        IList<RouteConfig>? routes,
        Action? refresh = null,
        IHttpResponseCache? responseCache = null,
        ControlPlaneSecurity? security = null,
        ControlPlaneTlsOptions? tls = null,
        bool allowInsecureNonLoopback = false,
        IConfigEditor? configEditor = null,
        SnapshotValidationMode snapshotValidation = SnapshotValidationMode.Warn,
        ILogger? logger = null)
    {
        _clusters = clusters;
        _host = host;
        _port = port;
        _sharedSecret = sharedSecret;
        _routes = routes;
        _refresh = refresh;
        _responseCache = responseCache;
        _tls = tls;
        _allowInsecureNonLoopback = allowInsecureNonLoopback;
        _configEditor = configEditor;
        _snapshotValidation = snapshotValidation;
        _logger = logger;
        Security = security ?? new ControlPlaneSecurity();
    }

    public string Host => _host;
    public int Port => _server?.Port ?? _port;
    public string SharedSecret => _sharedSecret;

    /// <summary>Audit trail and failed-login throttle, shared with the dashboard.</summary>
    public ControlPlaneSecurity Security { get; }

    /// <summary>True when the listener serves HTTPS.</summary>
    public bool IsTls => _tls is not null;

    // Loopback by default; plain HTTP is allowed off loopback only with controlPlane.allowInsecureNonLoopback.
#pragma warning disable S5332
    public string Prefix => $"{(IsTls ? "https" : "http")}://{FormatHost(_host)}:{Port}/";
#pragma warning restore S5332

    /// <summary>The loaded TLS certificate (null for plain HTTP); the dashboard serves the same one.</summary>
    internal X509Certificate2? Certificate { get; private set; }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var trimmed = host.Trim().Trim('[', ']');
        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address));
    }

    public static void ValidateSecret(string host, string sharedSecret, bool allowInsecureDevSecret = false)
    {
        var isLoopback = IsLoopbackHost(host);
        if ((string.IsNullOrWhiteSpace(sharedSecret) ||
             sharedSecret.Equals("changeme", StringComparison.OrdinalIgnoreCase)) &&
            !(isLoopback && allowInsecureDevSecret))
        {
            throw new InvalidOperationException(
                "Plus control plane requires a non-default shared secret " +
                "(set controlPlane.sharedSecret). For loopback-only dev, set TITANIUM_PLUS_ALLOW_DEV_SECRET=1.");
        }
    }

    /// <summary>
    /// A control plane reachable from the network must be encrypted: it carries the shared secret and can rewrite
    /// routing. Loopback is exempt, and <paramref name="allowInsecureNonLoopback"/> is the explicit opt-out.
    /// </summary>
    public static void ValidateTransport(string host, ControlPlaneTlsOptions? tls, bool allowInsecureNonLoopback = false)
    {
        if (tls is null && !allowInsecureNonLoopback && !IsLoopbackHost(host))
        {
            throw new InvalidOperationException(
                $"Plus control plane host '{host}' is not loopback. Set controlPlane.tls (certificatePath and keyPath), " +
                "or controlPlane.allowInsecureNonLoopback: true when the network already protects the port.");
        }
    }

    public void Start()
    {
        ValidateTransport(_host, _tls, _allowInsecureNonLoopback);
        var certificate = _tls?.LoadCertificate();
        var server = new ControlHttpServer(ControlHttpServer.ResolveBindAddress(_host), _port, certificate, HandleAsync);
        try
        {
            server.Start();
        }
        catch
        {
            server.Dispose();
            certificate?.Dispose();
            throw;
        }

        _server = server;
        Certificate = certificate;
    }

    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
        Certificate?.Dispose();
        Certificate = null;
        if (Security.OwnsAudit)
        {
            Security.Audit.Dispose();
        }
    }

    private static string FormatHost(string host) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;

    private async Task<ControlHttpResponse> HandleAsync(ControlHttpRequest request, CancellationToken cancellationToken)
    {
        var denied = Security.Authenticate(request, ControlPlaneSecurity.ControlSurface, _sharedSecret, out var actor);
        if (denied is not null)
        {
            return denied;
        }

        var path = request.Path.TrimEnd('/');
        var method = request.Method;
        var remote = request.RemoteAddress?.ToString();
        if (path.Equals("/v1/snapshot", StringComparison.OrdinalIgnoreCase) &&
            method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return HandleGetSnapshot();
        }

        if (path.Equals("/v1/snapshot", StringComparison.OrdinalIgnoreCase) &&
            method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            var (response, result) = await HandlePutSnapshotAsync(request.BodyText(), cancellationToken).ConfigureAwait(false);
            Security.Audit.Record(actor, remote, AuditActions.SnapshotPut, "/v1/snapshot", result);
            return response;
        }

        if (path.Equals("/v1/cache/purge", StringComparison.OrdinalIgnoreCase) &&
            method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = request.QueryValue("prefix");
            var (response, result) = HandleCachePurge(prefix);
            Security.Audit.Record(actor, remote, AuditActions.CachePurge, string.IsNullOrEmpty(prefix) ? "*" : prefix, result);
            return response;
        }

        return ControlHttpResponse.Text(404, "text/plain; charset=utf-8", "not found");
    }

    private static ControlHttpResponse Json(int status, string json) =>
        ControlHttpResponse.Text(status, "application/json", json);

    private static ControlHttpResponse JsonWithHeader(int status, string json, string headerName, string headerValue) =>
        ControlHttpResponse.Text(status, "application/json", json, new KeyValuePair<string, string>(headerName, headerValue));

    private ControlHttpResponse HandleGetSnapshot()
    {
        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        var json = JsonSerializer.Serialize(new
        {
            clusters = snap.Clusters.ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    kv.Value.Id,
                    algorithm = kv.Value.Algorithm,
                    affinityCookie = kv.Value.AffinityCookie,
                    affinityHeader = kv.Value.AffinityHeader,
                    destinations = kv.Value.Destinations.Select(d => new
                    {
                        d.Id,
                        d.Address,
                        d.Port,
                        state = snap.DestinationStates.GetValueOrDefault(d.Id),
                    }),
                }),
            routes = _routes,
            destinationStates = snap.DestinationStates,
        }, JsonOptions);
        return Json(200, json);
    }

    private async Task<(ControlHttpResponse Response, string Result)> HandlePutSnapshotAsync(
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryParseSnapshotBody(body, out var clusters, out var routes, out var error))
            {
                return (Json(400, JsonSerializer.Serialize(new { error }, JsonOptions)), AuditResults.Rejected);
            }

            if (TryRejectSnapshotValidation(routes, clusters, out var validationWarnings, out var rejected))
            {
                return rejected;
            }

            var applyFailure = await ApplySnapshotAsync(clusters, routes, cancellationToken).ConfigureAwait(false);
            if (applyFailure is not null)
            {
                return applyFailure.Value;
            }

            _refresh?.Invoke();
            return SnapshotApplied(clusters, routes, validationWarnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Json(400, JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions)), AuditResults.Error);
        }
    }

    private bool TryRejectSnapshotValidation(
        List<RouteConfig>? routes,
        List<ClusterConfig>? clusters,
        out IReadOnlyList<string>? validationWarnings,
        out (ControlHttpResponse Response, string Result) rejected)
    {
        validationWarnings = null;
        rejected = default;
        if (_snapshotValidation == SnapshotValidationMode.Off || _configEditor is null)
        {
            return false;
        }

        var validationErrors = _configEditor.ValidateCandidate(routes, clusters);
        if (validationErrors.Count == 0)
        {
            return false;
        }

        if (_snapshotValidation == SnapshotValidationMode.Enforce)
        {
            rejected = (Json(422, JsonSerializer.Serialize(new { error = "validation failed", errors = validationErrors }, JsonOptions)),
                AuditResults.Rejected);
            return true;
        }

        validationWarnings = validationErrors;
        foreach (var message in validationErrors)
        {
            _logger?.LogWarning("Snapshot PUT validation: {Message}", message);
        }

        return false;
    }

    private async Task<(ControlHttpResponse Response, string Result)?> ApplySnapshotAsync(
        List<ClusterConfig>? clusters,
        List<RouteConfig>? routes,
        CancellationToken cancellationToken)
    {
        if (clusters is not null)
        {
            if (_clusters is null)
            {
                return (Json(503, "{\"error\":\"no cluster manager\"}"), AuditResults.Unavailable);
            }

            await _clusters.ApplyAsync(clusters, cancellationToken).ConfigureAwait(false);
        }

        if (routes is not null)
        {
            if (_routes is null)
            {
                return (Json(503, "{\"error\":\"no routes list\"}"), AuditResults.Unavailable);
            }

            _routes.Clear();
            foreach (var route in routes)
            {
                _routes.Add(route);
            }
        }

        return null;
    }

    private static (ControlHttpResponse Response, string Result) SnapshotApplied(
        List<ClusterConfig>? clusters,
        List<RouteConfig>? routes,
        IReadOnlyList<string>? validationWarnings)
    {
        var responseBody = JsonSerializer.Serialize(new
        {
            status = "applied",
            clusters = clusters?.Count,
            routes = routes?.Count,
            warnings = validationWarnings,
        }, JsonOptions);
        if (validationWarnings is { Count: > 0 })
        {
            return (JsonWithHeader(200, responseBody, "X-Titanium-Validation", "failed"), AuditResults.Ok);
        }

        return (Json(200, responseBody), AuditResults.Ok);
    }

    private (ControlHttpResponse Response, string Result) HandleCachePurge(string? prefix)
    {
        if (_responseCache is null)
        {
            return (Json(503, "{\"error\":\"no response cache\"}"), AuditResults.Unavailable);
        }

        var removed = _responseCache.Purge(string.IsNullOrEmpty(prefix) ? null : prefix);
        return (Json(200, JsonSerializer.Serialize(new { status = "purged", removed }, JsonOptions)), AuditResults.Ok);
    }
    /// <summary>
    /// Accepts <c>{ "clusters": [...], "routes": [...] }</c>, routes-only, clusters-only,
    /// or a bare cluster array (backward compatible).
    /// </summary>
    internal static bool TryParseSnapshotBody(
        string body,
        out List<ClusterConfig>? clusters,
        out List<RouteConfig>? routes,
        out string? error)
    {
        clusters = null;
        routes = null;
        error = null;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            clusters = JsonSerializer.Deserialize<List<ClusterConfig>>(body, JsonOptions);
            if (clusters is null)
            {
                error = "invalid body";
                return false;
            }

            return true;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "invalid body";
            return false;
        }

        if (root.TryGetProperty("clusters", out var clustersEl) ||
            root.TryGetProperty("Clusters", out clustersEl))
        {
            clusters = JsonSerializer.Deserialize<List<ClusterConfig>>(clustersEl.GetRawText(), JsonOptions);
        }

        if (root.TryGetProperty("routes", out var routesEl) ||
            root.TryGetProperty("Routes", out routesEl))
        {
            routes = JsonSerializer.Deserialize<List<RouteConfig>>(routesEl.GetRawText(), JsonOptions);
        }

        if (clusters is null && routes is null)
        {
            error = "body must include clusters and/or routes";
            return false;
        }

        return true;
    }

}

/// <summary>Maps leasttime / least_time / leastTime to <see cref="LoadBalanceAlgorithm.LeastTime"/>.</summary>
internal sealed class LoadBalanceAlgorithmConverter : JsonConverter<LoadBalanceAlgorithm>
{
    public override LoadBalanceAlgorithm Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
        {
            return (LoadBalanceAlgorithm)n;
        }

        var s = reader.GetString();
        return s?.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal) switch
        {
            "random" => LoadBalanceAlgorithm.Random,
            "leastrequests" => LoadBalanceAlgorithm.LeastRequests,
            "leasttime" => LoadBalanceAlgorithm.LeastTime,
            "roundrobin" or null or "" => LoadBalanceAlgorithm.RoundRobin,
            _ => Enum.TryParse<LoadBalanceAlgorithm>(s, ignoreCase: true, out var parsed)
                ? parsed
                : LoadBalanceAlgorithm.RoundRobin,
        };
    }

    public override void Write(Utf8JsonWriter writer, LoadBalanceAlgorithm value, JsonSerializerOptions options)
        => writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

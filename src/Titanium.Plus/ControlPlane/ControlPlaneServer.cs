using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.ControlPlane;

/// <summary>
/// Loopback-default control plane with shared-secret header auth (GET/PUT snapshot, cache purge).
/// </summary>
public sealed class ControlPlaneServer : IDisposable
{
    public const string SharedSecretHeader = "X-Titanium-Control-Secret";

    private static readonly JsonSerializerOptions JsonOptions = new()
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
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public ControlPlaneServer(IClusterManager? clusters, string host, int port, string sharedSecret)
        : this(clusters, host, port, sharedSecret, routes: null, refresh: null, responseCache: null)
    {
    }

    public ControlPlaneServer(
        IClusterManager? clusters,
        string host,
        int port,
        string sharedSecret,
        IList<RouteConfig>? routes,
        Action? refresh = null,
        IHttpResponseCache? responseCache = null)
    {
        _clusters = clusters;
        _host = host;
        _port = port;
        _sharedSecret = sharedSecret;
        _routes = routes;
        _refresh = refresh;
        _responseCache = responseCache;
    }

    public string Host => _host;
    public int Port => _port;
    public string SharedSecret => _sharedSecret;
    public string Prefix => $"http://{_host}:{_port}/";

    public static void ValidateSecret(string host, string sharedSecret, bool allowInsecureDevSecret = false)
    {
        var isLoopback = host is "127.0.0.1" or "localhost" or "::1";
        if (string.IsNullOrWhiteSpace(sharedSecret) ||
            sharedSecret.Equals("changeme", StringComparison.OrdinalIgnoreCase))
        {
            if (!(isLoopback && allowInsecureDevSecret))
            {
                throw new InvalidOperationException(
                    "Plus control plane requires a non-default shared secret " +
                    "(set controlPlane.sharedSecret). For loopback-only dev, set TITANIUM_PLUS_ALLOW_DEV_SECRET=1.");
            }
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (!Authorize(ctx.Request))
            {
                ctx.Response.StatusCode = 401;
                await WriteAsync(ctx.Response, "unauthorized");
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Equals("/v1/snapshot", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleGetSnapshotAsync(ctx);
                    return;
                }

                if (ctx.Request.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePutSnapshotAsync(ctx);
                    return;
                }
            }

            if (path.Equals("/v1/cache/purge", StringComparison.OrdinalIgnoreCase) &&
                ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCachePurgeAsync(ctx);
                return;
            }

            ctx.Response.StatusCode = 404;
            await WriteAsync(ctx.Response, "not found");
        }
        catch
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task HandleGetSnapshotAsync(HttpListenerContext ctx)
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
        ctx.Response.ContentType = "application/json";
        await WriteAsync(ctx.Response, json);
    }

    private async Task HandlePutSnapshotAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        try
        {
            if (!TryParseSnapshotBody(body, out var clusters, out var routes, out var error))
            {
                ctx.Response.StatusCode = 400;
                await WriteAsync(ctx.Response, JsonSerializer.Serialize(new { error }, JsonOptions));
                return;
            }

            if (clusters is not null)
            {
                if (_clusters is null)
                {
                    ctx.Response.StatusCode = 503;
                    await WriteAsync(ctx.Response, "{\"error\":\"no cluster manager\"}");
                    return;
                }

                await _clusters.ApplyAsync(clusters);
            }

            if (routes is not null)
            {
                if (_routes is null)
                {
                    ctx.Response.StatusCode = 503;
                    await WriteAsync(ctx.Response, "{\"error\":\"no routes list\"}");
                    return;
                }

                _routes.Clear();
                foreach (var route in routes)
                {
                    _routes.Add(route);
                }
            }

            _refresh?.Invoke();
            ctx.Response.StatusCode = 200;
            await WriteAsync(ctx.Response, JsonSerializer.Serialize(new
            {
                status = "applied",
                clusters = clusters?.Count,
                routes = routes?.Count,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await WriteAsync(ctx.Response, JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
        }
    }

    private async Task HandleCachePurgeAsync(HttpListenerContext ctx)
    {
        if (_responseCache is null)
        {
            ctx.Response.StatusCode = 503;
            await WriteAsync(ctx.Response, "{\"error\":\"no response cache\"}");
            return;
        }

        var prefix = ctx.Request.QueryString["prefix"];
        var removed = _responseCache.Purge(string.IsNullOrEmpty(prefix) ? null : prefix);
        ctx.Response.StatusCode = 200;
        await WriteAsync(ctx.Response, JsonSerializer.Serialize(new { status = "purged", removed }, JsonOptions));
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

    private bool Authorize(HttpListenerRequest request)
    {
        var header = request.Headers[SharedSecretHeader];
        return !string.IsNullOrEmpty(header) &&
               string.Equals(header, _sharedSecret, StringComparison.Ordinal);
    }

    private static async Task WriteAsync(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
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

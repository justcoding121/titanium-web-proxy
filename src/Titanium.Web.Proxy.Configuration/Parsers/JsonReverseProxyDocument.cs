using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration.Parsers;

/// <summary>
/// JSON reverse-proxy document subset: listeners, routes, clusters (camelCase or PascalCase).
/// </summary>
public static class JsonReverseProxyDocument
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static TwpConfig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var doc = JsonSerializer.Deserialize<ReverseProxyDocument>(json, Options)
                  ?? throw new InvalidOperationException("Reverse-proxy JSON deserialized to null.");

        var clusters = new List<ClusterConfig>();
        if (doc.Clusters is not null)
        {
            foreach (var c in doc.Clusters)
            {
                clusters.Add(new ClusterConfig
                {
                    Id = c.Id ?? throw new FormatException("Cluster id is required."),
                    Algorithm = ParseAlgorithm(c.LoadBalancingPolicy ?? c.Algorithm),
                    AffinityCookie = c.AffinityCookie,
                    AffinityHeader = c.AffinityHeader,
                    Destinations = (c.Destinations ?? []).Select((d, i) => new DestinationConfig
                    {
                        Id = d.Id ?? $"{c.Id}-d{i}",
                        Address = d.Address ?? throw new FormatException("Destination address is required."),
                        Port = d.Port ?? 80,
                        UseHttps = d.UseHttps ?? false,
                        Weight = d.Weight ?? 1,
                    }).ToList(),
                });
            }
        }

        var routes = new List<RouteConfig>();
        if (doc.Routes is not null)
        {
            var order = 0;
            foreach (var r in doc.Routes)
            {
                routes.Add(new RouteConfig
                {
                    Id = r.Id ?? $"route-{order + 1}",
                    ClusterId = r.ClusterId ?? throw new FormatException("Route clusterId is required."),
                    Order = r.Order ?? order,
                    Match = new RouteMatch
                    {
                        Host = r.Match?.Hosts?.FirstOrDefault(),
                        Path = r.Match?.Path,
                        PathKind = ParsePathKind(r.Match?.PathKind),
                        Method = r.Match?.Methods?.FirstOrDefault(),
                    },
                });
                order++;
            }
        }

        var listeners = new List<ListenerConfig>();
        if (doc.Listeners is not null)
        {
            foreach (var l in doc.Listeners)
            {
                listeners.Add(new ListenerConfig
                {
                    Host = l.Address ?? "0.0.0.0",
                    Port = l.Port ?? 8000,
                    DecryptSsl = l.DecryptSsl ?? false,
                    ForwardHost = l.ForwardHost,
                    ForwardPort = l.ForwardPort,
                    EnableHttp2 = l.EnableHttp2,
                    EnableHttp3 = l.EnableHttp3 ?? false,
                });
            }
        }

        return new TwpConfig
        {
            Listeners = listeners,
            Routes = routes,
            Clusters = clusters,
        };
    }

    public static TwpConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    private static LoadBalanceAlgorithm ParseAlgorithm(string? policy) =>
        policy?.ToLowerInvariant() switch
        {
            "random" => LoadBalanceAlgorithm.Random,
            "leastrequests" or "least_requests" => LoadBalanceAlgorithm.LeastRequests,
            "leasttime" or "least_time" or "leastresponsetime" => LoadBalanceAlgorithm.LeastTime,
            _ => LoadBalanceAlgorithm.RoundRobin,
        };

    private static PathMatchKind ParsePathKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "exact" => PathMatchKind.Exact,
            "template" => PathMatchKind.Template,
            _ => PathMatchKind.Prefix,
        };

    // System.Text.Json populates these DTO setters via reflection; Sonar S1144 is a false positive.
#pragma warning disable S1144
    private sealed class ReverseProxyDocument
    {
        public List<ListenerDto>? Listeners { get; set; }
        public List<RouteDto>? Routes { get; set; }
        public List<ClusterDto>? Clusters { get; set; }
    }

    private sealed class ListenerDto
    {
        public string? Address { get; set; }
        public int? Port { get; set; }
        public bool? DecryptSsl { get; set; }
        public string? ForwardHost { get; set; }
        public int? ForwardPort { get; set; }
        public bool? EnableHttp2 { get; set; }
        public bool? EnableHttp3 { get; set; }
    }

    private sealed class RouteDto
    {
        public string? Id { get; set; }
        public string? ClusterId { get; set; }
        public int? Order { get; set; }
        public MatchDto? Match { get; set; }
    }

    private sealed class MatchDto
    {
        public List<string>? Hosts { get; set; }
        public string? Path { get; set; }
        public string? PathKind { get; set; }
        public List<string>? Methods { get; set; }
    }

    private sealed class ClusterDto
    {
        public string? Id { get; set; }
        public string? LoadBalancingPolicy { get; set; }
        public string? Algorithm { get; set; }
        public string? AffinityCookie { get; set; }
        public string? AffinityHeader { get; set; }
        public List<DestinationDto>? Destinations { get; set; }
    }

    private sealed class DestinationDto
    {
        public string? Id { get; set; }
        public string? Address { get; set; }
        public int? Port { get; set; }
        public bool? UseHttps { get; set; }
        public int? Weight { get; set; }
    }
#pragma warning restore S1144
}

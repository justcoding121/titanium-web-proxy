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
                    HealthCheck = MapHealth(c.HealthCheck),
                    Destinations = (c.Destinations ?? []).Select((d, i) => new DestinationConfig
                    {
                        Id = d.Id ?? $"{c.Id}-d{i}",
                        Address = d.Address ?? throw new FormatException("Destination address is required."),
                        Port = d.Port ?? 80,
                        UseHttps = d.UseHttps ?? false,
                        Weight = d.Weight ?? 1,
                        UseDestinationHost = d.UseDestinationHost ?? false,
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
                        Host = r.Match?.Hosts?.FirstOrDefault() ?? r.Match?.Host,
                        Hosts = r.Match?.Hosts,
                        Path = r.Match?.Path,
                        PathKind = ParsePathKind(r.Match?.PathKind),
                        Method = r.Match?.Methods?.FirstOrDefault() ?? r.Match?.Method,
                        Methods = r.Match?.Methods,
                        Headers = r.Match?.Headers,
                        Query = r.Match?.Query,
                    },
                    Limits = r.Limits is null
                        ? null
                        : new RouteLimits
                        {
                            RequestTimeoutSeconds = r.Limits.RequestTimeoutSeconds,
                            IdleTimeoutSeconds = r.Limits.IdleTimeoutSeconds,
                            MaxBufferedBodyBytes = r.Limits.MaxBufferedBodyBytes,
                            MaxWebSocketFramePayloadBytes = r.Limits.MaxWebSocketFramePayloadBytes,
                        },
                    Transforms = MapTransforms(r.Transforms),
                    Retry = r.Retry is null
                        ? null
                        : new RetryConfig
                        {
                            Attempts = r.Retry.Attempts ?? 0,
                            RetryNonIdempotent = r.Retry.RetryNonIdempotent ?? false,
                            MaxBufferedBodyBytes = r.Retry.MaxBufferedBodyBytes ?? 64 * 1024,
                            StatusCodes = r.Retry.StatusCodes,
                            PerTryTimeoutMilliseconds = r.Retry.PerTryTimeoutMilliseconds ?? 0,
                            BackoffMilliseconds = r.Retry.BackoffMilliseconds ?? 0,
                            MaxBackoffMilliseconds = r.Retry.MaxBackoffMilliseconds ?? 1000,
                            JitterPercent = r.Retry.JitterPercent ?? 20,
                            HonorRetryAfter = r.Retry.HonorRetryAfter ?? false,
                            BudgetPercent = r.Retry.BudgetPercent ?? 0,
                            BudgetMinConcurrent = r.Retry.BudgetMinConcurrent ?? 3,
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
                    Host = l.Address ?? l.Host ?? "0.0.0.0",
                    Port = l.Port ?? 8000,
                    DecryptSsl = l.DecryptSsl ?? false,
                    ForwardHost = l.ForwardHost,
                    ForwardPort = l.ForwardPort,
                    EnableHttp2 = l.EnableHttp2,
                    EnableHttp3 = l.EnableHttp3,
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

    private static LoadBalanceAlgorithm ParseAlgorithm(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return LoadBalanceAlgorithm.RoundRobin;
        }

        return policy.ToLowerInvariant() switch
        {
            "roundrobin" or "round_robin" => LoadBalanceAlgorithm.RoundRobin,
            "random" => LoadBalanceAlgorithm.Random,
            "leastrequests" or "least_requests" => LoadBalanceAlgorithm.LeastRequests,
            "leasttime" or "least_time" or "leastresponsetime" => LoadBalanceAlgorithm.LeastTime,
            "poweroftwochoices" or "power_of_two_choices" => LoadBalanceAlgorithm.PowerOfTwoChoices,
            "firstalphabetical" or "first_alphabetical" => LoadBalanceAlgorithm.FirstAlphabetical,
            "consistenthash" or "iphash" or "ip_hash" => LoadBalanceAlgorithm.ConsistentHash,
            _ => throw new FormatException($"Unknown load-balancing algorithm '{policy}'."),
        };
    }

    private static PathMatchKind ParsePathKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "exact" => PathMatchKind.Exact,
            "template" => PathMatchKind.Template,
            "segmentprefix" or "segment_prefix" => PathMatchKind.SegmentPrefix,
            "regex" => PathMatchKind.Regex,
            _ => PathMatchKind.Prefix,
        };

    private static List<TransformConfig>? MapTransforms(List<TransformDto>? transforms)
    {
        if (transforms is null || transforms.Count == 0)
        {
            return null;
        }

        return transforms
            .Where(t => !string.IsNullOrWhiteSpace(t.Kind))
            .Select(t => new TransformConfig
            {
                Kind = t.Kind!,
                Parameters = t.Parameters,
            })
            .ToList();
    }

    // System.Text.Json populates these DTO setters via reflection; Sonar S1144 is a false positive.
#pragma warning disable S1144, S3459
    private sealed class ReverseProxyDocument
    {
        public List<ListenerDto>? Listeners { get; set; }
        public List<RouteDto>? Routes { get; set; }
        public List<ClusterDto>? Clusters { get; set; }
    }

    private sealed class ListenerDto
    {
        public string? Address { get; set; }
        public string? Host { get; set; }
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
        public LimitsDto? Limits { get; set; }
        public List<TransformDto>? Transforms { get; set; }
        public RetryDto? Retry { get; set; }
    }

    private sealed class RetryDto
    {
        public int? Attempts { get; set; }
        public bool? RetryNonIdempotent { get; set; }
        public int? MaxBufferedBodyBytes { get; set; }
        public List<int>? StatusCodes { get; set; }
        public int? PerTryTimeoutMilliseconds { get; set; }
        public int? BackoffMilliseconds { get; set; }
        public int? MaxBackoffMilliseconds { get; set; }
        public int? JitterPercent { get; set; }
        public bool? HonorRetryAfter { get; set; }
        public int? BudgetPercent { get; set; }
        public int? BudgetMinConcurrent { get; set; }
    }

    private sealed class LimitsDto
    {
        public int? RequestTimeoutSeconds { get; set; }
        public int? IdleTimeoutSeconds { get; set; }
        public int? MaxBufferedBodyBytes { get; set; }
        public int? MaxWebSocketFramePayloadBytes { get; set; }
    }

    private sealed class TransformDto
    {
        public string? Kind { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }

    private sealed class MatchDto
    {
        public List<string>? Hosts { get; set; }
        public string? Host { get; set; }
        public string? Path { get; set; }
        public string? PathKind { get; set; }
        public List<string>? Methods { get; set; }
        public string? Method { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public Dictionary<string, string>? Query { get; set; }
    }

    private sealed class ClusterDto
    {
        public string? Id { get; set; }
        public string? LoadBalancingPolicy { get; set; }
        public string? Algorithm { get; set; }
        public string? AffinityCookie { get; set; }
        public string? AffinityHeader { get; set; }
        public HealthDto? HealthCheck { get; set; }
        public List<DestinationDto>? Destinations { get; set; }
    }

    private static HealthCheckConfig? MapHealth(HealthDto? health)
    {
        if (health?.Passive is null)
        {
            return null;
        }

        var passive = health.Passive;
        return new HealthCheckConfig
        {
            Passive = new PassiveHealthConfig
            {
                Enabled = passive.Enabled ?? true,
                ConsecutiveFailures = passive.ConsecutiveFailures ?? 3,
                ReactivateAfterSeconds = passive.ReactivateAfterSeconds ?? 30,
                MaxEjectionPercent = passive.MaxEjectionPercent ?? 100,
                FailureStatusCodes = passive.FailureStatusCodes,
            },
        };
    }

    private sealed class HealthDto
    {
        public PassiveDto? Passive { get; set; }
    }

    private sealed class PassiveDto
    {
        public bool? Enabled { get; set; }
        public int? ConsecutiveFailures { get; set; }
        public int? ReactivateAfterSeconds { get; set; }
        public int? MaxEjectionPercent { get; set; }
        public List<int>? FailureStatusCodes { get; set; }
    }

    private sealed class DestinationDto
    {
        public string? Id { get; set; }
        public string? Address { get; set; }
        public int? Port { get; set; }
        public bool? UseHttps { get; set; }
        public int? Weight { get; set; }
        public bool? UseDestinationHost { get; set; }
    }
#pragma warning restore S1144, S3459
}

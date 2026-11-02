using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Config;

/// <summary>Persisted dashboard edits applied on top of the base file.</summary>
public sealed class ConfigOverlayDocument
{
    public int Version { get; set; } = 1;

    public string? BaseDigest { get; set; }

    public OverlayRouteSection Routes { get; set; } = new();

    public OverlayClusterSection Clusters { get; set; } = new();

    public List<OverlayHistoryEntry> History { get; set; } = [];
}

public sealed class OverlayRouteSection
{
    public List<RouteConfig> Upsert { get; set; } = [];

    public List<string> Remove { get; set; } = [];
}

public sealed class OverlayClusterSection
{
    public List<ClusterConfig> Upsert { get; set; } = [];

    public List<string> Remove { get; set; } = [];
}

public sealed class OverlayHistoryEntry
{
    public DateTimeOffset At { get; set; }

    public string Actor { get; set; } = "";

    public string Summary { get; set; } = "";
}

public readonly record struct ConfigOverlayMergeResult(TwpConfig Config, IReadOnlyList<string> Orphans);

/// <summary>Merges a base config with a persisted overlay.</summary>
public static class ConfigOverlay
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const int MaxHistoryEntries = 100;

    public static string ComputeDigest(ReadOnlySpan<byte> baseFileBytes)
    {
        var hash = SHA256.HashData(baseFileBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static ConfigOverlayMergeResult Merge(TwpConfig baseConfig, ConfigOverlayDocument overlay)
    {
        var orphans = new List<string>();
        var routes = baseConfig.Routes.ToDictionary(static r => r.Id, StringComparer.Ordinal);
        var clusters = baseConfig.Clusters.ToDictionary(static c => c.Id, StringComparer.Ordinal);

        orphans.AddRange(overlay.Clusters.Remove
            .Distinct(StringComparer.Ordinal)
            .Where(id => !clusters.Remove(id))
            .Select(id => $"cluster remove '{id}'"));

        foreach (var cluster in overlay.Clusters.Upsert)
        {
            clusters[cluster.Id] = cluster;
        }

        orphans.AddRange(overlay.Routes.Remove
            .Distinct(StringComparer.Ordinal)
            .Where(id => !routes.Remove(id))
            .Select(id => $"route remove '{id}'"));

        foreach (var route in overlay.Routes.Upsert)
        {
            routes[route.Id] = route;
        }

        var merged = CloneBase(baseConfig);
        merged.Routes = routes.Values.ToList();
        merged.Clusters = clusters.Values.ToList();
        return new ConfigOverlayMergeResult(merged, orphans);
    }

    public static ConfigOverlayDocument ApplyEdit(ConfigOverlayDocument overlay, ConfigEditKind kind, RouteConfig? route, ClusterConfig? cluster, string? id, string actor)
    {
        switch (kind)
        {
            case ConfigEditKind.UpsertRoute when route is not null:
                overlay.Routes.Upsert.RemoveAll(r => r.Id == route.Id);
                overlay.Routes.Upsert.Add(route);
                overlay.Routes.Remove.RemoveAll(r => r == route.Id);
                AppendHistory(overlay, actor, $"upsert route {route.Id}");
                break;
            case ConfigEditKind.RemoveRoute when !string.IsNullOrWhiteSpace(id):
                overlay.Routes.Remove.RemoveAll(r => r == id);
                overlay.Routes.Upsert.RemoveAll(r => r.Id == id);
                if (!overlay.Routes.Remove.Contains(id, StringComparer.Ordinal))
                {
                    overlay.Routes.Remove.Add(id);
                }

                AppendHistory(overlay, actor, $"remove route {id}");
                break;
            case ConfigEditKind.UpsertCluster when cluster is not null:
                overlay.Clusters.Upsert.RemoveAll(c => c.Id == cluster.Id);
                overlay.Clusters.Upsert.Add(cluster);
                overlay.Clusters.Remove.RemoveAll(c => c == cluster.Id);
                AppendHistory(overlay, actor, $"upsert cluster {cluster.Id}");
                break;
            case ConfigEditKind.RemoveCluster when !string.IsNullOrWhiteSpace(id):
                overlay.Clusters.Remove.RemoveAll(c => c == id);
                overlay.Clusters.Upsert.RemoveAll(c => c.Id == id);
                if (!overlay.Clusters.Remove.Contains(id, StringComparer.Ordinal))
                {
                    overlay.Clusters.Remove.Add(id);
                }

                AppendHistory(overlay, actor, $"remove cluster {id}");
                break;
            default:
                throw new ArgumentException("Invalid overlay edit.");
        }

        return overlay;
    }

    public static string Serialize(ConfigOverlayDocument overlay) =>
        JsonSerializer.Serialize(overlay, JsonOptions);

    public static ConfigOverlayDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<ConfigOverlayDocument>(json, JsonOptions) ?? new ConfigOverlayDocument();

    private static void AppendHistory(ConfigOverlayDocument overlay, string actor, string summary)
    {
        overlay.History.Add(new OverlayHistoryEntry
        {
            At = DateTimeOffset.UtcNow,
            Actor = actor,
            Summary = summary,
        });
        if (overlay.History.Count > MaxHistoryEntries)
        {
            overlay.History.RemoveRange(0, overlay.History.Count - MaxHistoryEntries);
        }
    }

    private static TwpConfig CloneBase(TwpConfig source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Listeners = source.Listeners.ToList(),
        Routes = source.Routes.ToList(),
        Clusters = source.Clusters.ToList(),
        Policies = source.Policies.ToList(),
        Plugins = source.Plugins?.ToList(),
        ScriptPipeline = source.ScriptPipeline?.ToList(),
        StaticFiles = source.StaticFiles,
        Plus = source.Plus,
        Certificates = source.Certificates,
        Logging = source.Logging,
        Server = source.Server,
    };
}

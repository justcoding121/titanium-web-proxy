using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>Validates a loaded <see cref="TwpConfig"/> for obvious structural errors.</summary>
public static class TwpConfigValidator
{
    public static IReadOnlyList<string> Validate(TwpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();
        var clusterIds = ValidateClusters(config.Clusters, errors);
        ValidateRoutes(config.Routes, clusterIds, errors);
        ValidateListeners(config.Listeners, errors);
        return errors;
    }

    private static HashSet<string> ValidateClusters(IEnumerable<ClusterConfig> clusters, List<string> errors)
    {
        var clusterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Id))
            {
                errors.Add("Cluster is missing Id.");
                continue;
            }

            if (!clusterIds.Add(cluster.Id))
            {
                errors.Add($"Duplicate cluster id '{cluster.Id}'.");
            }

            if (cluster.Destinations is null || cluster.Destinations.Count == 0)
            {
                errors.Add($"Cluster '{cluster.Id}' has no destinations.");
            }
        }

        return clusterIds;
    }

    private static void ValidateRoutes(
        IEnumerable<RouteConfig> routes,
        HashSet<string> clusterIds,
        List<string> errors)
    {
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id))
            {
                errors.Add("Route is missing Id.");
            }

            if (string.IsNullOrWhiteSpace(route.ClusterId) || !clusterIds.Contains(route.ClusterId))
            {
                errors.Add($"Route '{route.Id}' references unknown cluster '{route.ClusterId}'.");
            }

            if (route.Match is null)
            {
                errors.Add($"Route '{route.Id}' is missing Match.");
            }
        }
    }

    private static void ValidateListeners(IEnumerable<ListenerConfig> listeners, List<string> errors)
    {
        foreach (var listener in listeners)
        {
            if (listener.Port is < 1 or > 65535)
            {
                errors.Add($"Listener port {listener.Port} is out of range.");
            }
        }
    }
}

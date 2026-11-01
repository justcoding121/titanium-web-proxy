using Google.Protobuf.WellKnownTypes;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Xds.Ads;
using Titanium.Xds.Translation;

namespace Titanium.Xds;

public sealed class XdsSnapshotBuildResult
{
    public TwpConfig? Config { get; init; }

    public TranslationError? Error { get; init; }

    public bool IsSuccess => Error is null && Config is not null;
}

public static class XdsSnapshotBuilder
{
    public static XdsSnapshotBuildResult Build(AdsResourceStore store, string stateDirectory, TwpConfig bootstrap)
    {
        var clusters = new Dictionary<string, ClusterConfig>(StringComparer.Ordinal);
        if (store.TryGetAll(XdsTypeUrls.Cluster, out var clusterResources))
        {
            foreach (var (_, any) in clusterResources)
            {
                var cluster = any.Unpack<Envoy.Config.Cluster.V3.Cluster>();
                var translated = ClusterTranslator.Translate(cluster);
                if (!translated.IsSuccess)
                {
                    return new XdsSnapshotBuildResult { Error = translated.Error };
                }

                clusters[cluster.Name] = translated.Value!;
            }
        }

        if (store.TryGetAll(XdsTypeUrls.ClusterLoadAssignment, out var endpointResources))
        {
            foreach (var (_, any) in endpointResources)
            {
                var assignment = any.Unpack<Envoy.Config.Endpoint.V3.ClusterLoadAssignment>();
                var translated = EndpointTranslator.Translate(assignment);
                if (!translated.IsSuccess)
                {
                    return new XdsSnapshotBuildResult { Error = translated.Error };
                }

                if (!clusters.TryGetValue(assignment.ClusterName, out var cluster))
                {
                    return new XdsSnapshotBuildResult
                    {
                        Error = new TranslationError
                        {
                            ResourceName = assignment.ClusterName,
                            FieldPath = "cluster_name",
                            Message = "cluster load assignment references an unknown cluster",
                        },
                    };
                }

                clusters[assignment.ClusterName] = new ClusterConfig
                {
                    Id = cluster.Id,
                    Destinations = translated.Value!.ToList(),
                    Algorithm = cluster.Algorithm,
                    HttpClient = cluster.HttpClient,
                    HealthCheck = cluster.HealthCheck,
                    Retry = cluster.Retry,
                };
            }
        }

        var routes = new List<RouteConfig>();
        if (store.TryGetAll(XdsTypeUrls.RouteConfiguration, out var routeResources))
        {
            foreach (var (_, any) in routeResources)
            {
                var routeConfiguration = any.Unpack<Envoy.Config.Route.V3.RouteConfiguration>();
                var translated = RouteTranslator.Translate(routeConfiguration);
                if (!translated.IsSuccess)
                {
                    return new XdsSnapshotBuildResult { Error = translated.Error };
                }

                routes.AddRange(translated.Value!);
            }
        }

        var listeners = new List<ListenerConfig>();
        if (store.TryGetAll(XdsTypeUrls.Listener, out var listenerResources))
        {
            foreach (var (_, any) in listenerResources)
            {
                var listener = any.Unpack<Envoy.Config.Listener.V3.Listener>();
                var translated = ListenerTranslator.Translate(listener);
                if (!translated.IsSuccess)
                {
                    return new XdsSnapshotBuildResult { Error = translated.Error };
                }

                listeners.Add(translated.Value!);
            }
        }

        if (store.TryGetAll(XdsTypeUrls.Secret, out var secretResources))
        {
            foreach (var (_, any) in secretResources)
            {
                var secret = any.Unpack<Envoy.Extensions.TransportSockets.Tls.V3.Secret>();
                var translated = SecretTranslator.Translate(secret, stateDirectory);
                if (!translated.IsSuccess)
                {
                    return new XdsSnapshotBuildResult { Error = translated.Error };
                }

                _ = translated.Value;
            }
        }

        return new XdsSnapshotBuildResult
        {
            Config = new TwpConfig
            {
                SchemaVersion = bootstrap.SchemaVersion,
                Server = bootstrap.Server,
                Logging = bootstrap.Logging,
                Listeners = listeners,
                Routes = routes,
                Clusters = clusters.Values.ToList(),
            },
        };
    }
}

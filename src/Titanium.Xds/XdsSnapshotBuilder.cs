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

                clusters[assignment.ClusterName] = WithDestinations(cluster, translated.Value!.ToList());
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

        var certificateItems = new List<CertificateFileConfig>();
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

                // A TLS certificate secret becomes an SNI-selected certificate on the decrypting and TLS-terminating
                // listeners. Validation contexts (trusted CAs) are written to the state directory only.
                if (translated.Value!.PfxPath is { } pfxPath)
                {
                    certificateItems.Add(new CertificateFileConfig
                    {
                        Path = pfxPath,
                        Hosts = translated.Value.Hosts.ToList(),
                    });
                }
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
                Certificates = MergeCertificates(bootstrap.Certificates, certificateItems),
            },
        };
    }

    /// <summary>Keeps the file's certificate settings and adds the SDS certificates after the file's own items.</summary>
    private static CertificatesConfig? MergeCertificates(CertificatesConfig? bootstrap, List<CertificateFileConfig> sds)
    {
        if (sds.Count == 0)
        {
            return bootstrap;
        }

        var merged = new CertificatesConfig
        {
            CertificatePath = bootstrap?.CertificatePath,
            PrivateKeyPath = bootstrap?.PrivateKeyPath,
            AcmeEmail = bootstrap?.AcmeEmail,
            AcmeDomain = bootstrap?.AcmeDomain,
            AcmeDirectory = bootstrap?.AcmeDirectory,
        };
        foreach (var item in bootstrap?.Items ?? [])
        {
            merged.Items.Add(item);
        }

        foreach (var item in sds)
        {
            merged.Items.Add(item);
        }

        return merged;
    }

    /// <summary>Copy of <paramref name="cluster" /> with new destinations and every other setting kept (EDS only supplies endpoints).</summary>
    private static ClusterConfig WithDestinations(ClusterConfig cluster, IReadOnlyList<DestinationConfig> destinations) => new()
    {
        Id = cluster.Id,
        Destinations = destinations,
        Algorithm = cluster.Algorithm,
        AffinityCookie = cluster.AffinityCookie,
        AffinityHeader = cluster.AffinityHeader,
        HealthCheck = cluster.HealthCheck,
        Retry = cluster.Retry,
        SlowStartSeconds = cluster.SlowStartSeconds,
        Queue = cluster.Queue,
        HttpClient = cluster.HttpClient,
        SessionAffinity = cluster.SessionAffinity,
        HashKey = cluster.HashKey,
        LoadBalancingPolicy = cluster.LoadBalancingPolicy,
        SendProxyProtocol = cluster.SendProxyProtocol,
        ZoneAware = cluster.ZoneAware,
    };
}

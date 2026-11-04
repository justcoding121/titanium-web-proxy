using Envoy.Config.Cluster.V3;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Xds.Translation;

public static class ClusterTranslator
{
    public static TranslationResult<ClusterConfig> Translate(Cluster cluster)
    {
        if (string.IsNullOrWhiteSpace(cluster.Name))
        {
            return TranslationResult<ClusterConfig>.Fail(new TranslationError
            {
                ResourceName = cluster.Name ?? "(unnamed)",
                FieldPath = "name",
                Message = "cluster name is required",
            });
        }

        switch (cluster.Type)
        {
            case ClusterDiscoveryType.Static:
            case ClusterDiscoveryType.StrictDns:
            case ClusterDiscoveryType.Eds:
                break;
            default:
                return TranslationResult<ClusterConfig>.Fail(new TranslationError
                {
                    ResourceName = cluster.Name,
                    FieldPath = "type",
                    Message = $"unsupported cluster discovery type '{cluster.Type}'",
                });
        }

        if (cluster.LbPolicy is LbPolicy.RingHash)
        {
            return TranslationResult<ClusterConfig>.Fail(new TranslationError
            {
                ResourceName = cluster.Name,
                FieldPath = "lb_policy",
                Message = "RING_HASH is not supported yet",
            });
        }

        ClusterHttpClientConfig? httpClient = null;
        if (cluster.ConnectTimeout is not null)
        {
            httpClient = new ClusterHttpClientConfig
            {
                ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(cluster.ConnectTimeout.Seconds, 0, int.MaxValue)),
            };
        }

        var config = new ClusterConfig
        {
            Id = cluster.Name,
            Destinations = Array.Empty<DestinationConfig>(),
            Algorithm = cluster.LbPolicy switch
            {
                LbPolicy.LeastRequest => LoadBalanceAlgorithm.LeastRequests,
                LbPolicy.Random => LoadBalanceAlgorithm.Random,
                _ => LoadBalanceAlgorithm.RoundRobin,
            },
            HttpClient = httpClient,
        };

        return TranslationResult<ClusterConfig>.Ok(config);
    }
}

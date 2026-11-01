using System.Security.Cryptography;
using System.Text;
using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Gateway.Translation;

internal sealed partial class TranslationPass
{
    private readonly Dictionary<string, BackendTlsPolicy> _servicePolicies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _inlineCaFiles = new(StringComparer.Ordinal);

    private void ApplyBackendTlsPolicies(ClusterRegistry clusters)
    {
        if (!_options.BackendTlsPolicyEnabled)
        {
            return;
        }

        _servicePolicies.Clear();
        _inlineCaFiles.Clear();
        foreach (var policy in _state.BackendTlsPolicies
                     .OrderBy(p => p.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(p => p.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(p => p.Metadata.Name, StringComparer.Ordinal))
        {
            var target = policy.Spec.TargetRefs?.FirstOrDefault(t =>
                (string.IsNullOrEmpty(t.Group) || t.Group == string.Empty) &&
                (string.IsNullOrEmpty(t.Kind) || t.Kind == GatewayApiNames.ServiceKind));
            if (target is null)
            {
                continue;
            }

            var key = $"{policy.Metadata.Namespace}/{target.Name}";
            if (_servicePolicies.ContainsKey(key))
            {
                continue;
            }

            if (policy.Spec.Validation?.SubjectAltNames is { Count: > 0 })
            {
                continue;
            }

            _servicePolicies[key] = policy;
        }

        foreach (var cluster in clusters.Build())
        {
            var serviceKey = ServiceKeyFromClusterId(cluster.Id);
            if (serviceKey is null || !_servicePolicies.TryGetValue(serviceKey, out var policy))
            {
                continue;
            }

            if (TryBuildBackendTlsCluster(cluster, policy, out var updated))
            {
                clusters.Replace(updated);
            }
        }
    }

    private bool TryBuildBackendTlsCluster(ClusterConfig cluster, BackendTlsPolicy policy, out ClusterConfig updated)
    {
        updated = cluster;
        var validation = policy.Spec.Validation;
        if (validation?.Hostname is not { Length: > 0 } hostname)
        {
            return false;
        }

        string? caPath = null;
        if (!string.Equals(validation.WellKnownCACertificates, "System", StringComparison.Ordinal))
        {
            if (!TryResolveCaBundle(policy, out var pem))
            {
                return false;
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pem)))[..12];
            var fileName = $"backend-ca-{hash}.pem";
            caPath = string.Concat(_options.TlsMountPath.TrimEnd('/'), "/", fileName);
            _inlineCaFiles[fileName] = Encoding.UTF8.GetBytes(pem);
        }

        updated = new ClusterConfig
        {
            Id = cluster.Id,
            Algorithm = cluster.Algorithm,
            AffinityCookie = cluster.AffinityCookie,
            AffinityHeader = cluster.AffinityHeader,
            HealthCheck = cluster.HealthCheck,
            Retry = cluster.Retry,
            SlowStartSeconds = cluster.SlowStartSeconds,
            Queue = cluster.Queue,
            SessionAffinity = cluster.SessionAffinity,
            HashKey = cluster.HashKey,
            LoadBalancingPolicy = cluster.LoadBalancingPolicy,
            Destinations = cluster.Destinations
                .Select(d => new DestinationConfig
                {
                    Id = d.Id,
                    Address = d.Address,
                    Port = d.Port,
                    UseHttps = true,
                    Weight = d.Weight,
                    UseDestinationHost = d.UseDestinationHost,
                    Health = d.Health,
                    DnsRefreshSeconds = d.DnsRefreshSeconds,
                    Host = d.Host,
                    MaxConcurrentRequests = d.MaxConcurrentRequests,
                    Priority = d.Priority,
                    Backup = d.Backup,
                    SlowStartSeconds = d.SlowStartSeconds,
                    AffinityKey = d.AffinityKey,
                })
                .ToList(),
            HttpClient = new ClusterHttpClientConfig
            {
                ServerName = hostname,
                VerifyHostname = true,
                CaBundlePath = caPath,
            },
        };
        return true;
    }

    private bool TryResolveCaBundle(BackendTlsPolicy policy, out string pem)
    {
        pem = string.Empty;
        var refs = policy.Spec.Validation?.CaCertificateRefs;
        if (refs is not { Count: > 0 })
        {
            return false;
        }

        var builder = new StringBuilder();
        foreach (var reference in refs)
        {
            var kind = string.IsNullOrEmpty(reference.Kind) ? GatewayApiNames.ConfigMapKind : reference.Kind;
            if (kind != GatewayApiNames.ConfigMapKind)
            {
                return false;
            }

            var map = _state.ConfigMaps.FirstOrDefault(c =>
                c.Namespace == policy.Metadata.Namespace && c.Name == reference.Name);
            if (map is null || !map.Data.TryGetValue("ca.crt", out var ca) || string.IsNullOrWhiteSpace(ca))
            {
                return false;
            }

            builder.AppendLine(ca.Trim());
        }

        pem = builder.ToString();
        return pem.Length > 0;
    }

    private static string? ServiceKeyFromClusterId(string clusterId)
    {
        var slash = clusterId.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return null;
        }

        var colon = clusterId.IndexOf(':', slash);
        if (colon <= slash)
        {
            return null;
        }

        return clusterId[..colon];
    }
}

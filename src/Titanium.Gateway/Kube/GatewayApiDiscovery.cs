using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Titanium.Gateway.Kube;

/// <summary>Which Gateway API version serves each optional route kind on this cluster.</summary>
public sealed record GatewayApiVersions(
    string? TlsRoutes,
    string? TcpRoutes,
    string? UdpRoutes,
    string? GrpcRoutes,
    string? BackendTlsPolicies)
{
    public static GatewayApiVersions Absent { get; } = new(null, null, null, null, null);

    public string? PathFor(string plural) => plural switch
    {
        "tlsroutes" => TlsRoutes,
        "tcproutes" => TcpRoutes,
        "udproutes" => UdpRoutes,
        "grpcroutes" => GrpcRoutes,
        "backendtlspolicies" => BackendTlsPolicies,
        _ => null,
    };
}

/// <summary>Probes <c>/apis/gateway.networking.k8s.io</c> for served resource versions.</summary>
public sealed class GatewayApiDiscovery
{
    private const string GroupPath = "/apis/gateway.networking.k8s.io";
    private readonly IKubeApi _api;
    private readonly ILogger _log;

    public GatewayApiDiscovery(IKubeApi api, ILogger log)
    {
        _api = api;
        _log = log;
    }

    public async Task<GatewayApiVersions> DiscoverAsync(CancellationToken cancellationToken)
    {
        var group = await _api.SendAsync(HttpMethod.Get, GroupPath, null, null, cancellationToken).ConfigureAwait(false);
        if (group.IsNotFound)
        {
            return GatewayApiVersions.Absent;
        }

        if (!group.IsSuccess)
        {
            _log.LogWarning("Gateway API discovery failed: {Status}", (int)group.Status);
            return GatewayApiVersions.Absent;
        }

        using var document = JsonDocument.Parse(group.Body);
        var versions = document.RootElement.GetArray("versions")
            .Select(v => v.GetString("version"))
            .Where(v => v is not null)
            .Cast<string>()
            .ToList();
        if (versions.Count == 0)
        {
            return GatewayApiVersions.Absent;
        }

        var served = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            var list = await _api.SendAsync(
                HttpMethod.Get,
                $"{GroupPath}/{version}",
                null,
                null,
                cancellationToken).ConfigureAwait(false);
            if (!list.IsSuccess)
            {
                continue;
            }

            using var resources = JsonDocument.Parse(list.Body);
            foreach (var resource in resources.RootElement.GetArray("resources"))
            {
                var name = resource.GetString("name");
                if (name is null)
                {
                    continue;
                }

                if (!served.TryGetValue(name, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    served[name] = set;
                }

                set.Add(version);
            }
        }

        string? Pick(string plural) =>
            ServedVersionPicker.Pick(plural, served.TryGetValue(plural, out var set) ? set : []);

        return new GatewayApiVersions(
            Pick("tlsroutes"),
            Pick("tcproutes"),
            Pick("udproutes"),
            Pick("grpcroutes"),
            Pick("backendtlspolicies"));
    }
}

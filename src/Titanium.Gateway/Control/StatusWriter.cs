using System.Text.Json;
using Microsoft.Extensions.Logging;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Control;

/// <summary>
///     Writes the status the translator computed. It stamps <c>lastTransitionTime</c> (kept when a condition did not
///     change), skips resources whose status already matches, and for HTTPRoute touches only the parent entries of
///     this controller, so other controllers' entries survive.
/// </summary>
public sealed class StatusWriter
{
    private const string Group = GatewayApiNames.Group;

    private readonly IKubeApi _api;
    private readonly string _controllerName;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    public StatusWriter(IKubeApi api, string controllerName, ILogger log, TimeProvider? time = null)
    {
        _api = api;
        _controllerName = controllerName;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Applies the patches that differ from what the cluster shows. Returns the number written and the number that failed.</summary>
    public async Task<(int Written, int Failed)> ApplyAsync(IReadOnlyList<StatusPatch> patches, ClusterState current, CancellationToken cancellationToken)
    {
        var written = 0;
        var failed = 0;
        foreach (var patch in patches)
        {
            try
            {
                if (await ApplyOneAsync(patch, current, cancellationToken).ConfigureAwait(false))
                {
                    written++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or KubeApiException or IOException)
            {
                failed++;
                _log.LogWarning(ex, "Could not write status of {Key}: {Message}", patch.Key, ex.Message);
            }
        }

        return (written, failed);
    }

    private async Task<bool> ApplyOneAsync(StatusPatch patch, ClusterState current, CancellationToken cancellationToken)
    {
        if (!TryBuildStatusWrite(patch, current, out var desired, out var path))
        {
            return false;
        }

        var body = JsonSerializer.Serialize(new { status = desired }, GatewayJson.Options);
        var response = await _api.SendAsync(HttpMethod.Patch, path, body, "application/merge-patch+json", cancellationToken).ConfigureAwait(false);
        if (response.IsNotFound)
        {
            return false;
        }

        if (!response.IsSuccess)
        {
            throw new KubeApiException(response.Status, response.Body);
        }

        return true;
    }

    private bool TryBuildStatusWrite(StatusPatch patch, ClusterState current, out object desired, out string path)
    {
        switch (patch.Kind)
        {
            case StatusKind.GatewayClass:
                return TryBuildGatewayClassWrite(patch, current, out desired, out path);

            case StatusKind.Gateway:
                return TryBuildGatewayWrite(patch, current, out desired, out path);

            case StatusKind.Ingress:
                return TryBuildIngressWrite(patch, current, out desired, out path);

            default:
                return TryBuildHttpRouteWrite(patch, current, out desired, out path);
        }
    }

    private bool TryBuildGatewayClassWrite(StatusPatch patch, ClusterState current, out object desired, out string path)
    {
        var existing = current.GatewayClasses.FirstOrDefault(c => c.Metadata.Name == patch.Name);
        if (existing is null)
        {
            desired = null!;
            path = null!;
            return false;
        }

        var conditions = Stamp(patch, existing.Status?.Conditions);
        if (Same(conditions, existing.Status?.Conditions))
        {
            desired = null!;
            path = null!;
            return false;
        }

        desired = new { conditions };
        path = $"/apis/{Group}/v1/gatewayclasses/{Uri.EscapeDataString(patch.Name)}/status";
        return true;
    }

    private bool TryBuildGatewayWrite(StatusPatch patch, ClusterState current, out object desired, out string path)
    {
        var existing = current.Gateways.FirstOrDefault(g => g.Metadata.Namespace == patch.Namespace && g.Metadata.Name == patch.Name);
        if (existing is null)
        {
            desired = null!;
            path = null!;
            return false;
        }

        var conditions = Stamp(patch, existing.Status?.Conditions);
        var listeners = (patch.Listeners ?? []).Select(l => StampListener(l, existing.Status?.Listeners)).ToList();
        var addresses = patch.Addresses ?? [];
        var unchanged = Same(conditions, existing.Status?.Conditions) &&
                        Json(listeners) == Json(existing.Status?.Listeners ?? []) &&
                        Json(addresses) == Json(existing.Status?.Addresses ?? []);
        if (unchanged)
        {
            desired = null!;
            path = null!;
            return false;
        }

        desired = new { addresses, conditions, listeners };
        path = $"/apis/{Group}/v1/namespaces/{Uri.EscapeDataString(patch.Namespace!)}/gateways/{Uri.EscapeDataString(patch.Name)}/status";
        return true;
    }

    private bool TryBuildIngressWrite(StatusPatch patch, ClusterState current, out object desired, out string path)
    {
        var existing = current.Ingresses.FirstOrDefault(i => i.Metadata.Namespace == patch.Namespace && i.Metadata.Name == patch.Name);
        if (existing is null)
        {
            desired = null!;
            path = null!;
            return false;
        }

        var entries = (patch.Addresses ?? [])
            .Select(a => a.Type == "Hostname"
                ? new IngressLoadBalancerEntry { Hostname = a.Value }
                : new IngressLoadBalancerEntry { Ip = a.Value })
            .ToList();
        if (Json(entries) == Json(existing.Status?.LoadBalancer?.Ingress ?? []))
        {
            desired = null!;
            path = null!;
            return false;
        }

        desired = new { loadBalancer = new { ingress = entries } };
        path = $"/apis/{IngressApiNames.Group}/{IngressApiNames.Version}/namespaces/{Uri.EscapeDataString(patch.Namespace!)}/ingresses/{Uri.EscapeDataString(patch.Name)}/status";
        return true;
    }

    private bool TryBuildHttpRouteWrite(StatusPatch patch, ClusterState current, out object desired, out string path)
    {
        var existing = current.HttpRoutes.FirstOrDefault(r => r.Metadata.Namespace == patch.Namespace && r.Metadata.Name == patch.Name);
        if (existing is null)
        {
            desired = null!;
            path = null!;
            return false;
        }

        var previous = existing.Status?.Parents ?? [];
        var others = previous.Where(p => !string.Equals(p.ControllerName, _controllerName, StringComparison.Ordinal)).ToList();
        var ours = previous.Where(p => string.Equals(p.ControllerName, _controllerName, StringComparison.Ordinal)).ToList();
        var parents = (patch.Parents ?? []).Select(p => StampParent(p, ours)).ToList();
        if (Json(parents) == Json(ours))
        {
            desired = null!;
            path = null!;
            return false;
        }

        desired = new { parents = others.Concat(parents).ToList() };
        path = $"/apis/{Group}/v1/namespaces/{Uri.EscapeDataString(patch.Namespace!)}/httproutes/{Uri.EscapeDataString(patch.Name)}/status";
        return true;
    }

    private List<Condition> Stamp(StatusPatch patch, IReadOnlyList<Condition>? previous) =>
        StampConditions(patch.Conditions, previous, patch.Generation);

    private List<Condition> StampConditions(IReadOnlyList<Condition> wanted, IReadOnlyList<Condition>? previous, long generation)
    {
        var now = _time.GetUtcNow();
        var result = new List<Condition>(wanted.Count);
        foreach (var condition in wanted)
        {
            var before = previous?.FirstOrDefault(c => c.Type == condition.Type);
            result.Add(new Condition
            {
                Type = condition.Type,
                Status = condition.Status,
                ObservedGeneration = condition.ObservedGeneration != 0 ? condition.ObservedGeneration : generation,
                Reason = condition.Reason,
                Message = condition.Message,
                LastTransitionTime = before is not null && before.Status == condition.Status && before.LastTransitionTime is not null
                    ? before.LastTransitionTime
                    : now,
            });
        }

        return result;
    }

    private ListenerStatus StampListener(ListenerStatus wanted, IReadOnlyList<ListenerStatus>? previous)
    {
        var before = previous?.FirstOrDefault(l => l.Name == wanted.Name);
        return new ListenerStatus
        {
            Name = wanted.Name,
            SupportedKinds = wanted.SupportedKinds,
            AttachedRoutes = wanted.AttachedRoutes,
            Conditions = StampConditions(wanted.Conditions, before?.Conditions, 0),
        };
    }

    private RouteParentStatus StampParent(RouteParentStatus wanted, IReadOnlyList<RouteParentStatus> previous)
    {
        var before = previous.FirstOrDefault(p => SameParent(p.ParentRef, wanted.ParentRef));
        return new RouteParentStatus
        {
            ParentRef = wanted.ParentRef,
            ControllerName = wanted.ControllerName,
            Conditions = StampConditions(wanted.Conditions, before?.Conditions, 0),
        };
    }

    private static bool SameParent(ParentReference a, ParentReference b) =>
        a.Name == b.Name && (a.Namespace ?? string.Empty) == (b.Namespace ?? string.Empty) &&
        (a.SectionName ?? string.Empty) == (b.SectionName ?? string.Empty) && a.Port == b.Port;

    private static bool Same(IReadOnlyList<Condition> wanted, IReadOnlyList<Condition>? existing) =>
        Json(wanted) == Json(existing ?? []);

    private static string Json(object value) => JsonSerializer.Serialize(value, GatewayJson.Options);
}

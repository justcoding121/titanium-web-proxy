using Titanium.Gateway.Model;

namespace Titanium.Gateway.Translation;

internal sealed partial class TranslationPass
{
    private List<StatusPatch> BuildPatches(bool dataPlaneConfirmed)
    {
        var patches = new List<StatusPatch>();
        patches.AddRange(_classes.Select(gatewayClass => new StatusPatch(
            StatusKind.GatewayClass,
            null,
            gatewayClass.Metadata.Name,
            gatewayClass.Metadata.Generation,
            [
                Conditions.Make(
                    Conditions.Accepted,
                    true,
                    "Accepted",
                    $"Handled by {_options.ControllerName}",
                    gatewayClass.Metadata.Generation),
            ])));

        var addresses = GatewayAddresses();
        foreach (var gateway in _gateways)
        {
            patches.Add(BuildGatewayPatch(gateway, addresses, dataPlaneConfirmed));
        }

        foreach (var route in _routes)
        {
            patches.Add(BuildRoutePatch(route));
        }

        foreach (var route in _l4Routes)
        {
            patches.Add(BuildL4RoutePatch(route));
        }

        foreach (var route in _grpcRoutes)
        {
            patches.Add(BuildGrpcRoutePatch(route));
        }

        foreach (var ingress in _ingresses)
        {
            patches.Add(BuildIngressPatch(ingress, addresses));
        }

        return patches;
    }

    private List<GatewayStatusAddress> GatewayAddresses()
    {
        var result = new List<GatewayStatusAddress>();
        if (_options.DataPlaneServiceName is null)
        {
            return result;
        }

        var service = _state.Services.FirstOrDefault(s =>
            s.Name == _options.DataPlaneServiceName &&
            string.Equals(s.Namespace, _options.DataPlaneServiceNamespace, StringComparison.Ordinal));
        if (service is null)
        {
            return result;
        }

        foreach (var address in service.LoadBalancerAddresses)
        {
            result.Add(new GatewayStatusAddress
            {
                Type = address.IsHostname ? "Hostname" : "IPAddress",
                Value = address.Value,
            });
        }

        // A Service without a load balancer (kind, bare metal) is still reachable at its cluster IP.
        if (result.Count == 0 && !string.IsNullOrEmpty(service.ClusterIp) && service.ClusterIp != "None")
        {
            result.Add(new GatewayStatusAddress { Type = "IPAddress", Value = service.ClusterIp });
        }

        return result;
    }

    private static StatusPatch BuildGatewayPatch(GatewayOutcome outcome, List<GatewayStatusAddress> addresses, bool confirmed)
    {
        var gateway = outcome.Gateway;
        var generation = gateway.Metadata.Generation;
        var programmable = outcome.Listeners.Count(l => l.Programmable);
        var invalid = outcome.Listeners.Count - programmable;
        var live = programmable > 0 && addresses.Count > 0 && confirmed;

        var listenerStatuses = outcome.Listeners
            .Select(l => BuildListenerStatus(l, generation, live, addresses.Count > 0))
            .ToList();

        Condition accepted;
        if (outcome.UnsupportedAddress)
        {
            accepted = Conditions.Make(Conditions.Accepted, false, "UnsupportedAddress", "spec.addresses is not supported; the address comes from the data-plane Service.", generation);
        }
        else if (programmable == 0 && outcome.Listeners.Count > 0)
        {
            accepted = Conditions.Make(Conditions.Accepted, false, "ListenersNotValid", "No listener is valid.", generation);
        }
        else if (invalid > 0)
        {
            accepted = Conditions.Make(Conditions.Accepted, true, "ListenersNotValid", $"{invalid} listener(s) are not valid.", generation);
        }
        else
        {
            accepted = Conditions.Make(Conditions.Accepted, true, "Accepted", "Gateway accepted", generation);
        }

        Condition programmed;
        if (programmable == 0)
        {
            programmed = Conditions.Make(Conditions.Programmed, false, "Invalid", "No listener is valid, so nothing is programmed.", generation);
        }
        else if (addresses.Count == 0)
        {
            programmed = Conditions.Make(Conditions.Programmed, false, "AddressNotAssigned", "The data-plane Service has no address yet.", generation);
        }
        else if (!confirmed)
        {
            programmed = Conditions.Make(Conditions.Programmed, false, "Pending", "Waiting for the data plane to apply the rendered config.", generation);
        }
        else
        {
            programmed = Conditions.Make(Conditions.Programmed, true, "Programmed", "The data plane runs the rendered config.", generation);
        }

        return new StatusPatch(
            StatusKind.Gateway,
            gateway.Metadata.Namespace,
            gateway.Metadata.Name,
            generation,
            [accepted, programmed],
            addresses,
            listenerStatuses);
    }

    private static ListenerStatus BuildListenerStatus(ListenerOutcome listener, long generation, bool live, bool hasAddress)
    {
        var status = new ListenerStatus
        {
            Name = listener.Listener.Name,
            AttachedRoutes = listener.AttachedRoutes,
        };
        if (listener.KindsValid)
        {
            if (listener.IsHttp || listener.IsHttps)
            {
                status.SupportedKinds.Add(new RouteGroupKind { Group = GatewayApiNames.Group, Kind = HttpRouteGroupKind });
                status.SupportedKinds.Add(new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.GrpcRouteKind });
            }
            else if (listener.IsTlsPassthrough)
            {
                status.SupportedKinds.Add(new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.TlsRouteKind });
            }
            else if (listener.IsTcp)
            {
                status.SupportedKinds.Add(new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.TcpRouteKind });
            }
            else if (listener.IsUdp)
            {
                status.SupportedKinds.Add(new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.UdpRouteKind });
            }
        }

        status.Conditions.Add(Conditions.Make(Conditions.Accepted, listener.Accepted, listener.AcceptedReason, listener.AcceptedMessage, generation));
        status.Conditions.Add(Conditions.Make(Conditions.Conflicted, listener.Conflicted, listener.ConflictReason, listener.ConflictMessage, generation));
        status.Conditions.Add(Conditions.Make(Conditions.ResolvedRefs, listener.RefsResolved, listener.RefsReason, listener.RefsMessage, generation));

        if (!listener.Programmable)
        {
            status.Conditions.Add(Conditions.Make(
                Conditions.Programmed,
                false,
                listener.InvalidReason ?? "Invalid",
                "The listener is not valid, so it is not programmed.",
                generation));
        }
        else if (live)
        {
            status.Conditions.Add(Conditions.Make(Conditions.Programmed, true, "Programmed", "Listener programmed", generation));
        }
        else
        {
            status.Conditions.Add(Conditions.Make(
                Conditions.Programmed,
                false,
                hasAddress ? "Pending" : "AddressNotAssigned",
                "Waiting for the data plane.",
                generation));
        }

        return status;
    }

    private void CollectNotes()
    {
        foreach (var route in _routes)
        {
            if (route.RefsReason is not null)
            {
                _notes.Add($"HTTPRoute {route.Key}: {route.RefsMessage}");
            }

            foreach (var message in route.Unsupported)
            {
                _notes.Add($"HTTPRoute {route.Key}: {message}");
            }
        }

        foreach (var item in _ingressEvents)
        {
            _notes.Add($"Ingress {item.Namespace}/{item.Name}: {item.Reason}: {item.Message}");
        }
    }

    private StatusPatch BuildL4RoutePatch(L4RouteOutcome route)
    {
        var kind = route.RouteKind switch
        {
            GatewayApiNames.TlsRouteKind => StatusKind.TlsRoute,
            GatewayApiNames.TcpRouteKind => StatusKind.TcpRoute,
            GatewayApiNames.UdpRouteKind => StatusKind.UdpRoute,
            _ => StatusKind.TlsRoute,
        };
        return BuildGenericRoutePatch(kind, route.Metadata, route.Parents, route.RefsReason, route.RefsMessage, []);
    }

    private StatusPatch BuildGrpcRoutePatch(GrpcRouteOutcome route) =>
        BuildGenericRoutePatch(
            StatusKind.GrpcRoute,
            route.Route.Metadata,
            route.Parents,
            route.RefsReason,
            route.RefsMessage,
            route.Unsupported);

    private StatusPatch BuildRoutePatch(RouteOutcome route) =>
        BuildGenericRoutePatch(
            StatusKind.HttpRoute,
            route.Route.Metadata,
            route.Parents,
            route.RefsReason,
            route.RefsMessage,
            route.Unsupported);

    private StatusPatch BuildGenericRoutePatch(
        StatusKind kind,
        ObjectMeta metadata,
        IReadOnlyList<ParentOutcome> parentOutcomes,
        string? refsReason,
        string? refsMessage,
        List<string> unsupported)
    {
        var generation = metadata.Generation;
        var parents = new List<RouteParentStatus>();
        foreach (var parent in parentOutcomes)
        {
            var status = new RouteParentStatus
            {
                ParentRef = parent.Reference,
                ControllerName = _options.ControllerName,
            };
            status.Conditions.Add(Conditions.Make(Conditions.Accepted, parent.Accepted, parent.Reason, parent.Message, generation));
            status.Conditions.Add(refsReason is null
                ? Conditions.Make(Conditions.ResolvedRefs, true, "ResolvedRefs", "All references resolved", generation)
                : Conditions.Make(Conditions.ResolvedRefs, false, refsReason, refsMessage ?? string.Empty, generation));
            if (unsupported.Count > 0)
            {
                status.Conditions.Add(Conditions.Make(
                    Conditions.PartiallyInvalid,
                    true,
                    "UnsupportedValue",
                    "Answered with 500: " + string.Join("; ", unsupported),
                    generation));
            }

            parents.Add(status);
        }

        return new StatusPatch(
            kind,
            metadata.Namespace,
            metadata.Name,
            generation,
            [],
            Parents: parents);
    }
}

using System.Linq;
using Envoy.Config.Listener.V3;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Titanium.Web.Proxy.Configuration.Models;
using TcpProxy = Envoy.Extensions.Filters.Network.TcpProxy.V3.TcpProxy;
using Hcm = Envoy.Extensions.Filters.Network.HttpConnectionManager.V3.HttpConnectionManager;

namespace Titanium.Xds.Translation;

public static class ListenerTranslator
{
    public static TranslationResult<ListenerConfig> Translate(Listener listener)
    {
        var name = string.IsNullOrWhiteSpace(listener.Name) ? "(unnamed)" : listener.Name;
        if (listener.Address?.SocketAddress is null)
        {
            return Fail(name, "address.socket_address", "listener address is required");
        }

        if (UnsupportedListenerFilter(listener) is { } listenerFilter)
        {
            return Fail(name, "listener_filters", $"unsupported listener filter '{listenerFilter.Name}'");
        }

        if (listener.FilterChains.Count != 1)
        {
            return Fail(name, "filter_chains", "exactly one filter chain is supported");
        }

        var chain = listener.FilterChains[0];
        var socket = listener.Address.SocketAddress;
        var config = new ListenerConfig
        {
            Host = socket.Address,
            Port = (int)socket.PortValue,
        };
        ApplyServerNames(chain, config);
        if (ApplyNetworkFilters(name, chain, config) is { } filterError)
        {
            return TranslationResult<ListenerConfig>.Fail(filterError);
        }

        if (listener.ListenerFilters.Any(f => string.Equals(f.Name, "envoy.filters.listener.proxy_protocol", StringComparison.Ordinal)))
        {
            config.AcceptProxyProtocol = true;
        }

        return TranslationResult<ListenerConfig>.Ok(config);
    }

    private static ListenerFilter? UnsupportedListenerFilter(Listener listener) =>
        listener.ListenerFilters.FirstOrDefault(static filter =>
            !string.Equals(filter.Name, "envoy.filters.listener.tls_inspector", StringComparison.Ordinal) &&
            !string.Equals(filter.Name, "envoy.filters.listener.proxy_protocol", StringComparison.Ordinal));

    private static void ApplyServerNames(FilterChain chain, ListenerConfig config)
    {
        if (chain.FilterChainMatch?.ServerNames is not { Count: > 0 } serverNames)
        {
            return;
        }

        config.Passthrough = true;
        config.DecryptSsl = false;
        config.SniRoutes = serverNames
            .Select(sn => new SniRouteConfig { ServerName = sn, ClusterId = string.Empty })
            .ToList();
    }

    private static TranslationError? ApplyNetworkFilters(string name, FilterChain chain, ListenerConfig config)
    {
        foreach (var filter in chain.Filters.Where(static filter => filter.TypedConfig is not null))
        {
            if (string.Equals(filter.Name, "envoy.filters.network.http_connection_manager", StringComparison.Ordinal))
            {
                if (ApplyHttpConnectionManager(name, filter, chain, config) is { } error)
                {
                    return error;
                }

                continue;
            }

            if (string.Equals(filter.Name, "envoy.filters.network.tcp_proxy", StringComparison.Ordinal))
            {
                ApplyTcpProxy(filter, chain, config);
                continue;
            }

            return new TranslationError
            {
                ResourceName = name,
                FieldPath = "filter_chains[0].filters",
                Message = $"unsupported network filter '{filter.Name}'",
            };
        }

        return null;
    }

    private static TranslationError? ApplyHttpConnectionManager(string name, Filter filter, FilterChain chain, ListenerConfig config)
    {
        var hcm = filter.TypedConfig.Unpack<Hcm>();
        var routeName = hcm.RouteConfigName;
        if (string.IsNullOrWhiteSpace(routeName) && hcm.RouteSpecifierCase == Hcm.RouteSpecifierOneofCase.Rds)
        {
            routeName = hcm.Rds.RouteConfigName;
        }

        if (string.IsNullOrWhiteSpace(routeName))
        {
            return new TranslationError
            {
                ResourceName = name,
                FieldPath = "filter_chains[0].filters[].route_config_name",
                Message = "http_connection_manager must name an RDS route configuration",
            };
        }

        config.DecryptSsl = chain.TransportSocket is not null;
        return null;
    }

    private static void ApplyTcpProxy(Filter filter, FilterChain chain, ListenerConfig config)
    {
        var tcp = filter.TypedConfig.Unpack<TcpProxy>();
        config.Passthrough = true;
        config.DecryptSsl = false;
        config.SniDefaultClusterId = tcp.Cluster;
        if (chain.TransportSocket is not null)
        {
            config.TerminateTls = true;
        }
    }

    private static TranslationResult<ListenerConfig> Fail(string name, string fieldPath, string message) =>
        TranslationResult<ListenerConfig>.Fail(new TranslationError
        {
            ResourceName = name,
            FieldPath = fieldPath,
            Message = message,
        });

    internal static Any PackFilter<T>(T message) where T : IMessage<T> =>
        Any.Pack(message);
}

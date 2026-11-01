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
            return TranslationResult<ListenerConfig>.Fail(new TranslationError
            {
                ResourceName = name,
                FieldPath = "address.socket_address",
                Message = "listener address is required",
            });
        }

        foreach (var filter in listener.ListenerFilters)
        {
            if (!string.Equals(filter.Name, "envoy.filters.listener.tls_inspector", StringComparison.Ordinal) &&
                !string.Equals(filter.Name, "envoy.filters.listener.proxy_protocol", StringComparison.Ordinal))
            {
                return TranslationResult<ListenerConfig>.Fail(new TranslationError
                {
                    ResourceName = name,
                    FieldPath = "listener_filters",
                    Message = $"unsupported listener filter '{filter.Name}'",
                });
            }
        }

        if (listener.FilterChains.Count != 1)
        {
            return TranslationResult<ListenerConfig>.Fail(new TranslationError
            {
                ResourceName = name,
                FieldPath = "filter_chains",
                Message = "exactly one filter chain is supported",
            });
        }

        var chain = listener.FilterChains[0];
        var socket = listener.Address.SocketAddress;
        var config = new ListenerConfig
        {
            Host = socket.Address,
            Port = (int)socket.PortValue,
        };

        if (chain.FilterChainMatch?.ServerNames is { Count: > 0 } serverNames)
        {
            config.Passthrough = true;
            config.DecryptSsl = false;
            config.SniRoutes = serverNames
                .Select(sn => new SniRouteConfig { ServerName = sn, ClusterId = string.Empty })
                .ToList();
        }

        foreach (var filter in chain.Filters)
        {
            if (filter.TypedConfig is null)
            {
                continue;
            }

            if (string.Equals(filter.Name, "envoy.filters.network.http_connection_manager", StringComparison.Ordinal))
            {
                var hcm = filter.TypedConfig.Unpack<Hcm>();
                var routeName = hcm.RouteConfigName;
                if (string.IsNullOrWhiteSpace(routeName) && hcm.RouteSpecifierCase == Hcm.RouteSpecifierOneofCase.Rds)
                {
                    routeName = hcm.Rds.RouteConfigName;
                }

                if (string.IsNullOrWhiteSpace(routeName))
                {
                    return TranslationResult<ListenerConfig>.Fail(new TranslationError
                    {
                        ResourceName = name,
                        FieldPath = "filter_chains[0].filters[].route_config_name",
                        Message = "http_connection_manager must name an RDS route configuration",
                    });
                }

                config.DecryptSsl = chain.TransportSocket is not null;
                continue;
            }

            if (string.Equals(filter.Name, "envoy.filters.network.tcp_proxy", StringComparison.Ordinal))
            {
                var tcp = filter.TypedConfig.Unpack<TcpProxy>();
                config.Passthrough = true;
                config.DecryptSsl = false;
                config.SniDefaultClusterId = tcp.Cluster;
                if (chain.TransportSocket is not null)
                {
                    config.TerminateTls = true;
                }

                continue;
            }

            return TranslationResult<ListenerConfig>.Fail(new TranslationError
            {
                ResourceName = name,
                FieldPath = "filter_chains[0].filters",
                Message = $"unsupported network filter '{filter.Name}'",
            });
        }

        if (listener.ListenerFilters.Any(f => string.Equals(f.Name, "envoy.filters.listener.proxy_protocol", StringComparison.Ordinal)))
        {
            config.AcceptProxyProtocol = true;
        }

        return TranslationResult<ListenerConfig>.Ok(config);
    }

    internal static Any PackFilter<T>(T message) where T : IMessage<T> =>
        Any.Pack(message);
}

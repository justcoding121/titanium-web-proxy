using Envoy.Config.Core.V3;
using Envoy.Config.Listener.V3;
using Envoy.Extensions.Filters.Network.HttpConnectionManager.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;
using Hcm = Envoy.Extensions.Filters.Network.HttpConnectionManager.V3.HttpConnectionManager;
using TcpProxy = Envoy.Extensions.Filters.Network.TcpProxy.V3.TcpProxy;

namespace Titanium.Xds.Tests;

[TestClass]
public class ListenerTranslatorGoldenTests
{
    [TestMethod]
    public void HttpListener_MapsHostPortAndServerNames()
    {
        var listener = new Listener
        {
            Name = "http",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            FilterChains =
            {
                new FilterChain
                {
                    FilterChainMatch = new FilterChainMatch { ServerNames = { "app.example.com" } },
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.http_connection_manager",
                            TypedConfig = Any.Pack(new Hcm { RouteConfigName = "routes" }),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual("127.0.0.1", result.Value!.Host);
        Assert.AreEqual(8080, result.Value.Port);
        Assert.AreEqual(1, result.Value.SniRoutes.Count);
        Assert.AreEqual("app.example.com", result.Value.SniRoutes[0].ServerName);
    }

    [TestMethod]
    public void MissingAddress_NacksWithFieldPath()
    {
        var listener = new Listener
        {
            Name = "http",
            FilterChains = { new FilterChain() },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "address.socket_address");
    }

    [TestMethod]
    public void UnsupportedListenerFilter_NacksWithFieldPath()
    {
        var listener = new Listener
        {
            Name = "http",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            ListenerFilters = { new ListenerFilter { Name = "envoy.filters.listener.original_dst" } },
            FilterChains = { new FilterChain() },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "listener_filters");
    }

    [TestMethod]
    public void FilterChainCountNotOne_NacksWithFieldPath()
    {
        var listener = new Listener
        {
            Name = "http",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            FilterChains = { new FilterChain(), new FilterChain() },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "filter_chains");
    }

    [TestMethod]
    public void ProxyProtocolListenerFilter_SetsAcceptProxyProtocol()
    {
        var listener = new Listener
        {
            Name = "pp",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            ListenerFilters = { new ListenerFilter { Name = "envoy.filters.listener.proxy_protocol" } },
            FilterChains =
            {
                new FilterChain
                {
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.http_connection_manager",
                            TypedConfig = Any.Pack(new Hcm { RouteConfigName = "routes" }),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsTrue(result.Value!.AcceptProxyProtocol);
    }

    [TestMethod]
    public void HcmWithRdsRouteConfigName_IsAccepted()
    {
        var listener = new Listener
        {
            Name = "rds",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            FilterChains =
            {
                new FilterChain
                {
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.http_connection_manager",
                            TypedConfig = Any.Pack(new Hcm
                            {
                                Rds = new Rds { RouteConfigName = "dynamic-routes" },
                            }),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsFalse(result.Value!.DecryptSsl);
    }

    [TestMethod]
    public void HcmWithNoRouteConfigName_NacksWithFieldPath()
    {
        var listener = new Listener
        {
            Name = "noroutename",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            FilterChains =
            {
                new FilterChain
                {
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.http_connection_manager",
                            TypedConfig = Any.Pack(new Hcm()),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "route_config_name");
    }

    [TestMethod]
    public void TcpProxyWithTransportSocket_SetsTerminateTls()
    {
        var listener = new Listener
        {
            Name = "tls-term",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8443 },
            },
            FilterChains =
            {
                new FilterChain
                {
                    TransportSocket = new TransportSocket { Name = "tls" },
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.tcp_proxy",
                            TypedConfig = Any.Pack(new TcpProxy { Cluster = "backend" }),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsTrue(result.Value!.Passthrough);
        Assert.IsTrue(result.Value.TerminateTls);
        Assert.AreEqual("backend", result.Value.SniDefaultClusterId);
    }

    [TestMethod]
    public void UnsupportedNetworkFilter_NacksWithFieldPath()
    {
        var listener = new Listener
        {
            Name = "unsup",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8080 },
            },
            FilterChains =
            {
                new FilterChain
                {
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.mongo_proxy",
                            TypedConfig = Any.Pack(new Hcm { RouteConfigName = "r" }),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "filters");
    }

    [TestMethod]
    public void TlsInspectorListenerFilter_IsAcceptedWithoutSettingProxyProtocol()
    {
        var listener = new Listener
        {
            Name = "tls-insp",
            Address = new Address
            {
                SocketAddress = new SocketAddress { Address = "127.0.0.1", PortValue = 8443 },
            },
            ListenerFilters = { new ListenerFilter { Name = "envoy.filters.listener.tls_inspector" } },
            FilterChains =
            {
                new FilterChain
                {
                    Filters =
                    {
                        new Filter
                        {
                            Name = "envoy.filters.network.http_connection_manager",
                            TypedConfig = Any.Pack(new Hcm { RouteConfigName = "routes" }),
                        },
                    },
                },
            },
        };

        var result = ListenerTranslator.Translate(listener);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsFalse(result.Value!.AcceptProxyProtocol);
    }
}

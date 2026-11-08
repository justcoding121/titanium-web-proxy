using Envoy.Config.Core.V3;
using Envoy.Config.Listener.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;
using Hcm = Envoy.Extensions.Filters.Network.HttpConnectionManager.V3.HttpConnectionManager;

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
}

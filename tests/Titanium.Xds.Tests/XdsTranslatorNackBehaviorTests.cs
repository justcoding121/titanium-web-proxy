using Envoy.Config.Cluster.V3;
using Envoy.Config.Core.V3;
using Envoy.Config.Listener.V3;
using Envoy.Config.Route.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;
using XdsRoute = Envoy.Config.Route.V3.Route;

namespace Titanium.Xds.Tests;

/// <summary>
///     Translator NACK branches: empty cluster name, RING_HASH, redirect, filter chain count, unsupported listener filter.
/// </summary>
[TestClass]
public class XdsTranslatorNackBehaviorTests
{
    [TestMethod]
    public void ClusterTranslator_NacksEmptyName_UnsupportedType_AndRingHash()
    {
        var empty = ClusterTranslator.Translate(new Cluster { Name = "  " });
        Assert.IsFalse(empty.IsSuccess);
        Assert.AreEqual("name", empty.Error!.FieldPath);

        var type = ClusterTranslator.Translate(new Cluster
        {
            Name = "c1",
            Type = (ClusterDiscoveryType)99,
        });
        Assert.IsFalse(type.IsSuccess);
        Assert.AreEqual("type", type.Error!.FieldPath);
        StringAssert.Contains(type.Error.Message, "unsupported cluster discovery type");

        var ring = ClusterTranslator.Translate(new Cluster
        {
            Name = "c1",
            Type = ClusterDiscoveryType.Eds,
            LbPolicy = LbPolicy.RingHash,
        });
        Assert.IsFalse(ring.IsSuccess);
        Assert.AreEqual("lb_policy", ring.Error!.FieldPath);
        StringAssert.Contains(ring.Error.Message, "RING_HASH");
    }

    [TestMethod]
    public void RouteTranslator_NacksRedirectAction()
    {
        var configuration = new RouteConfiguration
        {
            Name = "r-redirect",
            VirtualHosts =
            {
                new VirtualHost
                {
                    Name = "vh",
                    Domains = { "*" },
                    Routes =
                    {
                        new XdsRoute
                        {
                            Match = new RouteMatch { Prefix = "/" },
                            Redirect = new RedirectAction { HostRedirect = "elsewhere.example" },
                        },
                    },
                },
            },
        };

        var result = RouteTranslator.Translate(configuration);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "redirect");
        StringAssert.Contains(result.Error.Message, "redirect actions are not supported");
    }

    [TestMethod]
    public void ListenerTranslator_NacksWrongFilterChainCount_AndUnsupportedListenerFilter()
    {
        var noChain = ListenerTranslator.Translate(new Listener
        {
            Name = "l0",
            Address = new Address { SocketAddress = new SocketAddress { Address = "0.0.0.0", PortValue = 80 } },
        });
        Assert.IsFalse(noChain.IsSuccess);
        Assert.AreEqual("filter_chains", noChain.Error!.FieldPath);

        var badFilter = ListenerTranslator.Translate(new Listener
        {
            Name = "l1",
            Address = new Address { SocketAddress = new SocketAddress { Address = "0.0.0.0", PortValue = 80 } },
            ListenerFilters = { new ListenerFilter { Name = "envoy.filters.listener.original_dst" } },
            FilterChains = { new FilterChain() },
        });
        Assert.IsFalse(badFilter.IsSuccess);
        Assert.AreEqual("listener_filters", badFilter.Error!.FieldPath);
        StringAssert.Contains(badFilter.Error.Message, "original_dst");
    }
}

using Envoy.Config.Cluster.V3;
using Envoy.Config.Endpoint.V3;
using Envoy.Config.Listener.V3;
using Envoy.Config.Route.V3;
using Envoy.Extensions.TransportSockets.Tls.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Xds.Ads;
using Titanium.Xds.Translation;

namespace Titanium.Xds.Tests;

/// <summary>
///     AdsResourceStore name extraction across type URLs, plus a couple of translator edges still thin on develop.
/// </summary>
[TestClass]
public class AdsResourceStoreNamingBehaviorTests
{
    [TestMethod]
    public void Replace_IndexesResourcesByTypedName_ForEveryKnownTypeUrl()
    {
        var store = new AdsResourceStore();
        store.Replace(XdsTypeUrls.Cluster, [Any.Pack(new Cluster { Name = "c1" })]);
        store.Replace(XdsTypeUrls.ClusterLoadAssignment,
            [Any.Pack(new ClusterLoadAssignment { ClusterName = "c1" })]);
        store.Replace(XdsTypeUrls.RouteConfiguration, [Any.Pack(new RouteConfiguration { Name = "r1" })]);
        store.Replace(XdsTypeUrls.Listener, [Any.Pack(new Listener { Name = "l1" })]);
        store.Replace(XdsTypeUrls.Secret, [Any.Pack(new Secret { Name = "s1" })]);

        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.Cluster, out var clusters) && clusters.ContainsKey("c1"));
        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.ClusterLoadAssignment, out var clas) && clas.ContainsKey("c1"));
        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.RouteConfiguration, out var routes) && routes.ContainsKey("r1"));
        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.Listener, out var listeners) && listeners.ContainsKey("l1"));
        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.Secret, out var secrets) && secrets.ContainsKey("s1"));

        store.ClearType(XdsTypeUrls.Listener);
        Assert.IsFalse(store.TryGetAll(XdsTypeUrls.Listener, out _));
        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.Cluster, out _));
    }

    [TestMethod]
    public void EndpointTranslator_DefaultWeightIsOne_AndZeroWeightBecomesOne()
    {
        // Bug class: missing/zero EDS weight is treated as 0 and the destination is never selected.
        var assignment = new ClusterLoadAssignment
        {
            ClusterName = "c1",
            Endpoints =
            {
                new LocalityLbEndpoints
                {
                    Priority = 2,
                    Locality = new Envoy.Config.Core.V3.Locality { Zone = "z1" },
                    LbEndpoints =
                    {
                        new LbEndpoint
                        {
                            Endpoint = new Envoy.Config.Endpoint.V3.Endpoint
                            {
                                Address = new Envoy.Config.Core.V3.Address
                                {
                                    SocketAddress = new Envoy.Config.Core.V3.SocketAddress
                                    {
                                        Address = "10.0.0.1",
                                        PortValue = 8080,
                                    },
                                },
                            },
                        },
                        new LbEndpoint
                        {
                            LoadBalancingWeight = 0,
                            Endpoint = new Envoy.Config.Endpoint.V3.Endpoint
                            {
                                Address = new Envoy.Config.Core.V3.Address
                                {
                                    SocketAddress = new Envoy.Config.Core.V3.SocketAddress
                                    {
                                        Address = "10.0.0.2",
                                        PortValue = 8080,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var result = EndpointTranslator.Translate(assignment);
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(2, result.Value!.Count);
        Assert.AreEqual(1, result.Value[0].Weight);
        Assert.AreEqual(1, result.Value[1].Weight);
        Assert.AreEqual(2, result.Value[0].Priority);
        Assert.AreEqual("z1", result.Value[0].Zone);
        Assert.AreEqual("c1-0", result.Value[0].Id);
    }

    [TestMethod]
    public void ClusterTranslator_ConnectTimeout_IsClampedIntoHttpClient()
    {
        var result = ClusterTranslator.Translate(new Cluster
        {
            Name = "c1",
            Type = ClusterDiscoveryType.Eds,
            LbPolicy = LbPolicy.LeastRequest,
            ConnectTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(7)),
        });
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(LoadBalanceAlgorithm.LeastRequests, result.Value!.Algorithm);
        Assert.AreEqual(TimeSpan.FromSeconds(7), result.Value.HttpClient!.ConnectTimeout);
    }
}

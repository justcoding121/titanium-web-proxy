using Envoy.Config.Cluster.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Translation;

namespace Titanium.Xds.Tests;

[TestClass]
public class ClusterTranslatorGoldenTests
{
    [TestMethod]
    public void StaticCluster_RoundRobin_MapsToTwpCluster()
    {
        var cluster = new Cluster
        {
            Name = "c1",
            Type = ClusterDiscoveryType.Static,
            LbPolicy = LbPolicy.RoundRobin,
            ConnectTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(5)),
        };

        var result = ClusterTranslator.Translate(cluster);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("c1", result.Value!.Id);
        Assert.AreEqual(Titanium.Web.Proxy.Abstractions.Clusters.LoadBalanceAlgorithm.RoundRobin, result.Value.Algorithm);
        Assert.AreEqual(TimeSpan.FromSeconds(5), result.Value.HttpClient!.ConnectTimeout);
    }

    [TestMethod]
    public void UnsupportedType_NacksWithFieldPath()
    {
        var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.LogicalDns };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "type");
    }

    [TestMethod]
    public void EmptyName_NacksWithNameFieldPath()
    {
        var cluster = new Cluster { Name = string.Empty, Type = ClusterDiscoveryType.Static };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("name", result.Error!.FieldPath);
    }

    [TestMethod]
    public void RingHash_NacksWithLbPolicyFieldPath()
    {
        var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.Static, LbPolicy = LbPolicy.RingHash };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error!.FieldPath, "lb_policy");
    }

    [TestMethod]
    public void LeastRequest_MapsToLeastRequestsAlgorithm()
    {
        var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.Eds, LbPolicy = LbPolicy.LeastRequest };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Titanium.Web.Proxy.Abstractions.Clusters.LoadBalanceAlgorithm.LeastRequests, result.Value!.Algorithm);
    }

    [TestMethod]
    public void Random_MapsToRandomAlgorithm()
    {
        var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.Eds, LbPolicy = LbPolicy.Random };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Titanium.Web.Proxy.Abstractions.Clusters.LoadBalanceAlgorithm.Random, result.Value!.Algorithm);
    }

    [TestMethod]
    public void StrictDns_IsAccepted()
    {
        var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.StrictDns };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("c1", result.Value!.Id);
    }

    [TestMethod]
    public void NoConnectTimeout_YieldsNullHttpClient()
    {
        var cluster = new Cluster { Name = "c1", Type = ClusterDiscoveryType.Static };
        var result = ClusterTranslator.Translate(cluster);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value!.HttpClient);
    }
}

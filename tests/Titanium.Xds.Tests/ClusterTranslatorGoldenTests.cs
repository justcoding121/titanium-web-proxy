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
}

using Envoy.Config.Cluster.V3;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Xds.Ads;

namespace Titanium.Xds.Tests;

[TestClass]
public class AdsResourceStoreTests
{
    [TestMethod]
    public void Replace_TryGetAll_ClearType_RoundTripsNamedResources()
    {
        var store = new AdsResourceStore();
        var cluster = Any.Pack(new Cluster { Name = "c1" });

        store.Replace(XdsTypeUrls.Cluster, [cluster]);
        Assert.IsTrue(store.TryGetAll(XdsTypeUrls.Cluster, out var resources));
        Assert.AreEqual(1, resources.Count);
        Assert.IsTrue(resources.ContainsKey("c1"));

        store.ClearType(XdsTypeUrls.Cluster);
        Assert.IsFalse(store.TryGetAll(XdsTypeUrls.Cluster, out _));
    }

    [TestMethod]
    public void TryGetAll_UnknownType_ReturnsEmptyDictionary()
    {
        var store = new AdsResourceStore();
        Assert.IsFalse(store.TryGetAll("type.googleapis.com/unknown", out var resources));
        Assert.AreEqual(0, resources.Count);
    }
}

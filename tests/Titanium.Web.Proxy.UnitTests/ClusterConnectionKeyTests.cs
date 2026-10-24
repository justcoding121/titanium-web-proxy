using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ClusterConnectionKeyTests
{
    [TestMethod]
    public void UnsetHttpClient_DoesNotChangeThePoolKey()
    {
        var plain = TcpConnectionFactory.GetConnectionCacheKey("origin", 80, false, null, null, null);
        var explicitNull = TcpConnectionFactory.GetConnectionCacheKey("origin", 80, false, null, null, null, clusterKey: null);
        Assert.AreEqual(plain, explicitNull);
        var isolated = TcpConnectionFactory.GetConnectionCacheKey("origin", 80, false, null, null, null, clusterKey: "Tls12|True|");
        Assert.AreNotEqual(plain, isolated);
    }

    [TestMethod]
    public void DefaultHttpClient_HasAnEmptyConnectionKey()
    {
        Assert.AreEqual(string.Empty, new ClusterHttpClientConfig().ConnectionKey);
        Assert.AreNotEqual(string.Empty, new ClusterHttpClientConfig { AcceptAnyServerCertificate = true }.ConnectionKey);
    }
}

using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class NetworkHelperTests
{
    [TestMethod]
    public void IsLocalIpAddress_LoopbackIp_ReturnsTrue()
    {
        Assert.IsTrue(NetworkHelper.IsLocalIpAddress(IPAddress.Loopback));
        Assert.IsTrue(NetworkHelper.IsLocalIpAddress(IPAddress.IPv6Loopback));
    }

    [TestMethod]
    public void IsLocalIpAddress_LocalhostName_ReturnsTrue()
    {
        Assert.IsTrue(NetworkHelper.IsLocalIpAddress("localhost"));
        Assert.IsTrue(NetworkHelper.IsLocalIpAddress("127.0.0.1"));
    }

    [TestMethod]
    public void IsLocalIpAddress_ProxyDnsRequests_SkipsReverseLookupForRemoteHost()
    {
        // With proxyDnsRequests=true the helper must not reverse-DNS; a non-local hostname returns false.
        Assert.IsFalse(NetworkHelper.IsLocalIpAddress("example.com", proxyDnsRequests: true));
    }
}

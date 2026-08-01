using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3.Dns;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class OsConfiguredDnsServerTests
{
    [TestMethod]
    public void TryGetPrimaryDnsServer_NeverReturnsPublicGoogleDnsByDefault()
    {
        OsConfiguredDnsServer.InvalidateCache();
        var endpoint = OsConfiguredDnsServer.TryGetPrimaryDnsServer();

        // Discovery may return null on locked-down CI images; when it returns an endpoint it must
        // come from the OS configuration, not a hard-coded public resolver fallback.
        if (endpoint != null)
        {
            Assert.AreNotEqual("8.8.8.8", endpoint.Address.ToString(),
                "OS discovery must not silently fall back to Google Public DNS.");
            Assert.AreNotEqual("8.8.4.4", endpoint.Address.ToString());
            Assert.AreEqual(53, endpoint.Port);
        }
    }

    [TestMethod]
    public void DnsServerEndPoint_Default_IsNotPublicResolver()
    {
        OsConfiguredDnsServer.InvalidateCache();
        using var server = new ProxyServer(false, false, false);
#pragma warning disable TWP001
        var endpoint = server.DnsServerEndPoint;
#pragma warning restore TWP001

        Assert.AreNotEqual("8.8.8.8", endpoint.Address.ToString());
        Assert.AreNotEqual("8.8.4.4", endpoint.Address.ToString());
    }
}

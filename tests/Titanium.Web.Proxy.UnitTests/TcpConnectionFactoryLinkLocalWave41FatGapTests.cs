#pragma warning disable TWP001
using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="TcpConnectionFactory" /> suite: link-local and RFC1918 outbound blocks.
/// </summary>
[TestClass]
public class TcpConnectionFactoryLinkLocalWave41FatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void EvaluateOutboundPolicy_BlocksLinkLocal169254()
    {
        using var proxy = new ProxyServer(false, false, false) { BlockPrivateNetworkDestinations = true };
        var method = typeof(TcpConnectionFactory).GetMethod("EvaluateOutboundDestinationPolicy", PrivateStatic)!;
        var blocked = (OutboundDestinationBlockedException?)method.Invoke(null,
            [proxy, IPAddress.Parse("169.254.1.1"), "169.254.1.1", false]);
        Assert.IsNotNull(blocked);
    }

    [TestMethod]
    public void GetConnectionCacheKey_UpstreamProxyToken_IsStable()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, 3128);
        var withProxy = TcpConnectionFactory.GetConnectionCacheKey(
            "origin.wave41.test", 443, true, null, ep, null);
        var without = TcpConnectionFactory.GetConnectionCacheKey(
            "origin.wave41.test", 443, true, null, null, null);
        StringAssert.Contains(withProxy, "3128");
        Assert.AreNotEqual(withProxy, without);
    }
}
#pragma warning restore TWP001

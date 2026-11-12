#pragma warning disable TWP001
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="TcpConnectionFactory" /> suite: outbound destination blocks and cluster key helpers.
/// </summary>
[TestClass]
public class TcpConnectionFactoryOutboundPolicyFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void EvaluateOutboundDestinationPolicy_BlocksPrivateWhenEnabled()
    {
        using var proxy = new ProxyServer(false, false, false) { BlockPrivateNetworkDestinations = true };
        var method = typeof(TcpConnectionFactory).GetMethod("EvaluateOutboundDestinationPolicy", PrivateStatic)!;
        var blocked = (OutboundDestinationBlockedException?)method.Invoke(null,
            [proxy, IPAddress.Parse("10.0.0.5"), "10.0.0.5", false])!;
        Assert.IsNotNull(blocked);
        Assert.IsNull(method.Invoke(null, [proxy, IPAddress.Parse("10.0.0.5"), "10.0.0.5", true]));
    }

    [TestMethod]
    public void EvaluateOutboundDestinationPolicy_BlocksLoopbackWhenEnabled()
    {
        using var proxy = new ProxyServer(false, false, false) { BlockLoopbackDestinations = true };
        var method = typeof(TcpConnectionFactory).GetMethod("EvaluateOutboundDestinationPolicy", PrivateStatic)!;
        var blocked = (OutboundDestinationBlockedException?)method.Invoke(null,
            [proxy, IPAddress.Loopback, "127.0.0.1", false])!;
        Assert.IsNotNull(blocked);
        StringAssert.Contains(blocked!.Message, "BlockLoopbackDestinations");
    }

    [TestMethod]
    public void GetConnectionCacheKey_IncludesSortedAlpnTokens()
    {
        var keySingle = TcpConnectionFactory.GetConnectionCacheKey(
            "origin.test", 443, true,
            [SslApplicationProtocol.Http2], null, null);
        var keyMulti = TcpConnectionFactory.GetConnectionCacheKey(
            "origin.test", 443, true,
            [SslApplicationProtocol.Http11, SslApplicationProtocol.Http2], null, null);
        StringAssert.Contains(keySingle, SslApplicationProtocol.Http2.ToString());
        StringAssert.Contains(keyMulti, SslApplicationProtocol.Http2.ToString());
        Assert.AreNotEqual(keySingle, keyMulti);
    }

    [TestMethod]
    public void ClusterTargetHost_WithoutCluster_FallsBackToRemoteHost()
    {
        var method = typeof(TcpConnectionFactory).GetMethod("ClusterTargetHost", PrivateStatic)!;
        Assert.AreEqual("public.example", (string)method.Invoke(null, [null, "public.example"])!);
    }
}
#pragma warning restore TWP001

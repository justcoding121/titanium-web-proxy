using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class QuicLoopbackBindTests
{
    [TestMethod]
    public void Localhost_BindsTheClientSocketToIpv6Loopback()
    {
        Assert.IsTrue(QuicConnectionFactory.TryLoopbackBind("localhost", 443, out var remote, out var local));
        Assert.AreEqual(IPAddress.IPv6Loopback, remote.Address);
        Assert.AreEqual(443, remote.Port);
        Assert.AreEqual(IPAddress.IPv6Loopback, local.Address);
        Assert.AreEqual(0, local.Port);
    }

    [TestMethod]
    public void Ipv4Loopback_BindsTheMatchingFamily()
    {
        Assert.IsTrue(QuicConnectionFactory.TryLoopbackBind("127.0.0.1", 8443, out var remote, out var local));
        Assert.AreEqual(IPAddress.Loopback, remote.Address);
        Assert.AreEqual(IPAddress.Loopback, local.Address);
        Assert.AreEqual(8443, remote.Port);
    }

    [TestMethod]
    public void PublicHost_StaysUnbound()
    {
        Assert.IsFalse(QuicConnectionFactory.TryLoopbackBind("example.com", 443, out _, out _));
    }
}

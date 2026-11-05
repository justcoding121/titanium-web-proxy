using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyListenExceptionTests
{
    [TestMethod]
    public void Message_NamesAddressAlreadyInUse()
    {
        var ex = new ProxyListenException(
            "explicit", IPAddress.Loopback, 8000, new SocketException(10048));

        StringAssert.Contains(ex.Message, "Failed to bind explicit listener 127.0.0.1:8000: address already in use.");
        Assert.IsTrue(ex.IsAddressInUse);
        Assert.AreEqual(IPAddress.Loopback, ex.Data["ipAddress"]);
        Assert.AreEqual(8000, ex.Data["port"]);
        Assert.AreEqual(8000, ex.Port);
    }

    [TestMethod]
    public void Message_NamesAccessDenied()
    {
        var ex = new ProxyListenException(
            "transparent", IPAddress.Loopback, 80, new SocketException(10013));

        StringAssert.Contains(ex.Message, "access denied");
        StringAssert.Contains(ex.Message, "127.0.0.1:80");
        Assert.IsFalse(ex.IsAddressInUse);
    }

    [TestMethod]
    public void Message_IncludesOtherSocketErrors()
    {
        var ex = new ProxyListenException(
            "socks", IPAddress.Loopback, 1080, new SocketException(10061));

        StringAssert.Contains(ex.Message, "Failed to bind socks listener 127.0.0.1:1080:");
        StringAssert.Contains(ex.Message, "ConnectionRefused");
    }

    [TestMethod]
    public void Message_BracketsIpv6()
    {
        var ex = new ProxyListenException(
            "explicit", IPAddress.IPv6Loopback, 8000, new SocketException(10048));

        StringAssert.Contains(ex.Message, "[::1]:8000");
    }
}

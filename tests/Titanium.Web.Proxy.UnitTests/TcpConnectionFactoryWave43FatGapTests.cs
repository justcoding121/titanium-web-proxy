#pragma warning disable TWP001
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TcpConnectionFactoryWave43FatGapTests
{
    [TestMethod]
    public void PreviewToString_EmptyAndContent()
    {
        var preview = typeof(TcpConnectionFactory).GetMethod(
            "PreviewToString", Wave42FatGapTestSupport.PrivateStatic)!;
        using var empty = new MemoryStream();
        Assert.IsNull(preview.Invoke(null, [empty]));
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes("peek"));
        Assert.AreEqual("peek", (string)preview.Invoke(null, [ms])!);
    }

    [TestMethod]
    public void InterleaveByAddressFamily_PrefersIpv6FirstWhenBothPresent()
    {
        var v4 = IPAddress.Parse("127.0.0.1");
        var v6 = IPAddress.IPv6Loopback;
        var ordered = TcpConnectionFactory.InterleaveByAddressFamily([v4, v6]);
        Assert.AreEqual(2, ordered.Length);
        Assert.AreEqual(AddressFamily.InterNetworkV6, ordered[0].AddressFamily);
    }
}
#pragma warning restore TWP001

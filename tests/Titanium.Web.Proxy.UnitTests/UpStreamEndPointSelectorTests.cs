using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UpStreamEndPointSelectorTests
{
    private static readonly IPEndPoint IPv4Adapter = new(IPAddress.Parse("10.0.0.2"), 0);
    private static readonly IPEndPoint IPv6Adapter = new(IPAddress.Parse("2001:db8::2"), 0);
    private static readonly IPEndPoint LegacyIPv4 = new(IPAddress.Parse("10.0.0.9"), 0);

    [TestMethod]
    public void Resolve_IPv4Only_Uses_IPv4_Endpoint()
    {
        var selected = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetwork,
            null, IPv4Adapter, null, null, null, null);

        Assert.AreEqual(IPv4Adapter, selected);
    }

    [TestMethod]
    public void Resolve_IPv6Only_Uses_IPv6_Endpoint()
    {
        var selected = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetworkV6,
            null, null, IPv6Adapter, null, null, null);

        Assert.AreEqual(IPv6Adapter, selected);
    }

    [TestMethod]
    public void Resolve_DualStack_Picks_Matching_Family()
    {
        var forV4 = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetwork,
            null, IPv4Adapter, IPv6Adapter, null, null, null);
        var forV6 = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetworkV6,
            null, IPv4Adapter, IPv6Adapter, null, null, null);

        Assert.AreEqual(IPv4Adapter, forV4);
        Assert.AreEqual(IPv6Adapter, forV6);
    }

    [TestMethod]
    public void Resolve_LegacyWrongFamily_Falls_Back_To_Null()
    {
        // Legacy UpStreamEndPoint is IPv4-only; IPv6 destination must not force that bind.
        var selected = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetworkV6,
            LegacyIPv4, null, null, null, null, null);

        Assert.IsNull(selected, "Wrong-family legacy endpoint must be ignored for dual-stack fallback.");
    }

    [TestMethod]
    public void Resolve_LegacyMatchingFamily_Is_Used()
    {
        var selected = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetwork,
            LegacyIPv4, null, null, null, null, null);

        Assert.AreEqual(LegacyIPv4, selected);
    }

    [TestMethod]
    public void Resolve_SessionFamilySpecific_Beats_Server()
    {
        var sessionV4 = new IPEndPoint(IPAddress.Parse("10.0.0.3"), 0);
        var selected = UpStreamEndPointSelector.Resolve(AddressFamily.InterNetwork,
            null, sessionV4, null, LegacyIPv4, IPv4Adapter, IPv6Adapter);

        Assert.AreEqual(sessionV4, selected);
    }

    [TestMethod]
    public void CacheKey_Separates_IPv4_And_IPv6_Bind_Config()
    {
        var factory = new TcpConnectionFactory(new ProxyServer());
        try
        {
            var ipv4Only = TcpConnectionFactory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                upStreamEndPointIPv4: IPv4Adapter);
            var ipv6Only = TcpConnectionFactory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                upStreamEndPointIPv6: IPv6Adapter);
            var dual = TcpConnectionFactory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                upStreamEndPointIPv4: IPv4Adapter, upStreamEndPointIPv6: IPv6Adapter);
            var dualAgain = TcpConnectionFactory.GetConnectionCacheKey("example.com", 443, true, null, null, null,
                upStreamEndPointIPv4: IPv4Adapter, upStreamEndPointIPv6: IPv6Adapter);

            Assert.AreNotEqual(ipv4Only, ipv6Only);
            Assert.AreNotEqual(ipv4Only, dual);
            Assert.AreNotEqual(ipv6Only, dual);
            Assert.AreEqual(dual, dualAgain);
        }
        finally
        {
            factory.Dispose();
        }
    }

    [TestMethod]
    public void AppendToCacheKey_Includes_Family_Tags()
    {
        var sb = new StringBuilder("base");
        UpStreamEndPointSelector.AppendToCacheKey(sb, LegacyIPv4, IPv4Adapter, IPv6Adapter);
        var key = sb.ToString();

        StringAssert.Contains(key, "g:");
        StringAssert.Contains(key, "4:");
        StringAssert.Contains(key, "6:");
    }
}

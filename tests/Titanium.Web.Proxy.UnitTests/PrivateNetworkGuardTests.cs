using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Phase E.15: outbound destination policy hook - "add an explicit hook for private, link-local
///     and metadata addresses."
/// </summary>
[TestClass]
public class PrivateNetworkGuardTests
{
    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.0.0.5")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.168.1.1")]
    [DataRow("169.254.169.254")] // cloud metadata endpoint (AWS/GCP/Azure)
    [DataRow("169.254.1.1")]
    [DataRow("0.0.0.0")]
    [DataRow("100.64.0.1")]
    [DataRow("255.255.255.255")]
    [DataRow("224.0.0.1")]
    [DataRow("::1")]
    [DataRow("::")]
    [DataRow("fe80::1")]
    [DataRow("ff02::1")]
    [DataRow("fec0::1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456:789a::1")]
    [DataRow("::ffff:10.0.0.1")]
    [DataRow("64:ff9b::7f00:1")]
    [DataRow("64:ff9b::a00:1")]
    [DataRow("2002:7f00:1::1")]
    [DataRow("2001:0:0:0:0:0:f5ff:fffe")]
    [DataRow("192.0.0.8")]
    [DataRow("192.0.2.1")]
    [DataRow("198.18.0.1")]
    [DataRow("198.51.100.1")]
    [DataRow("203.0.113.1")]
    public void IsBlocked_PrivateLinkLocalLoopbackAndMetadataAddresses_ReturnsTrue(string address)
    {
        Assert.IsTrue(PrivateNetworkGuard.IsBlocked(IPAddress.Parse(address)),
            $"{address} should be classified as blocked");
    }

    [DataTestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("1.1.1.1")]
    [DataRow("93.184.216.34")]
    [DataRow("2606:4700:4700::1111")]
    [DataRow("64:ff9b::808:808")]
    [DataRow("2002:808:808::1")]
    [DataRow("2001:0:0:0:0:0:f7f7:f7f7")]
    public void IsBlocked_GloballyRoutableAddresses_ReturnsFalse(string address)
    {
        Assert.IsFalse(PrivateNetworkGuard.IsBlocked(IPAddress.Parse(address)),
            $"{address} should be classified as globally routable, not blocked");
    }

    [TestMethod]
    public void IsBlocked_IPv4MappedIPv6Loopback_UnwrapsAndBlocks()
    {
        Assert.IsTrue(PrivateNetworkGuard.IsBlocked(IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("127.0.0.2")]
    [DataRow("::1")]
    [DataRow("::ffff:127.0.0.1")]
    public void IsLoopback_LoopbackAddresses_ReturnsTrue(string address)
    {
        Assert.IsTrue(PrivateNetworkGuard.IsLoopback(IPAddress.Parse(address)),
            $"{address} should be classified as loopback");
    }

    [DataTestMethod]
    [DataRow("10.0.0.5")]
    [DataRow("8.8.8.8")]
    [DataRow("169.254.169.254")]
    [DataRow("192.168.1.1")]
    [DataRow("fc00::1")]
    [DataRow("fe80::1")]
    public void IsLoopback_NonLoopbackAddresses_ReturnsFalse(string address)
    {
        Assert.IsFalse(PrivateNetworkGuard.IsLoopback(IPAddress.Parse(address)),
            $"{address} should not be classified as loopback");
    }
}

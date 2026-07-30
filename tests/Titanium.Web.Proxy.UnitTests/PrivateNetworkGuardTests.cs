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
    [DataRow("fe80::1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456:789a::1")]
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
}

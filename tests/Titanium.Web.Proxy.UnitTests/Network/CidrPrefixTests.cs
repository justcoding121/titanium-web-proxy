using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Network;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class CidrPrefixTests
{
    [TestMethod]
    public void Contains_MatchesIpv4AndIpv6Prefixes()
    {
        Assert.IsTrue(CidrPrefix.TryParse("10.0.0.0/8", out var v4));
        Assert.IsTrue(v4!.Contains(IPAddress.Parse("10.1.2.3")));
        Assert.IsFalse(v4.Contains(IPAddress.Parse("11.0.0.1")));
        Assert.IsTrue(v4.Contains(IPAddress.Parse("::ffff:10.9.8.7")));

        Assert.IsTrue(CidrPrefix.TryParse("2001:db8::/32", out var v6));
        Assert.IsTrue(v6!.Contains(IPAddress.Parse("2001:db8::1")));
        Assert.IsFalse(v6.Contains(IPAddress.Parse("2001:db9::1")));

        Assert.IsTrue(CidrPrefix.TryParse("192.168.1.9", out var host));
        Assert.IsTrue(host!.Contains(IPAddress.Parse("192.168.1.9")));
        Assert.IsFalse(host.Contains(IPAddress.Parse("192.168.1.10")));
    }

    [TestMethod]
    public void TryParse_RejectsMalformedText()
    {
        Assert.IsFalse(CidrPrefix.TryParse(null, out _));
        Assert.IsFalse(CidrPrefix.TryParse("not-a-cidr", out _));
        Assert.IsFalse(CidrPrefix.TryParse("10.0.0.0/99", out _));
        Assert.IsFalse(CidrPrefix.TryParse("10.0.0.0/", out _));
    }

    [TestMethod]
    public void Endpoint_EmptyListTrustsAnyPeer_AndRejectsABadPrefix()
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)
        {
            AcceptProxyProtocol = true,
        };
        Assert.IsTrue(endPoint.IsTrustedProxyProtocolPeer(IPAddress.Parse("8.8.8.8")));

        endPoint.TrustedProxyProtocolSources = ["10.0.0.0/8"];
        Assert.IsTrue(endPoint.IsTrustedProxyProtocolPeer(IPAddress.Parse("10.2.3.4")));
        Assert.IsFalse(endPoint.IsTrustedProxyProtocolPeer(IPAddress.Parse("8.8.8.8")));
        Assert.IsFalse(endPoint.IsTrustedProxyProtocolPeer(null));

        Assert.ThrowsExactly<FormatException>(() => endPoint.TrustedProxyProtocolSources = ["nope"]);
    }
}

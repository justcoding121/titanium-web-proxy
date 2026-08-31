using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Ipv6UnreachableSoftSkipTests
{
    [TestInitialize]
    public void Reset() => Ipv6UnreachableSoftSkip.ResetForTests();

    [TestMethod]
    public void DefaultTtl_IsFiveMinutes()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(5), Ipv6UnreachableSoftSkip.DefaultTtl);
    }

    [TestMethod]
    public void IsIpv6Unreachable_ClassifiesUnreachableFamily()
    {
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsIpv6Unreachable(
            new SocketException((int)SocketError.NetworkUnreachable)));
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsIpv6Unreachable(
            new SocketException((int)SocketError.HostUnreachable)));
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsIpv6Unreachable(
            new SocketException((int)SocketError.NetworkDown)));
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsIpv6Unreachable(
            new SocketException((int)SocketError.AddressNotAvailable)));
        Assert.IsFalse(Ipv6UnreachableSoftSkip.IsIpv6Unreachable(
            new SocketException((int)SocketError.ConnectionRefused)));
    }

    [TestMethod]
    public void ExplicitShortTtl_ExpiresSkipWindow()
    {
        var v6 = IPAddress.Parse("2001:db8::1");
        var unreachable = new SocketException((int)SocketError.NetworkUnreachable);
        Ipv6UnreachableSoftSkip.RecordAttemptFailure(v6, unreachable, enabled: true,
            ttl: TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsSkipping());
        Thread.Sleep(30);
        Assert.IsFalse(Ipv6UnreachableSoftSkip.IsSkipping(ttl: TimeSpan.FromMilliseconds(1)));
    }

    [TestMethod]
    public void FilterIfSkipping_WhenNotArmed_ReturnsOriginal()
    {
        var input = new[] { IPAddress.Parse("2001:db8::1"), IPAddress.Parse("192.0.2.1") };
        var result = Ipv6UnreachableSoftSkip.FilterIfSkipping(input, enabled: true);
        CollectionAssert.AreEqual(input, result);
    }

    [TestMethod]
    public void OneIpv6UnreachableStrike_ThenFiltersIpv6()
    {
        var v6 = IPAddress.Parse("2001:db8::1");
        var v4 = IPAddress.Parse("192.0.2.1");
        var unreachable = new SocketException((int)SocketError.NetworkUnreachable);

        Ipv6UnreachableSoftSkip.RecordAttemptFailure(v6, unreachable, enabled: true);
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsSkipping(),
            "one NetworkUnreachable on broken dual-stack should arm soft-skip immediately");

        var filtered = Ipv6UnreachableSoftSkip.FilterIfSkipping(new[] { v6, v4 }, enabled: true);
        CollectionAssert.AreEqual(new[] { v4 }, filtered);
    }

    [TestMethod]
    public void FilterIfSkipping_Ipv6OnlyList_DoesNotEmptyRace()
    {
        var v6a = IPAddress.Parse("2001:db8::1");
        var v6b = IPAddress.Parse("2001:db8::2");
        var unreachable = new SocketException((int)SocketError.NetworkUnreachable);
        Ipv6UnreachableSoftSkip.RecordAttemptFailure(v6a, unreachable, enabled: true);

        var input = new[] { v6a, v6b };
        var filtered = Ipv6UnreachableSoftSkip.FilterIfSkipping(input, enabled: true);
        CollectionAssert.AreEqual(input, filtered);
    }

    [TestMethod]
    public void SuccessfulIpv6Connect_ClearsSkip()
    {
        var v6 = IPAddress.Parse("2001:db8::1");
        var unreachable = new SocketException((int)SocketError.NetworkUnreachable);
        Ipv6UnreachableSoftSkip.RecordAttemptFailure(v6, unreachable, enabled: true);
        Assert.IsTrue(Ipv6UnreachableSoftSkip.IsSkipping());

        Ipv6UnreachableSoftSkip.RecordAttemptSuccess(v6, enabled: true);
        Assert.IsFalse(Ipv6UnreachableSoftSkip.IsSkipping());
    }

    [TestMethod]
    public void Disabled_NeverArmsOrFilters()
    {
        var v6 = IPAddress.Parse("2001:db8::1");
        var v4 = IPAddress.Parse("192.0.2.1");
        var unreachable = new SocketException((int)SocketError.NetworkUnreachable);
        Ipv6UnreachableSoftSkip.RecordAttemptFailure(v6, unreachable, enabled: false);
        Assert.IsFalse(Ipv6UnreachableSoftSkip.IsSkipping());

        var input = new[] { v6, v4 };
        CollectionAssert.AreEqual(input, Ipv6UnreachableSoftSkip.FilterIfSkipping(input, enabled: false));
    }
}

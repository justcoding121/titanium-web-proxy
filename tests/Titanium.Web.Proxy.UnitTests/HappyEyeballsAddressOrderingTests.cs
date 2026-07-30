using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Phase F.18: "Add address racing in TcpConnectionFactory ... covering the RFC 8305 Connection
///     Attempt Delay and address-family interleaving." These tests cover
///     <see cref="TcpConnectionFactory.InterleaveByAddressFamily" /> in isolation, since the actual race
///     (staggered concurrent connects) requires real sockets and is covered instead by
///     UpStreamEndPointFamilyTests and ConnectionPoolTests at the integration level.
/// </summary>
[TestClass]
public class HappyEyeballsAddressOrderingTests
{
    private static IPAddress V4(string s)
    {
        return IPAddress.Parse(s);
    }

    private static IPAddress V6(string s)
    {
        return IPAddress.Parse(s);
    }

    [TestMethod]
    public void SingleAddress_ReturnedUnchanged()
    {
        var input = new[] { V4("10.0.0.1") };

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        CollectionAssert.AreEqual(input, result);
    }

    [TestMethod]
    public void EmptyArray_ReturnedUnchanged()
    {
        var input = System.Array.Empty<IPAddress>();

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        CollectionAssert.AreEqual(input, result);
    }

    [TestMethod]
    public void SingleFamilyOnly_OrderIsUnchanged()
    {
        var input = new[] { V4("10.0.0.1"), V4("10.0.0.2"), V4("10.0.0.3") };

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        CollectionAssert.AreEqual(input, result);
    }

    [TestMethod]
    public void MixedFamilies_InterleavesStartingWithIPv4_EvenWhenResolverListedIPv6First()
    {
        // Resolver order deliberately puts IPv6 first, to verify the interleave does not just
        // preserve whichever family happened to come first in the input.
        var v6A = V6("2001:db8::1");
        var v6B = V6("2001:db8::2");
        var v4A = V4("192.0.2.1");
        var v4B = V4("192.0.2.2");
        var input = new[] { v6A, v6B, v4A, v4B };

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        CollectionAssert.AreEqual(new[] { v4A, v6A, v4B, v6B }, result);
    }

    [TestMethod]
    public void MixedFamilies_PreservesRelativeOrderWithinEachFamily()
    {
        var v4A = V4("192.0.2.1");
        var v4B = V4("192.0.2.2");
        var v4C = V4("192.0.2.3");
        var v6A = V6("2001:db8::1");
        var input = new[] { v4A, v4B, v6A, v4C };

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        // IPv4 addresses must stay in their original relative order (A, B, C), each paired in turn
        // with the single IPv6 address, then the remaining IPv4 addresses trail off unchanged.
        CollectionAssert.AreEqual(new[] { v4A, v6A, v4B, v4C }, result);
    }

    [TestMethod]
    public void UnequalFamilyCounts_TrailingAddressesKeepTheirOrder()
    {
        var v4A = V4("192.0.2.1");
        var v6A = V6("2001:db8::1");
        var v6B = V6("2001:db8::2");
        var v6C = V6("2001:db8::3");
        var input = new[] { v4A, v6A, v6B, v6C };

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        CollectionAssert.AreEqual(new[] { v4A, v6A, v6B, v6C }, result);
    }

    [TestMethod]
    public void ResultContainsExactlyTheSameAddressesAsInput()
    {
        var input = new[]
        {
            V6("2001:db8::1"), V4("192.0.2.1"), V6("2001:db8::2"), V4("192.0.2.2"), V4("192.0.2.3")
        };

        var result = TcpConnectionFactory.InterleaveByAddressFamily(input);

        Assert.AreEqual(input.Length, result.Length);
        CollectionAssert.AreEquivalent(input, result);
    }
}

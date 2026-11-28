using System;
using System.Net;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public sealed class ManagedStackFlagTests
{
    [TestMethod]
    public void TheFlagsDefaultOffAndCanChangeBeforeStart()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsFalse(proxy.UseManagedTlsStack);
        Assert.IsFalse(proxy.UseManagedQuicStack);

        proxy.UseManagedTlsStack = true;
        proxy.UseManagedQuicStack = true;
        Assert.IsTrue(proxy.UseManagedTlsStack);
        Assert.IsTrue(proxy.UseManagedQuicStack);

        proxy.UseManagedTlsStack = false;
        proxy.UseManagedQuicStack = false;
        Assert.IsFalse(proxy.UseManagedTlsStack);
        Assert.IsFalse(proxy.UseManagedQuicStack);
    }

    [TestMethod]
    public void TheSuiteSwitchTurnsBothFlagsOn()
    {
        string? previous = Environment.GetEnvironmentVariable("TWP_MANAGED_TLS");
        try
        {
            Environment.SetEnvironmentVariable("TWP_MANAGED_TLS", "1");
            using var proxy = new ProxyServer(false, false, false);
            Assert.IsTrue(proxy.UseManagedTlsStack);
            Assert.IsTrue(proxy.UseManagedQuicStack);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TWP_MANAGED_TLS", previous);
        }
    }

    [TestMethod]
    public void UpstreamFingerprintFailsClosed()
    {
        using var off = new ProxyServer(false, false, false);
        var profile = new ClusterHttpClientConfig { Fingerprint = "chrome" };
        ManagedStackRequiredException denied = Assert.ThrowsExactly<ManagedStackRequiredException>(
            () => off.RejectUpstreamFingerprint(profile));
        StringAssert.Contains(denied.Message, "UseManagedTlsStack");
        StringAssert.Contains(denied.Message, ManagedStackRequiredException.Reason);

        using var on = new ProxyServer(false, false, false);
        on.UseManagedTlsStack = true;
        NotSupportedException builtIn = Assert.ThrowsExactly<NotSupportedException>(
            () => on.RejectUpstreamFingerprint(profile));
        StringAssert.Contains(builtIn.Message, "built-in hello");
        on.RejectUpstreamFingerprint(null);
    }

    [TestMethod]
    public async Task ADifferentValueAfterStartThrowsAndTheSameValueDoesNot()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start(changeSystemProxySettings: false);
        try
        {
            Assert.IsFalse(proxy.UseManagedTlsStack);
            Assert.IsFalse(proxy.UseManagedQuicStack);
            proxy.UseManagedTlsStack = false;
            proxy.UseManagedQuicStack = false;

            InvalidOperationException tls = Assert.ThrowsExactly<InvalidOperationException>(() => proxy.UseManagedTlsStack = true);
            StringAssert.Contains(tls.Message, nameof(ProxyServer.UseManagedTlsStack));
            InvalidOperationException quic = Assert.ThrowsExactly<InvalidOperationException>(() => proxy.UseManagedQuicStack = true);
            StringAssert.Contains(quic.Message, nameof(ProxyServer.UseManagedQuicStack));
            Assert.IsFalse(proxy.UseManagedTlsStack);
            Assert.IsFalse(proxy.UseManagedQuicStack);
        }
        finally
        {
            await proxy.StopAsync();
        }

        proxy.UseManagedTlsStack = true;
        proxy.UseManagedQuicStack = true;
        Assert.IsTrue(proxy.UseManagedTlsStack);
        Assert.IsTrue(proxy.UseManagedQuicStack);
    }

    [TestMethod]
    public async Task TheFipsOverrideFreezesAfterStart()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.AllowManagedStackUnderFips = true;
        proxy.UseManagedTlsStack = true;
        proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false));
        proxy.Start(changeSystemProxySettings: false);
        try
        {
            Assert.IsTrue(proxy.AllowManagedStackUnderFips);
            proxy.AllowManagedStackUnderFips = true;
            Assert.ThrowsExactly<InvalidOperationException>(() => proxy.AllowManagedStackUnderFips = false);
        }
        finally
        {
            await proxy.StopAsync();
        }
    }

    [TestMethod]
    public async Task TheManagedDialResolvesAnAddressLiteral()
    {
        IPEndPoint resolved = await QuicConnectionFactory.ResolveRemoteAsync(
            new DnsEndPoint("127.0.0.1", 443), CancellationToken.None);

        Assert.AreEqual(IPAddress.Loopback, resolved.Address);
        Assert.AreEqual(443, resolved.Port);

        IPEndPoint kept = await QuicConnectionFactory.ResolveRemoteAsync(
            new IPEndPoint(IPAddress.IPv6Loopback, 443), CancellationToken.None);
        Assert.AreEqual(IPAddress.IPv6Loopback, kept.Address);
    }
}

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

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
}

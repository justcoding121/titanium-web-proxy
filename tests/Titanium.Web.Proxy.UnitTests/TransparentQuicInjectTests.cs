using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public sealed class TransparentQuicInjectTests
{
    [TestMethod]
    public async Task TheFlagOffPathRefusesTransparentQuic()
    {
        using var proxy = new ProxyServer(false, false, false);
        ManagedStackRequiredException ex = await Assert.ThrowsExactlyAsync<ManagedStackRequiredException>(() =>
            proxy.HandleTransparentQuicDatagramAsync(
                new byte[1200],
                new IPEndPoint(IPAddress.Loopback, 9),
                new IPEndPoint(IPAddress.Loopback, 443),
                (_, _, _) => ValueTask.CompletedTask).AsTask());
        StringAssert.Contains(ex.Message, "Transparent UDP interception");
        StringAssert.Contains(ex.Message, "UseManagedQuicStack");
    }

    [TestMethod]
    public async Task ALongHeaderIsAnsweredForTheOriginalDestination()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            UseManagedQuicStack = true,
            AllowManagedStackUnderFips = true,
        };
        var client = new IPEndPoint(IPAddress.Loopback, 9);
        var origin = new IPEndPoint(IPAddress.Loopback, 443);
        var packet = new byte[1200];
        packet[0] = 0xC0;
        packet[4] = 2;
        packet[5] = 4;
        int sent = 0;
        IPEndPoint? to = null;
        await proxy.HandleTransparentQuicDatagramAsync(packet, client, origin, (datagram, destination, _) =>
        {
            sent = datagram.Length;
            to = destination;
            Assert.AreEqual(0x80, datagram.Span[0]);
            Assert.AreEqual(0, datagram.Span[1]);
            Assert.AreEqual(0, datagram.Span[4]);
            return ValueTask.CompletedTask;
        });
        Assert.IsTrue(sent > 0);
        Assert.AreEqual(client, to);
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class UpStreamEndPointFamilyTests
{
    [TestMethod]
    public async Task IPv4_Only_Bind_Works_For_IPv4_Localhost()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("v4-ok"));

        var proxy = testSuite.GetProxy();
        proxy.UpStreamEndPointIPv4 = new IPEndPoint(IPAddress.Loopback, 0);
        // Intentionally set a wrong-family legacy endpoint — must be ignored for IPv4 when IPv4-specific is set,
        // and must not break IPv4 when only used as legacy... here we only set IPv4-specific.

        var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpUrl);
        Assert.AreEqual("v4-ok", body);
    }

    [TestMethod]
    public async Task Legacy_IPv4_Bind_Does_Not_Block_When_Dual_Family_Configured()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("dual-ok"));

        var proxy = testSuite.GetProxy();
        // Old pattern that broke IPv6; with family-aware selection, IPv4 destination still works
        // and IPv6-specific bind is available for IPv6 destinations.
        proxy.UpStreamEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        proxy.UpStreamEndPointIPv4 = new IPEndPoint(IPAddress.Loopback, 0);
        proxy.UpStreamEndPointIPv6 = new IPEndPoint(IPAddress.IPv6Loopback, 0);

        var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpUrl);
        Assert.AreEqual("dual-ok", body);
    }

    [TestMethod]
    public async Task Per_Session_IPv4_Bind_Override_Works()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("session-ok"));

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (_, e) =>
        {
            e.HttpClient.UpStreamEndPointIPv4 = new IPEndPoint(IPAddress.Loopback, 0);
            await Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpUrl);
        Assert.AreEqual("session-ok", body);
    }
}

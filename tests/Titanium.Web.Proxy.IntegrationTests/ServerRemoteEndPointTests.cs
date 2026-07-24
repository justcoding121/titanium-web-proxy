using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ServerRemoteEndPointTests
{
    [TestMethod]
    public async Task BeforeResponse_Exposes_Connected_Server_Remote_EndPoint()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        IPEndPoint? capturedRemote = null;
        IPAddress? capturedIp = null;

        var proxy = testSuite.GetProxy();
        proxy.BeforeResponse += async (sender, e) =>
        {
            capturedRemote = e.ServerRemoteEndPoint;
            capturedIp = e.ServerIpAddress;
            await Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(capturedRemote);
        Assert.IsNotNull(capturedIp);
        Assert.AreEqual(IPAddress.Loopback, capturedIp);
        Assert.AreEqual(server.HttpListeningPort, capturedRemote.Port);
    }

    [TestMethod]
    public async Task BeforeResponse_With_Upstream_Proxy_Reports_Proxy_Hop()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var upstream = testSuite.GetProxy();
        var proxy = testSuite.GetProxy(upstream);

        IPEndPoint? capturedRemote = null;

        proxy.BeforeResponse += async (sender, e) =>
        {
            capturedRemote = e.ServerRemoteEndPoint;
            await Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(capturedRemote);
        // Physical peer is the upstream proxy hop, not the origin server.
        Assert.AreEqual(upstream.ProxyEndPoints[0].Port, capturedRemote.Port);
        Assert.AreNotEqual(server.HttpListeningPort, capturedRemote.Port);
    }

    [TestMethod]
    public async Task BeforeRequest_Has_No_Server_Remote_EndPoint_Yet()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        IPEndPoint? beforeRequestRemote = null;
        IPEndPoint? beforeResponseRemote = null;

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            beforeRequestRemote = e.ServerRemoteEndPoint;
            await Task.CompletedTask;
        };
        proxy.BeforeResponse += async (sender, e) =>
        {
            beforeResponseRemote = e.ServerRemoteEndPoint;
            await Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);
        await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.IsNull(beforeRequestRemote);
        Assert.IsNotNull(beforeResponseRemote);
    }
}

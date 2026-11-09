using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: HTTP/1.0 absolute-form GET, CancelRequest without a
///     staged body, and UserData surviving AfterResponse.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerHttp10AndCancelFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: HTTP/1.0 absolute-form GET is rejected or drops the Host authority.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Http10_AbsoluteFormGet_RoundTrips()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            await ctx.Response.WriteAsync("http10-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        var target = new Uri(server.ListeningHttpUrl);
        var req =
            $"GET http://{target.Host}:{target.Port}/h10 HTTP/1.0\r\n" +
            $"Host: {target.Host}:{target.Port}\r\n" +
            "\r\n";

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ep.Port);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(req));
        using var reader = new StreamReader(tcp.GetStream(), Encoding.ASCII);
        var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsNotNull(status);
        Assert.IsTrue(status!.Contains("200"), $"HTTP/1.0 absolute-form must succeed; got '{status}'");
        var rest = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.IsTrue(rest.Contains("http10-ok", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Bug class: CancelRequest without Ok/GenericResponse still dials the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_CancelRequest_DoesNotContactOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            contacted = true;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.CancelRequest = true;
            e.Ok("cancelled");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/cancel");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("cancelled", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: UserData set in BeforeRequest is cleared before AfterResponse.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_UserData_SurvivesAfterResponse()
    {
        object? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ud"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.UserData = "marker-ud";
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, e) =>
        {
            seen = e.UserData;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ud");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        for (var i = 0; i < 50 && seen is null; i++)
            await Task.Delay(50);
        Assert.AreEqual("marker-ud", seen);
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>ExplicitClientHandler</c> suite: <see cref="ExplicitProxyEndPoint.BeforeTunnelConnectResponse" />
///     (never covered by FatGap before), <see cref="TunnelConnectSessionEventArgs.IsHttpsConnect" />, and
///     timing capture on the tunnel handshake path.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitBeforeTunnelConnectResponseFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: BeforeTunnelConnectResponse never runs for decrypted HTTPS CONNECT, so
    ///     <see cref="TunnelConnectSessionEventArgs.IsHttpsConnect" /> is unusable from user code.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task MitmHttps_BeforeTunnelConnectResponse_IsHttpsConnectTrue()
    {
        bool? isHttps = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("tunnel-gap"));

        var proxy = suite.GetProxy();
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ep.BeforeTunnelConnectResponse += (_, e) =>
        {
            isHttps = e.IsHttpsConnect;
            signaled.TrySetResult();
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/t");
        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(isHttps == true);
    }

    /// <summary>
    ///     Bug class: opaque tunnels (DecryptSsl=false) skip BeforeTunnelConnectResponse so callers
    ///     cannot observe HTTPS semantics before relaying TLS bytes.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task OpaqueHttpsTunnel_BeforeTunnelConnectResponse_StillFires()
    {
        var responseEventCount = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("opaque"));

        var proxy = suite.GetProxy();
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.DecryptSsl = false;
            return Task.CompletedTask;
        };
        ep.BeforeTunnelConnectResponse += (_, e) =>
        {
            responseEventCount++;
            _ = e.IsHttpsConnect;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/o");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, responseEventCount);
    }

    /// <summary>
    ///     Bug class: <see cref="ProxyServer.EnableRequestTimingCapture" /> does not populate
    ///     <see cref="TunnelConnectSessionEventArgs.ConnectTiming" /> in BeforeTunnelConnectResponse.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task RequestTimingCapture_BeforeTunnelConnectResponse_HasConnectTiming()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("timing"));

        var proxy = suite.GetProxy();
        proxy.EnableRequestTimingCapture = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        var timingSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ep.BeforeTunnelConnectResponse += (_, e) =>
        {
            timingSeen.TrySetResult(e.ConnectTiming != null);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/tim");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(await timingSeen.Task.WaitAsync(TimeSpan.FromSeconds(15)));
    }
}

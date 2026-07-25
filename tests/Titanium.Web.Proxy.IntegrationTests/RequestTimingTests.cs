using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Covers <see cref="ProxyServer.EnableRequestTimingCapture" />: disabled by default (no timing object
///     ever allocated), and - once enabled - the structured milestones exposed on
///     <see cref="SessionEventArgsBase.Timing" />, <see cref="SessionEventArgsBase.UpstreamConnectionTiming" />,
///     and <see cref="TunnelConnectSessionEventArgs.ClientTlsTiming" />.
/// </summary>
[DoNotParallelize]
[TestClass]
public class RequestTimingTests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Request_Timing_Is_Null_By_Default()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        Assert.IsFalse(proxy.EnableRequestTimingCapture, "timing capture should be disabled by default");

        HttpRequestTiming? capturedTiming = null;
        var seenNonNullUpstreamTiming = false;
        proxy.AfterResponse += (_, args) =>
        {
            capturedTiming = args.Timing;
            if (args.UpstreamConnectionTiming != null) seenNonNullUpstreamTiming = true;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpUrl);
        Assert.AreEqual("ok", body);

        Assert.IsNull(capturedTiming, "Timing must stay null while capture is disabled");
        Assert.IsFalse(seenNonNullUpstreamTiming, "UpstreamConnectionTiming must stay null while capture is disabled");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Request_Timing_Captures_Http11_Milestones_In_Order()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableRequestTimingCapture = true;

        HttpRequestTiming? capturedTiming = null;
        UpstreamConnectionTiming? capturedConnectionTiming = null;
        proxy.AfterResponse += (_, args) =>
        {
            capturedTiming = args.Timing;
            capturedConnectionTiming = args.UpstreamConnectionTiming;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var body = await client.GetStringAsync(server.ListeningHttpUrl);
        Assert.AreEqual("ok", body);

        // AfterResponse fires from within the proxy's own per-request finally block, immediately after the
        // response is written to the client stream - there is no guarantee it has already run by the time
        // the client-side await above returns, so poll briefly rather than asserting on it synchronously.
        for (var i = 0; i < 50 && capturedTiming == null; i++) await Task.Delay(20);

        Assert.IsNotNull(capturedTiming, "Timing should be populated once capture is enabled");
        var timing = capturedTiming!;

        Assert.IsNotNull(timing.RequestHeadersReceivedAt);
        Assert.IsNotNull(timing.ConnectionReadyAt);
        Assert.IsNotNull(timing.RequestSentAt);
        Assert.IsNotNull(timing.ResponseHeadersReceivedAt);

        Assert.IsTrue(timing.RequestHeadersReceivedAt >= timing.SessionCreatedAt);
        Assert.IsTrue(timing.ConnectionReadyAt >= timing.RequestHeadersReceivedAt);
        Assert.IsTrue(timing.RequestSentAt >= timing.ConnectionReadyAt);
        Assert.IsTrue(timing.ResponseHeadersReceivedAt >= timing.RequestSentAt);

        Assert.AreEqual(1, timing.AttemptCount);
        Assert.IsFalse(timing.UpstreamConnectionReused, "the very first request must establish a fresh connection");
        Assert.IsNotNull(timing.UpstreamConnectionId);

        // OnAfterResponse marks IsComplete only after the AfterResponse handler above returns (see its
        // remarks), so poll briefly rather than asserting it synchronously from inside the handler itself.
        for (var i = 0; i < 50 && !timing.IsComplete; i++) await Task.Delay(20);

        Assert.IsTrue(timing.IsComplete, "session should be marked complete shortly after AfterResponse fires");
        Assert.IsTrue(timing.TotalDuration >= TimeSpan.Zero);
        Assert.IsTrue(timing.TotalDuration >= (timing.ResponseHeadersReceivedAt!.Value - timing.SessionCreatedAt));

        Assert.IsNotNull(capturedConnectionTiming, "the upstream connection's own establishment timing should be reachable from the session");
        Assert.IsNotNull(capturedConnectionTiming!.TcpConnectedAt);
        Assert.IsNull(capturedConnectionTiming.TlsHandshakeCompletedAt, "plain HTTP has no TLS handshake");
        Assert.IsTrue(capturedConnectionTiming.TotalDuration >= TimeSpan.Zero);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Request_Timing_Marks_Upstream_Connection_Reused_On_Second_Request()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableRequestTimingCapture = true;

        var requestCount = 0;
        HttpRequestTiming? firstTiming = null;
        HttpRequestTiming? secondTiming = null;
        proxy.AfterResponse += (_, args) =>
        {
            requestCount++;
            if (requestCount == 1) firstTiming = args.Timing;
            else secondTiming = args.Timing;

            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        Assert.AreEqual("ok", await client.GetStringAsync(server.ListeningHttpUrl));

        // AfterResponse fires from within the proxy's own per-request finally block, immediately after the
        // response is written to the client stream - there is no guarantee it has already run by the time
        // the client-side await above returns, so poll briefly rather than asserting on it synchronously.
        for (var i = 0; i < 50 && requestCount < 1; i++) await Task.Delay(20);

        Assert.AreEqual("ok", await client.GetStringAsync(server.ListeningHttpUrl));

        for (var i = 0; i < 50 && requestCount < 2; i++) await Task.Delay(20);

        Assert.IsNotNull(firstTiming);
        Assert.IsNotNull(secondTiming);

        Assert.IsFalse(firstTiming!.UpstreamConnectionReused);
        Assert.IsTrue(secondTiming!.UpstreamConnectionReused,
            "the second request on the same client connection should reuse the pooled upstream connection");
        Assert.AreEqual(firstTiming.UpstreamConnectionId, secondTiming.UpstreamConnectionId);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Upstream_Connection_Timing_Includes_Tls_Handshake_For_Https_Origin()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableRequestTimingCapture = true;

        UpstreamConnectionTiming? capturedConnectionTiming = null;
        proxy.AfterResponse += (_, args) =>
        {
            capturedConnectionTiming = args.UpstreamConnectionTiming;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl), new StringContent("hi"));
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());

        // AfterResponse fires from within the proxy's own per-request finally block, immediately after the
        // response is written to the client stream - there is no guarantee it has already run by the time
        // the client-side await above returns, so poll briefly rather than asserting on it synchronously.
        for (var i = 0; i < 50 && capturedConnectionTiming == null; i++) await Task.Delay(20);

        Assert.IsNotNull(capturedConnectionTiming);
        Assert.IsNotNull(capturedConnectionTiming!.TlsHandshakeCompletedAt);
        Assert.IsTrue(capturedConnectionTiming.TlsHandshakeDuration >= TimeSpan.Zero);
        Assert.IsTrue(capturedConnectionTiming.TotalDuration >= capturedConnectionTiming.TlsHandshakeDuration);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Client_Tls_Timing_Is_Captured_For_Decrypted_Tunnel()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableRequestTimingCapture = true;

        TunnelConnectSessionEventArgs? capturedTunnelArgs = null;
        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, args) =>
        {
            capturedTunnelArgs = args;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);
        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl), new StringContent("hi"));
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());

        Assert.IsNotNull(capturedTunnelArgs, "BeforeTunnelConnectRequest should have fired for the HTTPS CONNECT");

        // The client TLS handshake happens after BeforeTunnelConnectRequest but strictly before the HTTP
        // request that already completed above is ever answered, so ClientTlsTiming is guaranteed to be
        // fully populated by this point - no polling needed, unlike HttpRequestTiming.IsComplete above.
        var clientTlsTiming = capturedTunnelArgs!.ClientTlsTiming;
        Assert.IsNotNull(clientTlsTiming, "ClientTlsTiming should be populated for a decrypted HTTPS tunnel");
        Assert.IsNotNull(clientTlsTiming!.CompletedAt);
        Assert.IsTrue(clientTlsTiming.HandshakeDuration >= TimeSpan.Zero);
    }
}

#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     What a host observes in <c>BeforeResponse</c> / <c>AfterResponse</c> for each client/origin protocol
///     pairing. A host that labels an exchange "client → origin" (Inspector's Protocol column) reads
///     <c>Request.HttpVersion</c> and <c>Response.HttpVersion</c>, so those must describe the two real legs
///     even while a bridge temporarily rewrites them for wire emission. The native h1↔h1 and h2↔h2 rows are
///     the unchanged baseline the bridge rows must stay consistent with.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ObservedProtocolVersionTests
{
    private static readonly Version H1 = new(1, 1);
    private static readonly Version H2 = new(2, 0);
    private static readonly Version H3 = new(3, 0);

    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    private sealed class Observed
    {
        public Version? BeforeRequest;
        public Version? BeforeResponseRequest;
        public Version? BeforeResponseResponse;
        public Version? AfterResponseRequest;
        public Version? AfterResponseResponse;

        public override string ToString() =>
            $"BeforeRequest={BeforeRequest} | BeforeResponse={BeforeResponseRequest}->{BeforeResponseResponse}" +
            $" | AfterResponse={AfterResponseRequest}->{AfterResponseResponse}";
    }

    private static Observed Observe(ProxyServer proxy)
    {
        var observed = new Observed();
        proxy.BeforeRequest += (_, e) =>
        {
            observed.BeforeRequest = e.HttpClient.ClientHttpVersion;
            return Task.CompletedTask;
        };
        proxy.BeforeResponse += (_, e) =>
        {
            observed.BeforeResponseRequest = e.HttpClient.ClientHttpVersion;
            observed.BeforeResponseResponse = e.HttpClient.OriginHttpVersion;
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, e) =>
        {
            observed.AfterResponseRequest = e.HttpClient.ClientHttpVersion;
            observed.AfterResponseResponse = e.HttpClient.OriginHttpVersion;
            return Task.CompletedTask;
        };
        return observed;
    }

    private static async Task AssertLegs(Observed observed, Version client, Version origin)
    {
        for (var i = 0; i < 100 && observed.AfterResponseResponse == null; i++)
            await Task.Delay(50);

        var expected = $"BeforeRequest={client} | BeforeResponse={client}->{origin} | AfterResponse={client}->{origin}";
        Assert.AreEqual(expected, observed.ToString());
    }

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static void ForceUpstream(ProxyServer proxy, UpstreamHttpProtocol protocol)
    {
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = protocol;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
    }

    private static async Task GetOk(HttpClient client, string url)
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        using var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("legs-body", await response.Content.ReadAsStringAsync());
    }

    // ---- native baselines (no bridge): must keep reporting the same pair ----

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Native_H1_To_H1_Reports_H1_H1()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("legs-body"));
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var observed = Observe(proxy);

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        await GetOk(client, $"https://localhost:{server.HttpsListeningPort}/");

        await AssertLegs(observed, H1, H1);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Native_H2_To_H2_Reports_H2_H2()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("legs-body"));
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var observed = Observe(proxy);

        using var client = TestHelper.GetHttp2Client(proxy);
        await GetOk(client, $"https://localhost:{server.HttpsListeningPort}/");

        await AssertLegs(observed, H2, H2);
    }

    // ---- TCP bridges ----

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Bridge_H2Client_To_H1Origin_Reports_H2_H1()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("legs-body"));
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var observed = Observe(proxy);
        ForceUpstream(proxy, UpstreamHttpProtocol.Http11);

        using var client = TestHelper.GetHttp2Client(proxy);
        await GetOk(client, $"https://localhost:{server.HttpsListeningPort}/");

        await AssertLegs(observed, H2, H1);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Bridge_H1Client_To_H2Origin_Reports_H1_H2()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("legs-body"));
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var observed = Observe(proxy);
        ForceUpstream(proxy, UpstreamHttpProtocol.Http2);

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        await GetOk(client, $"https://localhost:{server.HttpsListeningPort}/");

        await AssertLegs(observed, H1, H2);
    }

    // ---- HTTP/3 origin (cold = forced at CONNECT, warm = capability-cache hit) ----

    private static async Task RunAgainstH3Origin(bool http2Client, bool warm)
    {
        RequireQuic();
        await using var origin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleRequest(_ => Task.FromResult(new QuicHttp3Response(200, "legs-body",
            Encoding.UTF8.GetBytes("legs-body"))));

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        var observed = Observe(proxy);

        if (warm)
        {
            proxy.Http3OriginCapabilityCache.Set($"localhost:{origin.Port}", int.MinValue,
                TimeSpan.FromMinutes(5), targetName: null);
            proxy.Http3WarmOrigins.Mark("localhost", origin.Port);
        }
        else
        {
            ForceUpstream(proxy, UpstreamHttpProtocol.Http3);
        }

        using var client = http2Client
            ? TestHelper.GetHttp2Client(proxy)
            : TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        await GetOk(client, $"https://localhost:{origin.Port}/");

        await AssertLegs(observed, http2Client ? H2 : H1, H3);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public Task Bridge_H2Client_To_H3Origin_Reports_H2_H3(bool warm) => RunAgainstH3Origin(http2Client: true, warm);

    [TestMethod]
    [Timeout(60 * 1000)]
    [DataRow(false)]
    [DataRow(true)]
    public Task Bridge_H1Client_To_H3Origin_Reports_H1_H3(bool warm) => RunAgainstH3Origin(http2Client: false, warm);
}

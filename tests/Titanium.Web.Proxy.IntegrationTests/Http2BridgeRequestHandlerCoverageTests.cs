using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining high-unc bridge / RequestHandler paths: Via loop 508 on H1↔H2 bridges,
///     H1→H2 GenericResponse cancel, and H2→H1 request-body relay.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2BridgeRequestHandlerCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static Func<Http2RawFrame.Connection, Task> NoOpOriginHandler() => async conn =>
    {
        await conn.SendInitialSettingsAsync();
        try { while (true) await conn.ReadFrameAsync(); }
        catch { /* torn down */ }
    };

    private static (ProxyServer Proxy, TestSuite Suite) CreateH1ToH2BridgeProxy()
    {
        var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        return (proxy, suite);
    }

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Endpoint) CreateH2ToH1BridgeProxy(
        TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, endpoint);
    }

    /// <summary>
    ///     Bug class: H2→H1 bridge ignores a Via header that already contains the proxy pseudonym
    ///     and re-forwards forever instead of answering 508 Loop Detected.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_ViaLoop_Returns508()
    {
        var originContacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            originContacted = true;
            return ctx.Response.WriteAsync("should-not-reach");
        });

        var (proxy, endpoint) = CreateH2ToH1BridgeProxy(suite);
        proxy.ViaHeaderPseudonym = "h2h1-loop-sentinel";

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/via-loop")],
            [("via", "2.0 h2h1-loop-sentinel")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("508", headers.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(originContacted, "Via loop must not contact the H1 origin.");
    }

    /// <summary>
    ///     Bug class: H1→H2 bridge misses Via loop detection under interception and contacts the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ViaLoop_Returns508()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            originContacted = true;
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        proxy.ViaHeaderPseudonym = "h1h2-loop-sentinel";

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Headers.TryAddWithoutValidation("Via", "1.1 h1h2-loop-sentinel");

        var response = await client.SendAsync(request);
        Assert.AreEqual((HttpStatusCode)508, response.StatusCode);
        Assert.IsFalse(originContacted, "Via loop must not contact the H2 origin.");
    }

    /// <summary>
    ///     Bug class: CancelRequest + GenericResponse on the H1→H2 bridge still opens the H2 origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_GenericResponse_DoesNotContactOrigin()
    {
        var originContacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            originContacted = true;
            try { while (true) await conn.ReadFrameAsync(); }
            catch { /* torn down */ }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("synthetic-h1h2", (HttpStatusCode)419);
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        var response = await client.GetAsync(origin.Url);
        Assert.AreEqual((HttpStatusCode)419, response.StatusCode);
        Assert.IsFalse(originContacted, "Synthetic GenericResponse must not contact the H2 origin.");
    }

    /// <summary>
    ///     Bug class: H2→H1 POST body is dropped (request-body channel never drained / Content-Length 0).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_PostBody_RelaysToOrigin()
    {
        byte[]? seenBody = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            seenBody = ms.ToArray();
            ctx.Response.StatusCode = 204;
            await ctx.Response.CompleteAsync();
        });

        var (_, endpoint) = CreateH2ToH1BridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var payload = Encoding.ASCII.GetBytes("bridge-post-body");
        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"), (":authority", "localhost"), (":path", "/post")],
            [("content-length", payload.Length.ToString()), ("content-type", "text/plain")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, payload);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("204", headers.Single(h => h.Name == ":status").Value);
        CollectionAssert.AreEqual(payload, seenBody,
            "H2→H1 bridge must relay the full POST body to the H1 origin.");
    }
}

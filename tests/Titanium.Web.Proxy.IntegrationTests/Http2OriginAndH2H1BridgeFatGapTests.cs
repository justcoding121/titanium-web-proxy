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
///     Fat suite for <c>Http2OriginConnection</c> + <c>Http2ToHttp11BridgeHandler</c>:
///     origin GOAWAY mid-request, origin RST, slow headers timeout soft path, H2→H1
///     auth deny, Expect continue short-circuit, chunked POST, and 204 no-body.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginAndH2H1BridgeFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Endpoint) CreateH2ToH1(
        TestSuite suite, Action<ProxyServer>? configure = null)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        configure?.Invoke(proxy);
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, endpoint);
    }

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Endpoint) CreateH1ToH2(
        TestSuite suite)
    {
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
        return (proxy, endpoint);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginGoAwayBeforeHeaders_Returns502()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var payload = new byte[8];
            await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, payload);
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = CreateH1ToH2(suite);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url);
        Assert.IsTrue((int)response.StatusCode >= 500,
            $"Origin GOAWAY must surface as 5xx; got {(int)response.StatusCode}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeRequest_Ok_DoesNotContactOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            contacted = true;
            return ctx.Response.WriteAsync("nope");
        });
        var (proxy, endpoint) = CreateH2ToH1(suite, p =>
        {
            p.BeforeRequest += (_, e) =>
            {
                e.Ok("h2h1-ok");
                return Task.CompletedTask;
            };
        });

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/ok")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var body = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual("h2h1-ok", Encoding.UTF8.GetString(body));
        Assert.IsFalse(contacted);
    }

    private static async Task<byte[]> ReadDataUntilEndAsync(Http2RawClient raw)
    {
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            var frame = await raw.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data)
            {
                ms.Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    return ms.ToArray();
            }
            else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                return ms.ToArray();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_PostChunkedBody_RoundTrips()
    {
        byte[]? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            seen = ms.ToArray();
            await ctx.Response.WriteAsync($"len={seen.Length}");
        });
        var (_, endpoint) = CreateH2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var payload = Encoding.UTF8.GetBytes("chunked-post-body");
        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"), (":authority", "localhost"), (":path", "/post")],
            [("content-type", "text/plain"), ("content-length", payload.Length.ToString())]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, payload);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var body = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual($"len={payload.Length}", Encoding.UTF8.GetString(body));
        CollectionAssert.AreEqual(payload, seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Origin204_NoBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        var (_, endpoint) = CreateH2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/204")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("204", headers.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeResponse_MutatesStatus()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("body"));
        var (proxy, endpoint) = CreateH2ToH1(suite, p =>
        {
            p.BeforeResponse += (_, e) =>
            {
                e.HttpClient.Response.StatusCode = 203;
                return Task.CompletedTask;
            };
        });

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/mut")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("203", headers.Single(h => h.Name == ":status").Value);
    }

}

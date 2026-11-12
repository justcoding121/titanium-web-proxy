using System;
using System.IO;
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
///     Fat H1↔H2 bridge / RequestHandler / ResponseHandler suite: method matrix,
///     BeforeRequest Ok short-circuit, large multi-DATA body, query-string GET,
///     and BeforeResponse header mutation.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2BridgeMethodsAndBodyFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H1ToH2(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, ep);
    }

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H2ToH1(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, ep);
    }

    private static async Task<byte[]> ReadDataUntilEndAsync(Http2RawClient raw)
    {
        using var ms = new MemoryStream();
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
    public async Task H1ToH2_PutAndDelete_RoundTrip()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        string? seenMethod = null;
        byte[]? seenBody = null;
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                seenMethod = headers.First(h => h.Name == ":method").Value;
                seenBody = body;
                var resp = conn.EncodeHeaders([(":status", "200")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(
                    Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.UTF8.GetBytes($"m={seenMethod}"));
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);

        using (var content = new StringContent("put-payload", Encoding.UTF8, "text/plain"))
        using (var put = await client.PutAsync(origin.Url + "/put", content))
        {
            Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
            Assert.AreEqual("PUT", seenMethod);
            Assert.AreEqual("put-payload", Encoding.UTF8.GetString(seenBody ?? []));
            Assert.AreEqual("m=PUT", await put.Content.ReadAsStringAsync());
        }

        using (var del = await client.DeleteAsync(origin.Url + "/del"))
        {
            Assert.AreEqual(HttpStatusCode.OK, del.StatusCode);
            Assert.AreEqual("DELETE", seenMethod);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_Ok_SkipsOrigin()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
            catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-h1h2");
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var response = await client.GetAsync(origin.Url + "/ok");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("synthetic-h1h2", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_GetWithQuery_RoundTrips()
    {
        string? seenQuery = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seenQuery = ctx.Request.QueryString.Value;
            return ctx.Response.WriteAsync("q-ok");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"),
             (":path", "/q?x=1&y=two")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var body = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual("q-ok", Encoding.UTF8.GetString(body));
        Assert.AreEqual("?x=1&y=two", seenQuery);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_PutBody_RoundTrips()
    {
        byte[]? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            seen = ms.ToArray();
            await ctx.Response.WriteAsync($"n={seen.Length}");
        });
        var (_, ep) = H2ToH1(suite);

        var payload = Encoding.UTF8.GetBytes(new string('P', 2048));
        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "PUT"), (":scheme", "https"), (":authority", "localhost"), (":path", "/put")],
            [("content-type", "text/plain"), ("content-length", payload.Length.ToString())]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        // Split DATA to exercise multi-frame body relay.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, (Http2FrameFlag)0, payload.AsSpan(0, 1024).ToArray());
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, payload.AsSpan(1024).ToArray());

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var body = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        Assert.AreEqual($"n={payload.Length}", Encoding.UTF8.GetString(body));
        CollectionAssert.AreEqual(payload, seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeResponse_AddsHeader()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("hdr"));
        var (proxy, ep) = H2ToH1(suite);
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("x-bridge", "yes");
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/h")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(headers.Any(h => h.Name == "x-bridge" && h.Value == "yes"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Options_RoundTrips()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            Assert.AreEqual("OPTIONS", headers.First(h => h.Name == ":method").Value);
            var resp = conn.EncodeHeaders(
                [(":status", "204")],
                [("allow", "GET,POST,OPTIONS")]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Options, origin.Url + "/opt");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_LargeResponse_MultiDataFrames()
    {
        var payload = Encoding.UTF8.GetBytes(new string('R', 8192));
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            ctx.Response.ContentLength = payload.Length;
            await ctx.Response.Body.WriteAsync(payload);
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/big")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        var body = endStream ? Array.Empty<byte>() : await ReadDataUntilEndAsync(raw);
        CollectionAssert.AreEqual(payload, body);
    }
}

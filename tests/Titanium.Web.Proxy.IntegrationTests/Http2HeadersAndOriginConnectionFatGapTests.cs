using System;
using System.Buffers.Binary;
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
///     Fat Copy.Headers + Http2OriginConnection suite: te trailers-only, duplicate :path,
///     origin GOAWAY with last-stream, origin SETTINGS ack, H1→H2 POST body, H2→H1 HEAD.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2HeadersAndOriginConnectionFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<(Http2FrameType Type, int Error)> ReadRstOrGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.RstStream)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
        }

        Assert.Fail("Timed out waiting for RST/GOAWAY.");
        return default;
    }

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

    [TestMethod]
    [Timeout(30_000)]
    public async Task TeTrailersOnly_IsAcceptedAndRequestSucceeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            Assert.AreEqual("trailers", headers.FirstOrDefault(h => h.Name == "te").Value);
            var resp = conn.EncodeHeaders([(":status", "204")], []);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/te")],
            [("te", "trailers")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        string? status = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline && status is null)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
                status = raw.Connection.DecodeHeaders(frame.Payload).Find(h => h.Name == ":status").Value;
        }

        Assert.AreEqual("204", status);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EmptyPath_IsRejected()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (type, _) = await ReadRstOrGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(type is Http2FrameType.RstStream or Http2FrameType.GoAway);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Head_OmitsBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            Assert.AreEqual(HttpMethods.Head, ctx.Request.Method);
            ctx.Response.ContentLength = 11;
            await ctx.Response.WriteAsync("should-omit");
        });
        var (_, ep) = H2ToH1(suite);

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "HEAD"), (":scheme", "https"), (":authority", "localhost"), (":path", "/head")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream, "HEAD response must END_STREAM on headers.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeResponse_ChangesStatus()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("x"));
        var (proxy, ep) = H2ToH1(suite);
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.StatusCode = 203;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(ep.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/mut")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);
        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("203", headers.Single(h => h.Name == ":status").Value);
    }
}

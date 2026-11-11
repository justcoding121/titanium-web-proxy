using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     HTTP/2 header-copy and H1↔H2 bridge semantics that callers can observe.
///     Bug class: connection-specific headers leaked, illegal pseudo-headers forwarded, cookie join wrong.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2HeaderSemanticsBridgeTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_StripsConnectionSpecificHeaders_BeforeOrigin()
    {
        // Bug class: Connection/Keep-Alive/Upgrade/Proxy-Connection forwarded onto an H2 origin.
        List<(string Name, string Value)>? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            seen = headers;
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "ok"u8.ToArray());
        });

        using var suite = new TestSuite();
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

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;
        request.Headers.TryAddWithoutValidation("Connection", "keep-alive, Upgrade");
        request.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
        request.Headers.TryAddWithoutValidation("Proxy-Connection", "keep-alive");
        request.Headers.TryAddWithoutValidation("Upgrade", "h2c");
        request.Headers.TryAddWithoutValidation("TE", "trailers, deflate");

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(seen);
        Assert.IsFalse(seen!.Any(h => h.Name == "connection"), "connection leaked");
        Assert.IsFalse(seen.Any(h => h.Name == "keep-alive"), "keep-alive leaked");
        Assert.IsFalse(seen.Any(h => h.Name == "upgrade"), "upgrade leaked");
        Assert.IsFalse(seen.Any(h => h.Name == "proxy-connection"), "proxy-connection leaked");
        var te = seen.FirstOrDefault(h => h.Name == "te").Value;
        if (!string.IsNullOrEmpty(te))
        {
            Assert.AreEqual("trailers", te);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_UppercaseHeaderName_IsRejected()
    {
        // Bug class: uppercase H2 header names are accepted and forwarded (RFC 9113 §8.2.1).
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var hitOrigin = false;
        server.HandleRequest(async context =>
        {
            hitOrigin = true;
            await context.Response.WriteAsync("should-not-run");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", serverUri.Authority), (":path", "/")],
            [("X-Bad-Case", "1")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var sawRstOrGoAway = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !sawRstOrGoAway)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
            {
                sawRstOrGoAway = true;
            }
        }

        Assert.IsTrue(sawRstOrGoAway, "proxy must RST/GOAWAY on uppercase header names");
        Assert.IsFalse(hitOrigin, "illegal headers must not reach the origin");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_DuplicatePseudoHeader_IsRejected()
    {
        // Bug class: duplicate :path (or other pseudo) is forwarded.
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var hitOrigin = false;
        server.HandleRequest(_ =>
        {
            hitOrigin = true;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        // EncodeHeaders may dedupe; craft a block with two :path by concatenating two encoded fields.
        var first = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", serverUri.Authority), (":path", "/a")],
            Array.Empty<(string, string)>());
        var secondPath = raw.Connection.EncodeHeaders(
            [(":path", "/b")],
            Array.Empty<(string, string)>());
        var combined = first.Concat(secondPath).ToArray();
        await raw.Connection.WriteHeaderBlockAsync(1, combined, endStream: true);

        var sawRstOrGoAway = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !sawRstOrGoAway)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
            {
                sawRstOrGoAway = true;
            }
        }

        Assert.IsTrue(sawRstOrGoAway, "duplicate pseudo-header must be rejected");
        Assert.IsFalse(hitOrigin);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_JoinsCookieHeaders_WithSemicolon()
    {
        // Bug class: multiple cookie fields are dropped or joined incorrectly going to H1.
        string? cookie = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async context =>
        {
            cookie = context.Request.Headers.Cookie.ToString();
            await context.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", serverUri.Authority), (":path", "/")],
            [("cookie", "a=1"), ("cookie", "b=2")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var status = await ReadStatusAsync(raw, 1);
        Assert.AreEqual("200", status);
        Assert.IsNotNull(cookie);
        StringAssert.Contains(cookie, "a=1");
        StringAssert.Contains(cookie, "b=2");
    }

    private static async Task<string?> ReadStatusAsync(Http2RawClient raw, int streamId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.StreamId != streamId)
            {
                continue;
            }

            if (frame.Type == Http2FrameType.Headers)
            {
                return raw.Connection.DecodeHeaders(frame.Payload).FirstOrDefault(h => h.Name == ":status").Value;
            }

            if (frame.Type == Http2FrameType.RstStream)
            {
                return null;
            }
        }

        return null;
    }
}

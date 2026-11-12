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
///     Remaining high-unc lines in <c>Http2Helper.Copy</c> (stream-id-0 HEADERS/PRIORITY)
///     and H1↔H2 bridge delivery (HTTP/1.0 unknown-length body buffering, H2→H1 HEAD).
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyBridgeHandlerCoverageTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<Http2RawFrame.Frame> ReadSignificantFrameAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        Http2RawFrame.Frame frame;
        do
        {
            frame = await raw.Connection.ReadFrameAsync().WaitAsync(timeout);
        } while (frame.Type is Http2FrameType.Settings or Http2FrameType.WindowUpdate);

        return frame;
    }

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
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, endpoint);
    }

    // -------------------------------------------------------------------------
    // Http2Helper.Copy — HEADERS / PRIORITY on stream 0
    // -------------------------------------------------------------------------

    /// <summary>
    ///     RFC 7540 §6.2: HEADERS must not use stream id 0. Bug class: accepted / hung instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Headers_OnStreamZero_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 0,
            Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream, block);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "HEADERS on stream 0 must produce GOAWAY.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     RFC 7540 §6.3: PRIORITY must not use stream id 0. Bug class: accepted instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Priority_OnStreamZero_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // PRIORITY payload: 4-byte dependent stream id + 1-byte weight.
        var priority = new byte[5];
        await raw.Connection.WriteFrameAsync(Http2FrameType.Priority, 0, (Http2FrameFlag)0, priority);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "PRIORITY on stream 0 must produce GOAWAY.");
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
    }

    // -------------------------------------------------------------------------
    // Http11ToHttp2Bridge — HTTP/1.0 unknown-length body buffering
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: an HTTP/1.0 client through the H1→H2 bridge hangs or gets a truncated body when
    ///     the H2 origin omits Content-Length (HTTP/1.0 has no chunked framing — must buffer).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Http10Client_UnknownLengthOriginBody_IsFullyBuffered()
    {
        var expected = Encoding.ASCII.GetBytes("http10-unknown-length-body-bytes");
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            // No content-length — END_STREAM on DATA delimits the body.
            var block = conn.EncodeHeaders([(":status", "200")],
                [("content-type", "text/plain")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, expected);
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version10,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        CollectionAssert.AreEqual(expected, body,
            "HTTP/1.0 client must receive the fully buffered unknown-length H2 origin body.");
    }

    // -------------------------------------------------------------------------
    // Http2ToHttp11Bridge — HEAD method
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: HEAD through H2→H1 is mishandled (body expected / method rewritten to GET).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Head_RelaysMethodAndOmitsBody()
    {
        string? seenMethod = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seenMethod = ctx.Request.Method;
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength = 42;
            return ctx.Response.WriteAsync("should-not-appear-for-head");
        });

        var (_, endpoint) = CreateH2ToH1BridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "HEAD"), (":scheme", "https"), (":authority", "localhost"), (":path", "/head")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Single(h => h.Name == ":status").Value);
        Assert.AreEqual("HEAD", seenMethod, "Origin must see HEAD, not GET.");
        Assert.IsTrue(endStream,
            "HEAD response must END_STREAM on HEADERS (no DATA body toward the H2 client).");
    }

    // -------------------------------------------------------------------------
    // Http2ToHttp11Bridge — BeforeRequest CancelRequest with GenericResponse
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Bug class: CancelRequest + GenericResponse on the H2→H1 bridge still contacts the origin
    ///     or fails to emit the synthetic status to the H2 client.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_BeforeRequest_GenericResponse_DoesNotContactOrigin()
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
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("synthetic-bridge", (HttpStatusCode)418);
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/synth")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("418", headers.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(originContacted, "Synthetic GenericResponse must not contact the H1 origin.");
    }
}

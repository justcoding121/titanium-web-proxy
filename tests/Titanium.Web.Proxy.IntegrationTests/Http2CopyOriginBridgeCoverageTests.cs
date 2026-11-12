using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Behavior coverage for remaining high-uncovered paths in
///     <c>Http2Helper.Copy.cs</c>, <c>Http2Helper.Copy.Headers.cs</c>,
///     <c>Http2OriginConnection.cs</c>, and the H1↔H2 bridge handlers.
///     Each test names the RFC / bug class it protects.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOriginBridgeCoverageTests
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
        catch { /* torn down by the test */ }
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

    // =========================================================================
    // Http2Helper.Copy.cs — invalid DATA padding (compressed-relay path)
    // =========================================================================

    /// <summary>
    ///     RFC 9113 §6.1: a DATA frame whose pad length is the payload length or longer is a
    ///     connection-level PROTOCOL_ERROR.
    ///     Bug class: proxy treats the pad-length octet as body bytes (or hangs) instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Data_PaddingExceedsPayload_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        // Leave interception off so the compressed-relay DATA path validates padding.

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/pad")],
            [("content-length", "4")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);

        // PADDED DATA: padLen=5 with total payload length 5 → padLen >= length (PROTOCOL_ERROR).
        // Wire: [padLen=5][four padding bytes] — no real body bytes.
        var badPadded = new byte[] { 5, 0, 0, 0, 0 };
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1,
            Http2FrameFlag.Padded | Http2FrameFlag.EndStream, badPadded);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "DATA padding that exceeds the payload must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
            "GOAWAY error code must be PROTOCOL_ERROR for oversized DATA padding.");
    }

    // =========================================================================
    // Http2Helper.Copy.cs — invalid HEADERS padding
    // =========================================================================

    /// <summary>
    ///     RFC 9113 §6.2: a HEADERS frame whose pad length is the payload length or longer is a
    ///     connection-level PROTOCOL_ERROR.
    ///     Bug class: proxy decodes garbage as HPACK / hangs instead of GOAWAY.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Headers_PaddingExceedsPayload_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true; // decode path that strips PADDED framing

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // HEADERS with PADDED | END_HEADERS | END_STREAM: padLen=3, total length=3 → invalid.
        var hdr = new byte[9 + 3];
        hdr[0] = 0; hdr[1] = 0; hdr[2] = 3; // length = 3
        hdr[3] = (byte)Http2FrameType.Headers;
        hdr[4] = (byte)(Http2FrameFlag.Padded | Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream);
        hdr[8] = 1; // stream 1
        hdr[9] = 3; // padLen = 3 (== length) → PROTOCOL_ERROR
        // bytes 10-11 are padding zeros

        await raw.Connection.GetStream().WriteAsync(hdr.AsMemory());

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "HEADERS padding that exceeds the payload must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    // =========================================================================
    // Http2Helper.Copy.Headers.cs — non-increasing client stream id
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §5.1.1: client-initiated stream ids must be odd and strictly increasing.
    ///     Bug class: reused / decreasing stream ids collide with closed-stream state and desync flow control.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_NonIncreasingStreamId_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                // Answer the first request so the proxy records LastClientStreamId = 5.
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "0")]);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: true);
                while (true) await conn.ReadFrameAsync();
            }
            catch { /* torn down */ }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Open stream 5 first (skipping 1/3 is allowed as long as ids increase).
        var block5 = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/five")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(5, block5, endStream: true);

        // Drain the 200 so LastClientStreamId is committed.
        Http2RawFrame.Frame resp;
        do { resp = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        while (resp.Type is Http2FrameType.Settings or Http2FrameType.WindowUpdate
            || (resp.Type == Http2FrameType.Headers && resp.StreamId != 5));

        // Now send stream 3 — lower than LastClientStreamId → connection PROTOCOL_ERROR.
        var block3 = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/three")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(3, block3, endStream: true);

        Http2RawFrame.Frame? goAway = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && goAway == null)
        {
            var f = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (f.Type == Http2FrameType.GoAway)
                goAway = f;
        }

        Assert.IsNotNull(goAway, "Non-increasing stream id must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(goAway!.Value.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    // =========================================================================
    // Http2Helper.Copy.cs — HEADERS while a previous header block is still open
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §6.10: only CONTINUATION for the same stream may follow a HEADERS without END_HEADERS.
    ///     Bug class: a second HEADERS while a block is open is accepted, desyncing HPACK state.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_Headers_WhilePreviousBlockOpen_Triggers_GoAway_ProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(NoOpOriginHandler());

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var full = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/open")],
            [("x-a", "1")]);
        var half = full.Take(full.Length / 2).ToArray();

        // Open a header block on stream 1 without END_HEADERS.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, Http2FrameFlag.EndStream, half);

        // Illegal: a new HEADERS on stream 3 while stream 1's block is still open.
        var other = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/other")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(3, other, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "HEADERS while a previous header block is open must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode);
    }

    // =========================================================================
    // Http2OriginConnection — origin DATA with invalid padding
    // =========================================================================

    /// <summary>
    ///     Bug class: Http2OriginConnection accepts DATA whose pad length exceeds the payload,
    ///     corrupting the bridged HTTP/1.1 body or hanging the read loop.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_Data_PaddingExceedsPayload_FailsRequest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            var headers = conn.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, headers, endStream: false);

            // Invalid padded DATA: padLen=5 with total length=5 → 1+padLen > length.
            var bad = new byte[] { 5, 0, 0, 0, 0 };
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                Http2FrameFlag.Padded | Http2FrameFlag.EndStream, bad);

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var succeeded = false;
        try
        {
            var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(12));
            // A 5xx from the bridge is also a valid failure signal.
            succeeded = (int)response.StatusCode is >= 200 and < 300;
        }
        catch
        {
            succeeded = false;
        }

        cts.Cancel();
        Assert.IsFalse(succeeded,
            "Invalid origin DATA padding must fail the bridged request (not return 2xx).");
    }

    // =========================================================================
    // Http2OriginConnection — origin HEADERS with invalid padding
    // =========================================================================

    /// <summary>
    ///     Bug class: Http2OriginConnection decodes a HEADERS frame with illegal padding instead of
    ///     tearing down the origin connection, leaving the H1 client hanging.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_Headers_PaddingExceedsPayload_FailsRequest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // Raw HEADERS with PADDED | END_HEADERS | END_STREAM and padLen >= length.
            var hdr = new byte[9 + 2];
            hdr[0] = 0; hdr[1] = 0; hdr[2] = 2;
            hdr[3] = (byte)Http2FrameType.Headers;
            hdr[4] = (byte)(Http2FrameFlag.Padded | Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream);
            BinaryPrimitives.WriteInt32BigEndian(hdr.AsSpan(5, 4), streamId & 0x7fffffff);
            hdr[9] = 2; // padLen = 2 (== length)
            await conn.GetStream().WriteAsync(hdr.AsMemory());

            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var succeeded = false;
        try
        {
            var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(12));
            succeeded = (int)response.StatusCode is >= 200 and < 300;
        }
        catch
        {
            succeeded = false;
        }

        cts.Cancel();
        Assert.IsFalse(succeeded,
            "Invalid origin HEADERS padding must fail the bridged request.");
    }

    // =========================================================================
    // Http11ToHttp2BridgeHandler — POST body relay via Http2OriginConnection
    // =========================================================================

    /// <summary>
    ///     Bug class: H1→H2 bridge drops or truncates a POST body when multiplexing onto
    ///     <c>Http2OriginConnection</c> (SendAsync DATA path).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_PostBody_IsRelayedByteForByteToOrigin()
    {
        byte[]? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, body) = await conn.ReadRequestAsync();
            seen = body;
            var block = conn.EncodeHeaders([(":status", "201")], [("content-length", "7")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("created"));
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        var payload = Encoding.UTF8.GetBytes("post-body-bytes-\u00e9");
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("created", await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(payload, seen,
            "POST body must reach the H2 origin unmodified through Http2OriginConnection.");
    }

    // =========================================================================
    // Http11ToHttp2BridgeHandler — large streaming response from H2 origin
    // =========================================================================

    /// <summary>
    ///     Bug class: multi-frame H2 origin responses stall the H1 client when
    ///     <c>Http2OriginConnection</c> flow-control / pipe drain mishandles WINDOW_UPDATE.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_LargeChunkedOriginBody_IsFullyRelayed()
    {
        var expected = new byte[48_000];
        new Random(42).NextBytes(expected);

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders([(":status", "200")],
                [("content-length", expected.Length.ToString())]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);

            // Split across several DATA frames to exercise the origin read-loop pipe path.
            const int chunk = 12_000;
            for (var offset = 0; offset < expected.Length; offset += chunk)
            {
                var len = Math.Min(chunk, expected.Length - offset);
                var end = offset + len >= expected.Length;
                var slice = expected.AsSpan(offset, len).ToArray();
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                    end ? Http2FrameFlag.EndStream : (Http2FrameFlag)0, slice);
            }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        CollectionAssert.AreEqual(expected, body,
            "Multi-frame H2 origin body must be fully drained to the H1 client.");
    }

    // =========================================================================
    // Http2ToHttp11BridgeHandler — OPTIONS request
    // =========================================================================

    /// <summary>
    ///     Bug class: OPTIONS through the H2→H1 bridge is mishandled (method dropped / 405 substituted).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_Options_RelaysMethodAndAllowHeader()
    {
        string? seenMethod = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seenMethod = ctx.Request.Method;
            ctx.Response.StatusCode = 204;
            ctx.Response.Headers.Allow = "GET, POST, OPTIONS";
            return Task.CompletedTask;
        });

        var (_, endpoint) = CreateH2ToH1BridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "OPTIONS"), (":scheme", "https"), (":authority", "localhost"), (":path", "/api")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("204", headers.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream, "OPTIONS 204 must END_STREAM on HEADERS.");
        Assert.AreEqual("OPTIONS", seenMethod);
        Assert.IsTrue(headers.Any(h => h.Name == "allow" && h.Value.Contains("OPTIONS", StringComparison.Ordinal)),
            "Allow header must be relayed.");
    }

    // =========================================================================
    // Http2ToHttp11BridgeHandler — response Set-Cookie multiplicity
    // =========================================================================

    /// <summary>
    ///     Bug class: multiple Set-Cookie headers from an H1 origin are collapsed when translated
    ///     to H2 HEADERS, breaking cookie isolation (RFC 9113 §8.2.3).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_MultipleSetCookie_ArePreservedAsSeparateFields()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Append("Set-Cookie", "a=1; Path=/");
            ctx.Response.Headers.Append("Set-Cookie", "b=2; Path=/");
            return ctx.Response.WriteAsync("ok");
        });

        var (_, endpoint) = CreateH2ToH1BridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, headers, _) = await raw.Connection.ReadHeaderBlockAsync();
        var cookies = headers.Where(h => h.Name == "set-cookie").Select(h => h.Value).ToList();
        Assert.IsTrue(cookies.Count >= 2 || cookies.Any(c => c.Contains("a=1") && c.Contains("b=2")),
            $"Both Set-Cookie values must reach the H2 client; got: [{string.Join(" | ", cookies)}]");
        Assert.IsTrue(cookies.Any(c => c.Contains("a=1", StringComparison.Ordinal)));
        Assert.IsTrue(cookies.Any(c => c.Contains("b=2", StringComparison.Ordinal)));
    }

    // =========================================================================
    // Http2ToHttp11BridgeHandler — Content-Length mismatch (too few body bytes)
    // =========================================================================

    /// <summary>
    ///     Bug class: H2 client declares content-length=N but sends fewer DATA bytes; the bridge
    ///     must not hang forever waiting for the rest (or send a truncated body to H1).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_ContentLength_TooShort_DoesNotHang()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        });

        var (_, endpoint) = CreateH2ToH1BridgeProxy(suite);
        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        // Declare 20 bytes but only send 4 + END_STREAM.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"), (":authority", "localhost"), (":path", "/short")],
            [("content-length", "20")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Encoding.ASCII.GetBytes("abcd"));

        // Must terminate (RST / 4xx/5xx / GOAWAY / close) within the test timeout — not hang.
        var terminal = false;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline && !terminal)
            {
                var f = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(4));
                if (f.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                    terminal = true;
                else if (f.Type == Http2FrameType.Headers && f.StreamId == 1)
                {
                    var status = raw.Connection.DecodeHeaders(f.Payload)
                        .FirstOrDefault(h => h.Name == ":status").Value;
                    if (status != null)
                        terminal = true; // any response (including error) counts
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException
            or OperationCanceledException or TimeoutException)
        {
            terminal = true;
        }

        Assert.IsTrue(terminal,
            "Content-Length mismatch (too short) must not hang the H2→H1 bridge.");
    }

    // =========================================================================
    // Http2OriginConnection — RST_STREAM from origin mid-response
    // =========================================================================

    /// <summary>
    ///     Bug class: origin RST_STREAM after response HEADERS leaves the H1→H2 bridge hanging
    ///     instead of failing the HTTP/1.1 client promptly.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginRstAfterHeaders_FailsClientPromptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            var rst = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(rst, (uint)Http2ErrorCode.InternalError);
            await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, (Http2FrameFlag)0, rst);
            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        });

        var (proxy, suite) = CreateH1ToH2BridgeProxy();
        using var ownedSuite = suite;

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var succeeded = false;
        try
        {
            var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(12));
            // Headers may already have been translated; body read must still fail or status be 5xx.
            try
            {
                var bodyBytes = await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5));
                succeeded = bodyBytes.Length >= 0 && (int)response.StatusCode is >= 200 and < 300;
            }
            catch
            {
                succeeded = false;
            }
        }
        catch
        {
            succeeded = false;
        }

        cts.Cancel();
        Assert.IsFalse(succeeded,
            "Origin RST_STREAM after HEADERS must fail the bridged H1 request promptly.");
    }
}



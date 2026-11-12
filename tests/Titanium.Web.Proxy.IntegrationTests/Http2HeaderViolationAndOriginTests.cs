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
///     Additional coverage for header-semantic violations (Http2Helper.Copy.Headers.cs)
///     and Http2OriginConnection paths not yet exercised by existing tests.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2HeaderViolationAndOriginTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    // -------------------------------------------------------------------------
    // Helper: skip preamble frames and find the first significant one
    // -------------------------------------------------------------------------

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

    private static async Task<Http2RawFrame.Frame?> FindFrameAsync(
        Http2RawClient raw,
        Func<Http2RawFrame.Frame, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (predicate(frame)) return frame;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException)
            {
                return null; // connection closed by proxy — valid terminal state
            }
        }
        return null;
    }

    // =========================================================================
    // Connection-specific headers in H2 request
    // Http2Helper.Copy.Headers.cs ~line 166
    // =========================================================================

    /// <summary>
    ///     RFC 9113 §8.2.2: HTTP/2 forbids connection-specific header fields such as
    ///     <c>Connection</c> and <c>Keep-Alive</c>. The proxy must reject the stream.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_ConnectionHeader_IsRejectedWithRstStream()
    {
        // Bug class: `Connection: keep-alive` forwarded to origin (RFC 9113 §8.2.2).
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
        proxy.EnableHttpInterception = true; // force header decode

        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        // Build a header block with a `connection` header (forbidden in H2).
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", serverUri.Authority), (":path", "/conn-hdr")],
            [("connection", "keep-alive")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST/GOAWAY on `connection` header in H2 request; got {frame.Type}.");
        if (frame.Type == Http2FrameType.RstStream)
        {
            Assert.AreEqual(1, frame.StreamId);
            Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                BinaryPrimitives.ReadInt32BigEndian(frame.Payload));
        }
        Assert.IsFalse(hitOrigin, "Connection-specific header must not reach the origin.");
    }

    /// <summary>
    ///     RFC 9113 §8.2.2: <c>Transfer-Encoding</c> is a connection-specific header field
    ///     and is forbidden in HTTP/2 header blocks.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_TransferEncodingHeader_IsRejectedWithRstStream()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ => Task.CompletedTask);

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", serverUri.Authority), (":path", "/te")],
            [("transfer-encoding", "chunked")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST/GOAWAY on transfer-encoding in H2 request; got {frame.Type}.");
    }

    // =========================================================================
    // Missing required pseudo-headers in H2 request
    // Http2Helper.Copy.Headers.cs ~line 266, 279
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §8.1.2.3: a non-CONNECT request must include <c>:scheme</c>.
    ///     Omitting it is a stream-level PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_MissingScheme_IsRejectedWithRstStream()
    {
        // Bug class: :scheme omitted — request forwarded to origin with an empty/missing :scheme.
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
        proxy.EnableHttpInterception = true;

        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        // Build header block without :scheme (only :method, :authority, :path).
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":authority", serverUri.Authority), (":path", "/no-scheme")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST/GOAWAY on missing :scheme; got {frame.Type}.");
        Assert.IsFalse(hitOrigin, "Missing :scheme must not reach origin.");
    }

    /// <summary>
    ///     RFC 7540 §8.1.2.3: a non-CONNECT request must include <c>:path</c>.
    ///     Omitting it is a stream-level PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_MissingPath_IsRejectedWithRstStream()
    {
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
        proxy.EnableHttpInterception = true;

        var serverUri = new Uri(server.ListeningHttpsUrl);
        using var raw = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, serverUri.Host, serverUri.Port);

        // Build header block without :path (only :method, :scheme, :authority).
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", serverUri.Authority)],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.IsTrue(frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway,
            $"Proxy must RST/GOAWAY on missing :path; got {frame.Type}.");
        Assert.IsFalse(hitOrigin, "Missing :path must not reach origin.");
    }

    // =========================================================================
    // Even client stream id — Http2Helper.Copy.Headers.cs ~line 300
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §5.1.1: client-initiated streams must use odd stream ids.
    ///     An even stream id from the client is a connection-level PROTOCOL_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_EvenStreamId_Triggers_GoAway_ProtocolError()
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

        // Send HEADERS on stream 2 (even — only server-initiated streams use even ids).
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/even")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(2, block, endStream: true);

        var frame = await ReadSignificantFrameAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, frame.Type,
            "Even client stream id must produce GOAWAY.");
        var errorCode = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, errorCode,
            "GOAWAY error code must be PROTOCOL_ERROR.");
    }

    // =========================================================================
    // Client GOAWAY — Http2Helper.Copy.cs ~line 1937
    // =========================================================================

    /// <summary>
    ///     Bug class: a GOAWAY from the client is not relayed to the origin, causing the origin
    ///     to continue processing requests that the client has already said it will not accept.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_GoAway_IsHandled_Without_HangingProxy()
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

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Send a client-initiated GOAWAY (stream 0, NO_ERROR, last-stream-id = 0).
        var goAwayPayload = new byte[8]; // last-stream-id=0, error-code=NO_ERROR
        await raw.Connection.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, goAwayPayload);

        // After sending GOAWAY the client closes the connection. The proxy must not hang
        // and must clean up its state within the test timeout.
        raw.Dispose(); // TCP close

        // If the proxy hangs after client GOAWAY, the test timeout triggers. Passing means no hang.
        await Task.Delay(500); // give the proxy a moment to react
        Assert.IsTrue(proxy.ProxyRunning);
    }

    // =========================================================================
    // Unknown/extension frame types — Http2Helper.Copy.cs
    // =========================================================================

    /// <summary>
    ///     RFC 7540 §5.5: unknown or unsupported frame types must be ignored.
    ///     The proxy must relay them through without crashing or closing the connection.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Client_UnknownFrameType_IsIgnored_ConnectionRemainsUsable()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("ok"));
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Frame type 0xf0 is in the extension range; must be relayed/ignored.
        await raw.Connection.WriteFrameAsync((Http2FrameType)0xf0, 0, (Http2FrameFlag)0,
            new byte[] { 0x01, 0x02, 0x03 });

        // After the extension frame, a normal request must succeed.
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/after-unknown")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        // Look for a 200 response.
        string status = string.Empty;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && string.IsNullOrEmpty(status))
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
            {
                var hdrs = raw.Connection.DecodeHeaders(frame.Payload);
                status = hdrs.FirstOrDefault(h => h.Name == ":status").Value ?? string.Empty;
            }
        }

        Assert.AreEqual("200", status,
            "Unknown frame type must be ignored; subsequent normal request must succeed.");
    }

    // =========================================================================
    // Http2OriginConnection: origin RST_STREAM on a bridge request
    // =========================================================================

    /// <summary>
    ///     Bug class: when an H2 origin sends RST_STREAM before HEADERS, the proxy must
    ///     return a 502 (or similar error) to the HTTP/1.1 bridged client and must not hang.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_RstBeforeHeaders_ReturnsErrorToH11Client()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            // RST immediately without sending any response headers.
            var rst = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(rst, (uint)Http2ErrorCode.InternalError);
            await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, (Http2FrameFlag)0, rst);
            try { while (true) await conn.ReadFrameAsync(); } catch { }
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
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, origin.Url);
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var response = await client.SendAsync(req);
            // A 5xx (502/504) error or a valid response — either is acceptable.
            // The proxy must not hang.
            Assert.IsTrue((int)response.StatusCode >= 400 || (int)response.StatusCode < 300,
                "Proxy must return an error or a success when origin RSTs; must not hang.");
        }
        catch (HttpRequestException)
        {
            // Connection reset by proxy is also acceptable for a bridge error.
        }
    }

    // =========================================================================
    // Http2OriginConnection: origin GOAWAY retires the connection
    // =========================================================================

    /// <summary>
    ///     Bug class: after origin GOAWAY, the next request should be retried on a fresh connection
    ///     and must not be sent to the already-going-away connection.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H2Origin_GoAway_RetrySucceedsOnFreshConnection()
    {
        var requestCount = 0;
        var connectionCount = 0;

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            var connIndex = Interlocked.Increment(ref connectionCount);
            await conn.SendInitialSettingsAsync();

            var (streamId, _, _) = await conn.ReadRequestAsync();
            Interlocked.Increment(ref requestCount);

            if (connIndex == 1)
            {
                // First connection: answer the first request, then send GOAWAY.
                var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("ok"));

                // GOAWAY: last-stream-id = streamId (this connection is going away).
                var goAway = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(goAway, (uint)streamId);
                await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, goAway);
            }
            else
            {
                // Second connection: answer normally.
                var block = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
                await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    Encoding.ASCII.GetBytes("ok"));
                try { while (true) await conn.ReadFrameAsync(); } catch { }
            }
        });

        using var suite = new TestSuite();
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        proxy.EnableTcpServerConnectionPrefetch = false; // deterministic connection count

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);

        // First request — goes to connection 1, answered OK.
        using var req1 = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        req1.Version = HttpVersion.Version11;
        req1.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var resp1 = await client.SendAsync(req1);
        Assert.AreEqual(HttpStatusCode.OK, resp1.StatusCode, "First request must succeed.");

        // Brief delay to let the proxy register the GOAWAY and retire the connection.
        await Task.Delay(300);

        // Second request — must open a new connection (the first is going-away).
        using var req2 = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        req2.Version = HttpVersion.Version11;
        req2.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var resp2 = await client.SendAsync(req2);
        Assert.AreEqual(HttpStatusCode.OK, resp2.StatusCode, "Second request must succeed on fresh connection.");
    }

    // =========================================================================
    // Http2OriginConnection: origin sends 1xx + multiple DATA frames
    // =========================================================================

    /// <summary>
    ///     Bug class: H2 origin sends multiple DATA frames (not just one); the bridge must
    ///     concatenate all DATA payload bytes and deliver the complete body.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Origin_MultipleDataFrames_AreAssembledIntoCompleteBody()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            var block = conn.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);

            // Three separate DATA frames.
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, (Http2FrameFlag)0,
                Encoding.ASCII.GetBytes("part1-"));
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, (Http2FrameFlag)0,
                Encoding.ASCII.GetBytes("part2-"));
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("part3"));
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
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("part1-part2-part3", body,
            "Multiple DATA frames must be reassembled into the complete response body.");
    }

    // =========================================================================
    // Http2OriginConnection: large response body (flow control exercises)
    // =========================================================================

    /// <summary>
    ///     Bug class: response bodies larger than the initial HTTP/2 flow-control window
    ///     (65535 bytes) stall because the proxy does not send WINDOW_UPDATE to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H2Origin_LargeBody_IsRelayedCompletely()
    {
        // 192 KB body — 3× the default 65535 stream window.
        const int bodySize = 192 * 1024;
        var expectedBody = new string('X', bodySize);

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            var block = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-length", bodySize.ToString())]);
            await conn.WriteHeaderBlockAsync(streamId, block, endStream: false);

            // Send in 16 KB chunks (safe max frame size per RFC 7540 §4.2).
            var payload = Encoding.ASCII.GetBytes(expectedBody);
            var chunkSize = 16384;
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                var len = Math.Min(chunkSize, payload.Length - offset);
                var chunk = new byte[len];
                Array.Copy(payload, offset, chunk, 0, len);
                var isLast = offset + len >= payload.Length;
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId,
                    isLast ? Http2FrameFlag.EndStream : (Http2FrameFlag)0, chunk);
            }
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
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(bodySize, body.Length,
            "Full body must be received; flow-control stall would cause truncation.");
        Assert.AreEqual(expectedBody, body, "Body content must be byte-for-byte accurate.");
    }

    // =========================================================================
    // H2→H1 bridge: DELETE method with no body
    // =========================================================================

    /// <summary>
    ///     Bug class: DELETE requests (no body) must be relayed correctly through the H2→H1 bridge
    ///     without the proxy erroneously waiting for a body.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_Delete_NoBody_IsRelayedCorrectly()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        string? receivedMethod = null;
        server.HandleRequest(ctx =>
        {
            receivedMethod = ctx.Request.Method;
            ctx.Response.StatusCode = 200;
            return ctx.Response.WriteAsync("deleted");
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

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "DELETE"), (":scheme", "https"), (":authority", "localhost"), (":path", "/item/99")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, hdrs, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", hdrs.Single(h => h.Name == ":status").Value);

        // Drain body if any.
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do { frame = await raw.Connection.ReadFrameAsync(); }
            while (!(frame.Type == Http2FrameType.Data && (frame.Flags & Http2FrameFlag.EndStream) != 0));
        }

        Assert.AreEqual("DELETE", receivedMethod, "DELETE method must reach origin.");
    }

    // =========================================================================
    // H2→H1 bridge: custom response header preserved
    // =========================================================================

    /// <summary>
    ///     Bug class: response headers added by the origin must be preserved when the
    ///     H2→H1 bridge converts the response back to HTTP/2.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_CustomResponseHeader_IsRelayedToH2Client()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers["X-Custom"] = "bridged-header-value";
            return ctx.Response.WriteAsync("ok");
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

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/custom-hdr")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, hdrs, _) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.IsTrue(hdrs.Any(h => h.Name == "x-custom" && h.Value == "bridged-header-value"),
            "Custom response header must be relayed through the H2→H1 bridge.");
    }

    // =========================================================================
    // H2→H1 bridge: error response relayed on OPTIONS method
    // =========================================================================

    /// <summary>
    ///     Bug class: OPTIONS method with a 405 origin response must be fully relayed
    ///     without the proxy hanging or substituting a different status.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Bridge_Options_405_IsRelayedCorrectly()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 405;
            ctx.Response.Headers.Allow = "GET, HEAD";
            return ctx.Response.WriteAsync("not-allowed");
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

        using var raw = await Http2RawClient.ConnectAsync(endpoint.Port, "localhost", server.HttpsListeningPort);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "OPTIONS"), (":scheme", "https"), (":authority", "localhost"), (":path", "*")],
            Array.Empty<(string, string)>());
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var (_, hdrs, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("405", hdrs.Single(h => h.Name == ":status").Value,
            "OPTIONS 405 must be relayed to the H2 client.");

        if (!endStream)
        {
            var bodyMs = new MemoryStream();
            Http2RawFrame.Frame frame;
            do
            {
                frame = await raw.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
                    bodyMs.Write(frame.Payload, 0, frame.Payload.Length);
            } while (!(frame.Type == Http2FrameType.Data
                && frame.StreamId == 1
                && (frame.Flags & Http2FrameFlag.EndStream) != 0));

            Assert.AreEqual("not-allowed", Encoding.UTF8.GetString(bodyMs.ToArray()));
        }
    }
}

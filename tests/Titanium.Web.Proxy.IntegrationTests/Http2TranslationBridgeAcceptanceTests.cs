using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Closes out the protocol-translation acceptance matrix for both bridges added in this delivery
///     (<see cref="Http2ToHttp11BridgeHandler" /> and the HTTP/1.1-to-h2 origin bridge): synthetic
///     short-circuit responses from <c>BeforeRequest</c>, response header mutation from
///     <c>BeforeResponse</c>, and large/streamed bodies in both directions. <see cref="Http2ProtocolPolicyTests" />
///     already covers the basic success/failure matrix and sequential connection reuse; this file focuses on
///     the interception-API and body-size edge cases called out as acceptance gates.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2TranslationBridgeAcceptanceTests
{
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

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_Ok_From_BeforeRequest_Short_Circuits_Without_Contacting_Origin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originWasContacted = false;
        server.HandleRequest(context =>
        {
            originWasContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-contacted");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok-from-before-request");
            return Task.CompletedTask;
        };

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        var body = new MemoryStream();
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await rawClient.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        Assert.AreEqual("synthetic-ok-from-before-request", Encoding.ASCII.GetString(body.ToArray()));
        Assert.IsFalse(originWasContacted,
            "A synthetic BeforeRequest response must short-circuit the h2-to-HTTP/1.1 bridge before it ever " +
            "opens/uses the HTTP/1.1 origin connection.");
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H11ToH2Bridge_Ok_From_BeforeRequest_Short_Circuits_Without_Contacting_Origin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var originWasContacted = false;
        server.HandleRequest(context =>
        {
            originWasContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-contacted");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok-from-before-request");
            return Task.CompletedTask;
        };

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>
                { System.Net.Security.SslApplicationProtocol.Http11 });

        var requestBytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await tunnel.SslStream.WriteAsync(requestBytes);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response, got: '{statusLine}'.");

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
            // skip headers
        }

        var body = await reader.ReadToEndAsync();
        Assert.AreEqual("synthetic-ok-from-before-request", body);
        Assert.IsFalse(originWasContacted,
            "A synthetic BeforeRequest response must short-circuit the HTTP/1.1-to-h2 bridge before it ever " +
            "opens/uses the h2 origin connection.");
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_Large_Response_Body_Is_Relayed_Correctly()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        // Kept comfortably under the default 65535-byte h2 flow-control window (both stream- and connection-
        // level) so this test can validate multi-DATA-frame relay through the bridge without needing the
        // minimal Http2RawClient test double to also implement sending WINDOW_UPDATE frames back to the proxy.
        var expectedBody = new string('x', 50_000);
        server.HandleRequest(context => context.Response.WriteAsync(expectedBody));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId);
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        var body = new MemoryStream();
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await rawClient.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        Assert.AreEqual(expectedBody, Encoding.ASCII.GetString(body.ToArray()));
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H11ToH2Bridge_Large_Request_Body_Is_Relayed_Correctly()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var expectedBody = new string('y', 500_000);
        server.HandleRequest(async context =>
        {
            using var bodyReader = new StreamReader(context.Request.Body);
            var receivedBody = await bodyReader.ReadToEndAsync();
            context.Response.Headers["X-Received-Length"] = receivedBody.Length.ToString();
            await context.Response.WriteAsync("large-body-received");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>
                { System.Net.Security.SslApplicationProtocol.Http11 });

        var bodyBytes = Encoding.ASCII.GetBytes(expectedBody);
        var requestText = "POST / HTTP/1.1\r\n" +
                           "Host: localhost\r\n" +
                           $"Content-Length: {bodyBytes.Length}\r\n" +
                           "Connection: close\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(requestText);
        await tunnel.SslStream.WriteAsync(requestBytes);
        await tunnel.SslStream.WriteAsync(bodyBytes);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response, got: '{statusLine}'.");

        var sawReceivedLengthHeader = false;
        var chunked = false;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("X-Received-Length:", StringComparison.OrdinalIgnoreCase))
            {
                sawReceivedLengthHeader = true;
                Assert.IsTrue(line.Contains(bodyBytes.Length.ToString()),
                    $"The origin must have received the full {bodyBytes.Length}-byte body: '{line}'.");
            }

            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                chunked = true;
        }

        Assert.IsTrue(sawReceivedLengthHeader, "Expected to see the X-Received-Length response header.");

        // A short unknown-length origin body may already be complete (Content-Length) or still open
        // (chunked) depending on scheduling. Either framing is correct; the body bytes are the check.
        var responseBody = await reader.ReadToEndAsync();
        if (chunked)
            responseBody = DecodeChunkedAscii(responseBody);
        Assert.AreEqual("large-body-received", responseBody);
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H11ToH2Bridge_BeforeResponse_Header_Mutation_Is_Applied()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.Headers["X-Original"] = "from-origin";
            return context.Response.WriteAsync("h11-to-h2-header-mutation-ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.RemoveHeader("X-Original");
            e.HttpClient.Response.Headers.AddHeader("X-Mutated-By-BeforeResponse", "yes");
            return Task.CompletedTask;
        };

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new System.Collections.Generic.List<System.Net.Security.SslApplicationProtocol>
                { System.Net.Security.SslApplicationProtocol.Http11 });

        var requestBytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await tunnel.SslStream.WriteAsync(requestBytes);

        using var reader = new StreamReader(tunnel.SslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"));

        var sawMutatedHeader = false;
        var sawOriginalHeader = false;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("X-Mutated-By-BeforeResponse:", StringComparison.OrdinalIgnoreCase))
                sawMutatedHeader = true;
            if (line.StartsWith("X-Original:", StringComparison.OrdinalIgnoreCase))
                sawOriginalHeader = true;
        }

        Assert.IsTrue(sawMutatedHeader, "BeforeResponse header additions must be relayed to the HTTP/1.1 client.");
        Assert.IsFalse(sawOriginalHeader, "BeforeResponse header removals must be honored before relaying.");

        var body = await reader.ReadToEndAsync();
        Assert.AreEqual("h11-to-h2-header-mutation-ok", body);
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_RstStream_From_Client_Mid_Response_Still_Fires_AfterResponse_Exactly_Once()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var responseHeadersSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HandleRequest(async context =>
        {
            await context.Response.WriteAsync("partial-");
            await context.Response.Body.FlushAsync();
            responseHeadersSent.TrySetResult(true);

            // Keep the HTTP/1.1 response open (never completing it) so the proxy's read of the origin's
            // body is still in flight when the h2 client below resets its stream, exactly matching the
            // "message boundary is no longer recoverable" scenario this bridge must tear down cleanly
            // rather than hang or attempt to pool.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // expected once the proxy closes its side of the origin connection.
            }
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var afterResponseCount = 0;
        proxy.AfterResponse += (_, _) =>
        {
            System.Threading.Interlocked.Increment(ref afterResponseCount);
            return Task.CompletedTask;
        };

        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();

        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        // Read the response HEADERS and at least the first DATA frame so the origin's response is
        // provably in flight before the stream is reset.
        await rawClient.Connection.ReadHeaderBlockAsync();
        await rawClient.Connection.ReadFrameAsync();
        await responseHeadersSent.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload, (int)Http2ErrorCode.Cancel);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, payload);

        for (var i = 0; i < 500 && afterResponseCount < 1; i++) await Task.Delay(20);

        Assert.AreEqual(1, afterResponseCount,
            "A client-reset h2 stream bridged to an HTTP/1.1 origin must still get exactly one AfterResponse " +
            "invocation.");
        Assert.IsNull(exceptionCapture.LastException, $"No exception should be raised: {exceptionCapture.LastException}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_256KiB_DefaultWindow_TrimsShortFrameAndCompletes()
    {
        const int length = 256 * 1024;
        var body = await ReadBridgedBodyAsync(length, initialWindow: null, grantCreditAfter: 65535);
        Assert.AreEqual(length, body.Bytes.Length);
        Assert.IsTrue(body.SawShortFrame,
            "A 65,535-byte client window must produce a DATA frame shorter than 16 KiB (the 16,383-byte remainder) " +
            "instead of stalling for one more byte.");
        for (var i = 0; i < body.Bytes.Length; i++)
            Assert.AreEqual((byte)(i & 0xff), body.Bytes[i]);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task H2ToH11Bridge_1MiB_DefaultWindow_Completes()
    {
        const int length = 1024 * 1024;
        var body = await ReadBridgedBodyAsync(length, initialWindow: null, grantCreditAfter: 65535);
        Assert.AreEqual(length, body.Bytes.Length);
        Assert.IsTrue(body.SawShortFrame);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_256KiB_LargeInitialWindow_UsesFullFrames()
    {
        const int length = 256 * 1024;
        var body = await ReadBridgedBodyAsync(length, initialWindow: 1024 * 1024, grantCreditAfter: int.MaxValue);
        Assert.AreEqual(length, body.Bytes.Length);
        Assert.IsFalse(body.SawShortFrame,
            "A 1 MiB client initial window covers a 256 KiB body, so every DATA frame should be 16 KiB.");
        Assert.IsTrue(body.DataFrames > 0);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_TwoStreams_ShareConnectionWindow_BothComplete()
    {
        const int length = 200 * 1024;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            var payload = new byte[length];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i & 0xff);
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, endStream: true);
        await rawClient.Connection.WriteHeaderBlockAsync(3, requestHeaders, endStream: true);

        var bodies = new System.Collections.Generic.Dictionary<int, MemoryStream>
        {
            [1] = new MemoryStream(),
            [3] = new MemoryStream()
        };
        var open = 2;
        var granted = false;
        for (var n = 0; n < 20000 && open > 0; n++)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.GoAway || frame.Type == Http2FrameType.RstStream)
                Assert.Fail($"Unexpected {frame.Type} on stream {frame.StreamId}.");
            if (frame.Type != Http2FrameType.Data || !bodies.ContainsKey(frame.StreamId))
                continue;
            if (frame.Payload.Length > 0)
                bodies[frame.StreamId].Write(frame.Payload, 0, frame.Payload.Length);
            if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                open--;
            var total = bodies[1].Length + bodies[3].Length;
            if (!granted && total >= 30000)
            {
                granted = true;
                var increment = WindowIncrement(1024 * 1024);
                await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, increment);
                await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 1, 0, increment);
                await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 3, 0, increment);
            }
        }

        Assert.AreEqual(0, open, "Both streams must finish.");
        Assert.AreEqual(length, bodies[1].Length);
        Assert.AreEqual(length, bodies[3].Length);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2ToH11Bridge_LargeBody_RstStream_DoesNotHang()
    {
        const int length = 256 * 1024;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.ContentLength = length;
            await context.Response.Body.WriteAsync(new byte[length]);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var exceptionCapture = new TestExceptionCapture();
        proxy.Logging.LoggerFactory = exceptionCapture;
        proxy.ApplyLoggingConfiguration();
        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, endStream: true);
        await rawClient.Connection.ReadHeaderBlockAsync();

        var received = 0;
        while (received < 48 * 1024)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data)
                received += frame.Payload.Length;
        }

        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload, (int)Http2ErrorCode.Cancel);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.RstStream, 1, 0, payload);
        await Task.Delay(200);
        Assert.IsTrue(exceptionCapture.LastException is not TimeoutException);
    }

    private static byte[] WindowIncrement(int increment) =>
    [
        (byte)((increment >> 24) & 0x7f),
        (byte)((increment >> 16) & 0xff),
        (byte)((increment >> 8) & 0xff),
        (byte)(increment & 0xff)
    ];

    private static async Task<(byte[] Bytes, bool SawShortFrame, int DataFrames)> ReadBridgedBodyAsync(
        int length, int? initialWindow, int grantCreditAfter)
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            var payload = new byte[length];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i & 0xff);
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (Models.ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);
        if (initialWindow.HasValue)
        {
            // INITIAL_WINDOW_SIZE changes stream windows only. The connection window stays at
            // 65,535 until a stream-0 WINDOW_UPDATE, so grant that before the request.
            var settings = new byte[6];
            settings[0] = 0;
            settings[1] = (byte)Http2SettingsId.InitialWindowSize;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(settings.AsSpan(2), initialWindow.Value);
            await rawClient.Connection.WriteFrameAsync(Http2FrameType.Settings, 0, 0, settings);
            await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0,
                WindowIncrement(initialWindow.Value));
        }

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, endStream: true);
        var (streamId, _, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();

        var body = new MemoryStream();
        var sawShort = false;
        var dataFrames = 0;
        var granted = false;
        if (!endStream)
        {
            while (true)
            {
                var frame = await rawClient.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.GoAway || frame.Type == Http2FrameType.RstStream)
                    Assert.Fail($"Unexpected {frame.Type}.");
                if (frame.Type != Http2FrameType.Data || frame.StreamId != streamId)
                    continue;
                dataFrames++;
                if (frame.Payload.Length > 0 && frame.Payload.Length < 16384)
                    sawShort = true;
                if (frame.Payload.Length > 0)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
                if (!granted && body.Length >= grantCreditAfter && body.Length < length)
                {
                    granted = true;
                    var increment = WindowIncrement(2 * 1024 * 1024);
                    await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, 0, 0, increment);
                    await rawClient.Connection.WriteFrameAsync(Http2FrameType.WindowUpdate, streamId, 0, increment);
                }

                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }
        }

        return (body.ToArray(), sawShort, dataFrames);
    }

    private static string DecodeChunkedAscii(string raw)
    {
        var result = new StringBuilder();
        var i = 0;
        while (i < raw.Length)
        {
            var lineEnd = raw.IndexOf("\r\n", i, StringComparison.Ordinal);
            if (lineEnd < 0) break;
            var sizeText = raw.Substring(i, lineEnd - i);
            var semi = sizeText.IndexOf(';');
            if (semi >= 0) sizeText = sizeText.Substring(0, semi);
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                break;
            if (size == 0) break;
            i = lineEnd + 2;
            if (i + size > raw.Length) break;
            result.Append(raw.Substring(i, size));
            i += size;
            if (i + 2 <= raw.Length && raw.Substring(i, 2) == "\r\n")
                i += 2;
        }

        return result.ToString();
    }
}

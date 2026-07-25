using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     End-to-end tests for the WebSocket upgrade path (<see cref="WebSocketHandler.HandleWebSocketUpgrade" />)
///     and <see cref="WebSocketDecoder" />/<see cref="WebSocketFrame" />, covering: the 101 handshake being
///     relayed, the raw byte relay that follows it (<c>TcpHelper.SendRaw</c>) carrying real WebSocket frames
///     correctly in both directions, that a <c>BeforeResponse</c> subscriber can still observe/decode those
///     frames via the public <c>SessionEventArgs.WebSocketDecoderSend</c>/<c>WebSocketDecoderReceive</c> API,
///     and that a <c>BeforeResponse</c> subscriber's changes to the upgrade response actually take effect
///     (including denying the upgrade outright) rather than being silently dropped.
/// </summary>
[DoNotParallelize]
[TestClass]
public class WebSocketUpgradeTests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    private const string OriginGreetingText = "hello-from-origin";
    private const string ClientPingText = "ping-from-client";
    private static readonly Encoding AsciiEncoding = Encoding.ASCII;

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WebSocketUpgrade_RelaysFramesBothWays_AndProxyCanDecodeThemViaPublicApi()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var handshake = AsciiEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");
            await context.Transport.Output.WriteAsync(handshake);

            // Server-to-client frames must never be masked (RFC 6455 5.1) - sent unsolicited, right after
            // the handshake, to prove the client receives it through the proxy's relay untouched.
            var greeting = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes(OriginGreetingText));
            await context.Transport.Output.WriteAsync(greeting);

            // Echo back the one inbound chunk the test sends (the client's masked ping frame) byte-for-
            // byte - still masked exactly as the client sent it. This isolates what's under test (the
            // proxy's raw relay and WebSocketDecoder's masking logic) from needing the origin to itself
            // understand WebSocket framing. The origin then completes its side deterministically, rather
            // than looping forever waiting for more input that this test never sends - matching the
            // pattern used by the other tests in this file and avoiding any reliance on exactly when the
            // client-side socket closes to unblock the origin's own connection teardown.
            var result = await context.Transport.Input.ReadAsync();
            if (!result.Buffer.IsEmpty)
                foreach (var segment in result.Buffer)
                    await context.Transport.Output.WriteAsync(segment.ToArray());

            context.Transport.Input.AdvanceTo(result.Buffer.End);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        // WebSocketFrame.Data aliases either the caller-supplied buffer or the decoder's own internal
        // reassembly buffer (both get reused for subsequent reads), so - exactly as shown in the example
        // apps - a consumer must extract what it needs (here, just the decoded text) while still inside
        // the Decode(...) enumeration, rather than retaining WebSocketFrame instances for later use.
        var proxySentTexts = new List<string>();
        var proxyReceivedTexts = new List<string>();
        proxy.BeforeResponse += (_, e) =>
        {
            e.DataSent += (_, dataArgs) =>
            {
                foreach (var frame in e.WebSocketDecoderSend.Decode(dataArgs.Buffer, dataArgs.Offset, dataArgs.Count))
                    proxySentTexts.Add(frame.GetText());
            };
            e.DataReceived += (_, dataArgs) =>
            {
                foreach (var frame in e.WebSocketDecoderReceive.Decode(dataArgs.Buffer, dataArgs.Offset,
                             dataArgs.Count))
                    proxyReceivedTexts.Add(frame.GetText());
            };
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();
        var timeout = TimeSpan.FromSeconds(10);

        await stream.WriteAsync(AsciiEncoding.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));

        var reader = new RawWebSocketStreamReader(stream);
        var headerText = await reader.ReadHeadersAsync(timeout);
        Assert.IsTrue(headerText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
            $"Expected the origin's 101 handshake to be relayed to the client verbatim. Got:\n{headerText}");

        // The unsolicited greeting the origin sent right after the handshake - proved end-to-end through
        // the proxy's raw relay, decoded by a fresh client-side WebSocketDecoder.
        var clientDecoder = new WebSocketDecoder(new DefaultBufferPool());
        var greetingFrames = await reader.ReadFramesAsync(clientDecoder, 1, timeout);
        Assert.AreEqual(1, greetingFrames.Count);
        Assert.AreEqual(OriginGreetingText, greetingFrames[0].GetText());

        // Client-to-server frames must be masked - send one, have the origin echo the exact same (still
        // masked) bytes back, and confirm the client's own decoder can unmask its own echoed frame.
        var pingFrame = BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes(ClientPingText), mask: true);
        await stream.WriteAsync(pingFrame);

        var echoFrames = await reader.ReadFramesAsync(clientDecoder, 1, timeout);
        Assert.AreEqual(1, echoFrames.Count);
        Assert.AreEqual(ClientPingText, echoFrames[0].GetText());

        // Nothing else is going to be exchanged - close the client side of the tunnel now (rather than
        // only implicitly, when this method's `using var tcpClient` unwinds) so the proxy's raw relay task
        // for this still-idle WebSocket session actually ends. Otherwise it would sit there indefinitely
        // waiting for either side to send more or disconnect - exactly like a real, idle WebSocket
        // connection would - and TestSuite.Dispose()/ProxyServer.Dispose() below would hang waiting for it.
        tcpClient.Close();

        // The proxy itself, purely through the public DataSent/DataReceived + WebSocketDecoderSend/Receive
        // API (exactly as documented/used in the examples), must have observed the very same two
        // application-level frames that crossed the wire: the client's outgoing ping (a "sent" frame from
        // the proxy's point of view) and the origin's echo of it (a "received" frame), in addition to the
        // origin's initial greeting.
        WaitForCondition(() => proxyReceivedTexts.Count >= 2, timeout,
            "Expected the proxy to observe both the origin's greeting and its echo as 'received' frames.");
        Assert.AreEqual(OriginGreetingText, proxyReceivedTexts[0]);
        Assert.AreEqual(ClientPingText, proxyReceivedTexts[1]);

        WaitForCondition(() => proxySentTexts.Count >= 1, timeout,
            "Expected the proxy to observe the client's outgoing ping as a 'sent' frame.");
        Assert.AreEqual(ClientPingText, proxySentTexts[0]);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WebSocketUpgrade_FrameIntercept_CanDropAndReplace_WhileDataEventsStillFire()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);
            var handshake = AsciiEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
            await context.Transport.Output.WriteAsync(handshake);

            // Echo each inbound chunk (post-intercept wire bytes) back to the client.
            while (true)
            {
                var result = await context.Transport.Input.ReadAsync();
                if (result.Buffer.IsEmpty && result.IsCompleted)
                {
                    context.Transport.Input.AdvanceTo(result.Buffer.End);
                    break;
                }

                if (!result.Buffer.IsEmpty)
                    foreach (var segment in result.Buffer)
                        await context.Transport.Output.WriteAsync(segment.ToArray());

                context.Transport.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }

            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var observedSent = new List<string>();
        proxy.BeforeResponse += (_, e) =>
        {
            e.BeforeWebSocketFrame += async (_, frame) =>
            {
                if (frame.Direction != WebSocketFrameDirection.ClientToServer)
                    return;

                var text = Encoding.UTF8.GetString(frame.Data);
                if (text == "drop-me")
                {
                    frame.Drop();
                    return;
                }

                if (text == "replace-me")
                {
                    frame.Replace(Encoding.UTF8.GetBytes("replaced"));
                    return;
                }

                await Task.CompletedTask;
            };

            e.DataSent += (_, dataArgs) =>
            {
                foreach (var frame in e.WebSocketDecoderSend.Decode(dataArgs.Buffer, dataArgs.Offset, dataArgs.Count))
                    observedSent.Add(frame.GetText());
            };
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();
        var timeout = TimeSpan.FromSeconds(10);

        await stream.WriteAsync(AsciiEncoding.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));

        var reader = new RawWebSocketStreamReader(stream);
        await reader.ReadHeadersAsync(timeout);

        await stream.WriteAsync(BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("drop-me"), mask: true));
        await stream.WriteAsync(BuildFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes("replace-me"), mask: true));
        await stream.WriteAsync(BuildFrame(WebsocketOpCode.Ping, Encoding.UTF8.GetBytes("ctl"), mask: true));

        var clientDecoder = new WebSocketDecoder(new DefaultBufferPool());
        // Copy payloads immediately — WebSocketFrame.Data aliases the decoder buffer.
        var frames = await reader.ReadFramesAsync(clientDecoder, 2, timeout);
        var captured = frames.Select(f => (Op: f.OpCode, Payload: f.Data.ToArray())).ToList();

        tcpClient.Close();

        Assert.AreEqual(2, captured.Count, "Dropped frame must not be echoed; replace + ping should arrive.");
        Assert.AreEqual("replaced", Encoding.UTF8.GetString(captured[0].Payload));
        Assert.AreEqual(WebsocketOpCode.Ping, captured[1].Op);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("ctl"), captured[1].Payload);

        WaitForCondition(() => observedSent.Contains("replaced"), timeout,
            "DataSent must still observe the replaced frame on the wire.");
        Assert.IsFalse(observedSent.Contains("drop-me"), "Dropped frames must not appear in DataSent.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WebSocketUpgrade_BeforeResponseHandler_HeaderChangeReachesClient()
    {
        // Regression test for the fix to WebSocketHandler.HandleWebSocketUpgrade: BeforeResponse used to
        // fire only *after* the original 101 response had already been written to the client, so any
        // change made here was silently lost. It must now take effect.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var handshake = AsciiEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
            await context.Transport.Output.WriteAsync(handshake);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("X-Injected-By-Proxy", "modified-before-relay");
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(AsciiEncoding.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));

        var headerText = await new RawWebSocketStreamReader(stream).ReadHeadersAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(headerText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal));
        Assert.IsTrue(headerText.Contains("X-Injected-By-Proxy: modified-before-relay", StringComparison.Ordinal),
            $"Expected the BeforeResponse handler's header addition to reach the client. Got:\n{headerText}");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WebSocketUpgrade_BeforeResponseHandler_CanDenyUpgrade_ClientGetsReplacementResponseNotRelay()
    {
        // Regression test: previously the original 101 was already on the wire before BeforeResponse ran,
        // so a handler trying to deny the upgrade via args.Respond/GenericResponse had no effect on what
        // the client received, and the proxy still went on to relay raw bytes as if it had succeeded.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var handshake = AsciiEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
            await context.Transport.Output.WriteAsync(handshake);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };
        proxy.BeforeResponse += (_, e) =>
        {
            e.GenericResponse("Upgrade denied by policy", HttpStatusCode.Forbidden);
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(AsciiEncoding.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                ms.Write(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
            // Expected if the connection were (incorrectly) kept open as a raw relay; the assertions below
            // will fail in that case since no 403 would ever have been observed.
        }

        var responseText = AsciiEncoding.GetString(ms.ToArray());
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 403", StringComparison.Ordinal),
            $"Expected the BeforeResponse handler's replacement 403 to reach the client instead of the " +
            $"original 101. Got:\n{responseText}");
        Assert.IsFalse(responseText.Contains("HTTP/1.1 101", StringComparison.Ordinal),
            "The original 101 must never have reached the client once BeforeResponse replaced it.");
        Assert.IsTrue(responseText.Contains("Upgrade denied by policy", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Characterization for issue #572: rewriting a WebSocket upgrade RequestUri to a new host/scheme/path
    ///     that embeds the original absolute URI (including its query) as a nested query value must preserve
    ///     host, scheme, path, and the nested query on the origin request line and Host header.
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WebSocketUpgrade_RequestUriRewrite_PreservesHostSchemePathAndNestedQuery()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        string capturedRequest = null;
        var requestReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HandleTcpRequest(async context =>
        {
            var requestText = string.Empty;
            while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer) requestText += AsciiEncoding.GetString(seg.Span);
                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            capturedRequest = requestText;
            requestReady.TrySetResult(true);

            var handshake = AsciiEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
            await context.Transport.Output.WriteAsync(handshake);
            context.Transport.Output.Complete();
        });

        // Mirror the issue report: nest the original absolute URI (with its own query) inside a new query.
        const string originalAbsoluteUri = "https://echo.websocket.org/?encoding=text";
        var originBase = new Uri(server.ListeningTcpUrl);
        var rewrittenAbsolute =
            $"http://{originBase.Host}:{originBase.Port}/?socket={originalAbsoluteUri}";
        var rewrittenUri = new Uri(rewrittenAbsolute);

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.RequestUri = rewrittenUri;
            e.HttpClient.Request.Host = rewrittenUri.Authority;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(AsciiEncoding.GetBytes(
            "GET /?encoding=text HTTP/1.1\r\nHost: echo.websocket.org\r\nUpgrade: websocket\r\n" +
            "Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n"));

        Assert.IsTrue(await requestReady.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Origin never received the rewritten WebSocket upgrade request.");
        Assert.IsNotNull(capturedRequest);

        var expectedNestedQuery = "/?socket=https://echo.websocket.org/?encoding=text";
        Assert.IsTrue(
            capturedRequest.Contains(expectedNestedQuery, StringComparison.Ordinal),
            $"Expected nested query '{expectedNestedQuery}' on the origin request. Got:\n{capturedRequest}");
        Assert.IsTrue(
            capturedRequest.Contains($"Host: {rewrittenUri.Authority}", StringComparison.OrdinalIgnoreCase)
            || capturedRequest.Contains($"Host: {rewrittenUri.Host}", StringComparison.OrdinalIgnoreCase),
            $"Expected Host rewritten to the new authority. Got:\n{capturedRequest}");
        Assert.IsFalse(
            capturedRequest.Contains("Host: echo.websocket.org", StringComparison.OrdinalIgnoreCase),
            "Original Host must not be forwarded after rewrite.");
        // Must not collapse to the pre-rewrite query alone.
        Assert.IsFalse(
            capturedRequest.Contains("GET /?encoding=text HTTP/1.1", StringComparison.Ordinal),
            "Original path/query must not be forwarded unchanged.");
    }

    /// <summary>
    ///     Same nested-query rewrite through an upstream HTTP proxy (issue #572 upstream path).
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task WebSocketUpgrade_RequestUriRewrite_ThroughUpstreamProxy_PreservesNestedQuery()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        string capturedRequest = null;
        var requestReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HandleTcpRequest(async context =>
        {
            var requestText = string.Empty;
            while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer) requestText += AsciiEncoding.GetString(seg.Span);
                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            capturedRequest = requestText;
            requestReady.TrySetResult(true);

            var handshake = AsciiEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
            await context.Transport.Output.WriteAsync(handshake);
            context.Transport.Output.Complete();
        });

        var upstream = testSuite.GetProxy();
        var proxy = testSuite.GetReverseProxy(upstream);

        const string originalAbsoluteUri = "https://echo.websocket.org/?encoding=text";
        var originBase = new Uri(server.ListeningTcpUrl);
        var rewrittenUri = new Uri(
            $"http://{originBase.Host}:{originBase.Port}/?socket={originalAbsoluteUri}");

        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.RequestUri = rewrittenUri;
            e.HttpClient.Request.Host = rewrittenUri.Authority;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(AsciiEncoding.GetBytes(
            "GET /?encoding=text HTTP/1.1\r\nHost: echo.websocket.org\r\nUpgrade: websocket\r\n" +
            "Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n"));

        Assert.IsTrue(await requestReady.Task.WaitAsync(TimeSpan.FromSeconds(15)),
            "Origin never received the rewritten WebSocket upgrade through the upstream proxy.");
        Assert.IsNotNull(capturedRequest);
        Assert.IsTrue(
            capturedRequest.Contains("socket=https://echo.websocket.org/?encoding=text", StringComparison.Ordinal),
            $"Nested query must survive the upstream-proxy path. Got:\n{capturedRequest}");
    }

    private static void WaitForCondition(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(20);
        }

        Assert.IsTrue(condition(), failureMessage);
    }

    private static async Task DrainRequestHeaders(ConnectionContext context)
    {
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer) requestText += AsciiEncoding.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }

    /// <summary>
    ///     Hand-builds a raw WebSocket frame (RFC 6455 section 5.2) with the given opcode/payload, choosing
    ///     the 7-bit/16-bit length encoding automatically based on the payload size (large 64-bit-length
    ///     frames are outside the scope of these end-to-end tests; see WebSocketDecoderTests for that).
    /// </summary>
    private static byte[] BuildFrame(WebsocketOpCode opCode, byte[] payload, bool mask = false,
        uint maskKey = 0x11223344)
    {
        var bytes = new List<byte> { (byte)(0x80 | (byte)opCode) };

        var maskBit = mask ? (byte)0x80 : (byte)0x00;
        var length = payload.Length;
        if (length <= 125)
        {
            bytes.Add((byte)(maskBit | length));
        }
        else
        {
            bytes.Add((byte)(maskBit | 126));
            bytes.Add((byte)(length >> 8));
            bytes.Add((byte)length);
        }

        byte[] maskKeyBytes = null;
        if (mask)
        {
            maskKeyBytes = new[]
            {
                (byte)maskKey, (byte)(maskKey >> 8), (byte)(maskKey >> 16), (byte)(maskKey >> 24)
            };
            bytes.AddRange(maskKeyBytes);
        }

        var payloadBytes = (byte[])payload.Clone();
        if (mask)
            for (var i = 0; i < payloadBytes.Length; i++)
                payloadBytes[i] ^= maskKeyBytes[i % 4];

        bytes.AddRange(payloadBytes);
        return bytes.ToArray();
    }

    /// <summary>
    ///     Thin helper around a raw <see cref="NetworkStream" /> that first peels off the HTTP response
    ///     headers, then hands everything after them to a <see cref="WebSocketDecoder" /> - reading more
    ///     from the socket as needed - so a test can assert on decoded application-level frames instead of
    ///     raw bytes.
    /// </summary>
    private sealed class RawWebSocketStreamReader
    {
        private readonly NetworkStream stream;
        private readonly List<byte> pending = new();

        public RawWebSocketStreamReader(NetworkStream stream)
        {
            this.stream = stream;
        }

        public async Task<string> ReadHeadersAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[4096];
            while (true)
            {
                var headerEnd = FindHeaderTerminator();
                if (headerEnd >= 0)
                {
                    var headerBytes = pending.Take(headerEnd + 4).ToArray();
                    pending.RemoveRange(0, headerEnd + 4);
                    return AsciiEncoding.GetString(headerBytes);
                }

                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) throw new IOException("Connection closed before response headers completed.");
                pending.AddRange(buffer.Take(read));
            }
        }

        public async Task<List<WebSocketFrame>> ReadFramesAsync(WebSocketDecoder decoder, int minFrameCount,
            TimeSpan timeout)
        {
            var frames = new List<WebSocketFrame>();

            void Capture(IEnumerable<WebSocketFrame> decoded)
            {
                // Copy payload immediately — Data aliases decoder/read buffers.
                foreach (var frame in decoded)
                    frames.Add(new WebSocketFrame
                    {
                        IsFinal = frame.IsFinal,
                        OpCode = frame.OpCode,
                        Data = frame.Data.ToArray()
                    });
            }

            if (pending.Count > 0)
            {
                var leftover = pending.ToArray();
                pending.Clear();
                Capture(decoder.Decode(leftover, 0, leftover.Length));
            }

            using var cts = new CancellationTokenSource(timeout);
            var buffer = new byte[4096];
            while (frames.Count < minFrameCount)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) throw new IOException("Connection closed before enough frames arrived.");
                Capture(decoder.Decode(buffer, 0, read));
            }

            return frames;
        }

        private int FindHeaderTerminator()
        {
            for (var i = 0; i + 3 < pending.Count; i++)
                if (pending[i] == '\r' && pending[i + 1] == '\n' && pending[i + 2] == '\r' && pending[i + 3] == '\n')
                    return i;

            return -1;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
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
///     Validates Phase 1.4 safety measures: extension offer stripping when frame interception is
///     active, RFC 6455 frame validation in the intercepted relay path, and that raw relay continues
///     to work without any frame-level handlers.
/// </summary>
[DoNotParallelize]
[TestClass]
public class WebSocketInterceptionSafetyTests
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

    private static readonly Encoding Ascii = Encoding.ASCII;

    // -------------------------------------------------------------------------
    // Test 1: Sec-WebSocket-Extensions header is stripped before the origin
    //         receives the upgrade request when frame interception is active.
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_ExtensionOfferStripped_WhenInterceptionActive()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        // Capture the raw upgrade request the origin server actually receives.
        string capturedRequest = null;
        var requestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HandleTcpRequest(async context =>
        {
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer)
                    sb.Append(Ascii.GetString(seg.Span));
                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            capturedRequest = sb.ToString();
            requestReceived.TrySetResult(true);

            // Complete the upgrade so the client does not see a broken connection.
            var handshake = Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");
            await context.Transport.Output.WriteAsync(handshake);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;

            // Subscribing to BeforeWebSocketFrame in BeforeRequest (i.e. before the upgrade
            // request is forwarded) causes HasWebSocketFrameInterceptHandler to be true by the
            // time WebSocketHandler checks it, which triggers automatic extension stripping.
            e.BeforeWebSocketFrame += (_, _) => Task.CompletedTask;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        // Upgrade request WITH a permessage-deflate extension offer.
        await stream.WriteAsync(Ascii.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Extensions: permessage-deflate\r\n" +
            "\r\n"));

        Assert.IsTrue(await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Origin never received the WebSocket upgrade request.");

        Assert.IsNotNull(capturedRequest);
        Assert.IsFalse(
            capturedRequest.Contains("permessage-deflate", StringComparison.OrdinalIgnoreCase),
            "The proxy must strip Sec-WebSocket-Extensions when frame interception is active. " +
            $"Got:\n{capturedRequest}");
        Assert.IsFalse(
            capturedRequest.Contains("Sec-WebSocket-Extensions", StringComparison.OrdinalIgnoreCase),
            "Sec-WebSocket-Extensions header must be absent from the forwarded request. " +
            $"Got:\n{capturedRequest}");

        tcpClient.Close();
    }

    // -------------------------------------------------------------------------
    // Test 2: Without a frame interception handler, extensions are forwarded as-is
    //         (raw relay must not strip headers).
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_ExtensionOffer_NotStripped_WhenNoInterceptionHandler()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        string? capturedRequest = null;
        var requestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.HandleTcpRequest(async context =>
        {
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer)
                    sb.Append(Ascii.GetString(seg.Span));
                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            capturedRequest = sb.ToString();
            requestReceived.TrySetResult(true);

            var handshake = Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");
            await context.Transport.Output.WriteAsync(handshake);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };
        // No BeforeWebSocketFrame registered → raw relay path.

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(Ascii.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Extensions: permessage-deflate\r\n" +
            "\r\n"));

        Assert.IsTrue(await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "Origin never received the WebSocket upgrade request.");

        Assert.IsNotNull(capturedRequest);
        Assert.IsTrue(
            capturedRequest.Contains("Sec-WebSocket-Extensions", StringComparison.OrdinalIgnoreCase),
            "Raw relay must forward Sec-WebSocket-Extensions unchanged. " +
            $"Got:\n{capturedRequest}");
        Assert.IsTrue(
            capturedRequest.Contains("permessage-deflate", StringComparison.OrdinalIgnoreCase),
            "permessage-deflate offer must be preserved in raw relay mode. " +
            $"Got:\n{capturedRequest}");

        tcpClient.Close();
    }

    // -------------------------------------------------------------------------
    // Test 3: Frame interception relay correctly validates and forwards valid frames
    //         end-to-end (sanity check that validation doesn't break the happy path).
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_InterceptRelay_ValidFrames_ForwardedSuccessfully()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        const string serverMessage = "hello-intercepted";
        const string clientMessage = "ping-intercepted";

        server.HandleTcpRequest(async context =>
        {
            await DrainHeadersAsync(context);

            var handshake = Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");
            await context.Transport.Output.WriteAsync(handshake);

            // Send a server-to-client text frame (unmasked, FIN=1, opcode=Text).
            var greeting = BuildUnmaskedFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes(serverMessage));
            await context.Transport.Output.WriteAsync(greeting);

            // Echo the client frame back unchanged.
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

        var interceptedDirections = new List<WebSocketFrameDirection>();
        proxy.BeforeResponse += (_, e) =>
        {
            e.BeforeWebSocketFrame += (_, frame) =>
            {
                interceptedDirections.Add(frame.Direction);
                return Task.CompletedTask;
            };
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();
        var timeout = TimeSpan.FromSeconds(10);

        await stream.WriteAsync(Ascii.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "\r\n"));

        var reader = new RawFrameReader(stream);
        var headerText = await reader.ReadHttpHeadersAsync(timeout);
        Assert.IsTrue(headerText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
            $"Expected 101 Switching Protocols. Got:\n{headerText}");

        var clientDecoder = new WebSocketDecoder(new DefaultBufferPool());
        var greetingFrames = await reader.ReadFramesAsync(clientDecoder, 1, timeout);
        Assert.AreEqual(1, greetingFrames.Count);
        Assert.AreEqual(serverMessage, greetingFrames[0].GetText());

        // Send a masked client frame.
        var pingFrame = BuildMaskedFrame(WebsocketOpCode.Text, Encoding.UTF8.GetBytes(clientMessage));
        await stream.WriteAsync(pingFrame);

        var echoFrames = await reader.ReadFramesAsync(clientDecoder, 1, timeout);
        Assert.AreEqual(1, echoFrames.Count);
        Assert.AreEqual(clientMessage, echoFrames[0].GetText());

        tcpClient.Close();

        // The proxy must have intercepted at least the server-to-client greeting
        // and the server's echo of the client frame.
        WaitForCondition(() => interceptedDirections.Count >= 2, timeout,
            "Expected the proxy to intercept at least 2 frames (server→client greeting and echo).");

        Assert.IsTrue(interceptedDirections.Contains(WebSocketFrameDirection.ServerToClient),
            "Intercepted directions must include ServerToClient.");
        Assert.IsTrue(interceptedDirections.Contains(WebSocketFrameDirection.ClientToServer),
            "Intercepted directions must include ClientToServer.");
    }

    // -------------------------------------------------------------------------
    // Test 4: Frames with reserved opcodes are rejected (connection torn down).
    // -------------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task WebSocket_InterceptRelay_ReservedOpcode_ClosesConnection()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainHeadersAsync(context);

            var handshake = Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");
            await context.Transport.Output.WriteAsync(handshake);

            // Send a frame with reserved opcode 0x3 — a protocol violation.
            var badFrame = BuildRawFrame(firstByte: 0x83 /* FIN=1, opcode=3 */, payload: new byte[] { 0x01 });
            await context.Transport.Output.WriteAsync(badFrame);

            // Keep the server side open long enough for the relay to react.
            await Task.Delay(3000);
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
            e.BeforeWebSocketFrame += (_, _) => Task.CompletedTask;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();
        var timeout = TimeSpan.FromSeconds(10);

        await stream.WriteAsync(Ascii.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "\r\n"));

        var reader = new RawFrameReader(stream);
        var headerText = await reader.ReadHttpHeadersAsync(timeout);
        Assert.IsTrue(headerText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
            $"Expected 101 Switching Protocols. Got:\n{headerText}");

        // The relay should close the client-side connection shortly after receiving the bad frame.
        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[256];
        int read;
        try
        {
            read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Proxy did not close the connection within the timeout after receiving a reserved-opcode frame.");
            return;
        }

        // read == 0 means the peer closed the connection (expected), or we may have received some data.
        // Either way, the connection must eventually close.
        Assert.IsTrue(read == 0 || !stream.CanRead || !tcpClient.Connected,
            "Expected the proxy to tear down the WebSocket connection after a reserved-opcode frame.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] BuildUnmaskedFrame(WebsocketOpCode opCode, byte[] payload)
    {
        var bytes = new List<byte> { (byte)(0x80 | (byte)opCode) };
        if (payload.Length <= 125)
            bytes.Add((byte)payload.Length);
        else
        {
            bytes.Add(126);
            bytes.Add((byte)(payload.Length >> 8));
            bytes.Add((byte)payload.Length);
        }

        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] BuildMaskedFrame(WebsocketOpCode opCode, byte[] payload,
        uint maskKey = 0x11223344)
    {
        var bytes = new List<byte> { (byte)(0x80 | (byte)opCode) };
        var maskBit = (byte)0x80;
        if (payload.Length <= 125)
            bytes.Add((byte)(maskBit | payload.Length));
        else
        {
            bytes.Add((byte)(maskBit | 126));
            bytes.Add((byte)(payload.Length >> 8));
            bytes.Add((byte)payload.Length);
        }

        byte[] maskKeyBytes =
        {
            (byte)maskKey, (byte)(maskKey >> 8), (byte)(maskKey >> 16), (byte)(maskKey >> 24)
        };
        bytes.AddRange(maskKeyBytes);

        var maskedPayload = (byte[])payload.Clone();
        for (var i = 0; i < maskedPayload.Length; i++)
            maskedPayload[i] ^= maskKeyBytes[i % 4];

        bytes.AddRange(maskedPayload);
        return bytes.ToArray();
    }

    private static byte[] BuildRawFrame(byte firstByte, byte[] payload)
    {
        var bytes = new List<byte> { firstByte };
        if (payload.Length <= 125)
            bytes.Add((byte)payload.Length);
        else
        {
            bytes.Add(126);
            bytes.Add((byte)(payload.Length >> 8));
            bytes.Add((byte)payload.Length);
        }

        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static async Task DrainHeadersAsync(ConnectionContext context)
    {
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer)
                sb.Append(Ascii.GetString(seg.Span));
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
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

    /// <summary>
    ///     Minimal helper that reads raw bytes from a <see cref="NetworkStream" />, peels off the
    ///     HTTP response headers, then passes subsequent bytes through a <see cref="WebSocketDecoder" />.
    /// </summary>
    private sealed class RawFrameReader
    {
        private readonly NetworkStream stream;
        private readonly List<byte> pending = new();

        public RawFrameReader(NetworkStream stream) => this.stream = stream;

        public async Task<string> ReadHttpHeadersAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[4096];
            while (true)
            {
                var idx = FindHeaderTerminator();
                if (idx >= 0)
                {
                    var hdrBytes = pending.GetRange(0, idx + 4).ToArray();
                    pending.RemoveRange(0, idx + 4);
                    return Ascii.GetString(hdrBytes);
                }

                var read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (read == 0) throw new IOException("Connection closed before HTTP headers completed.");
                pending.AddRange(new ArraySegment<byte>(buf, 0, read));
            }
        }

        public async Task<List<WebSocketFrame>> ReadFramesAsync(WebSocketDecoder decoder, int minCount,
            TimeSpan timeout)
        {
            var frames = new List<WebSocketFrame>();

            void Capture(IEnumerable<WebSocketFrame> decoded)
            {
                foreach (var f in decoded)
                    frames.Add(new WebSocketFrame { IsFinal = f.IsFinal, OpCode = f.OpCode, Data = f.Data.ToArray() });
            }

            if (pending.Count > 0)
            {
                var leftover = pending.ToArray();
                pending.Clear();
                Capture(decoder.Decode(leftover, 0, leftover.Length));
            }

            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[4096];
            while (frames.Count < minCount)
            {
                var read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (read == 0) throw new IOException("Connection closed before enough frames arrived.");
                Capture(decoder.Decode(buf, 0, read));
            }

            return frames;
        }

        private int FindHeaderTerminator()
        {
            for (var i = 0; i + 3 < pending.Count; i++)
                if (pending[i] == '\r' && pending[i + 1] == '\n' &&
                    pending[i + 2] == '\r' && pending[i + 3] == '\n')
                    return i;
            return -1;
        }
    }
}

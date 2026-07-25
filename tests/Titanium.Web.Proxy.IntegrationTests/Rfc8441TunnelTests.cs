using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for the RFC 8441 h2-client → HTTP/1.1-origin WebSocket tunnel
///     (<see cref="ProxyServer.RunExtendedConnectTunnelAsync" />).
///     These tests use <see cref="Http2RawClient" /> to send raw HTTP/2 frames through the proxy
///     (via the h2-to-h1 translation bridge) so the full extended-CONNECT path is exercised
///     without requiring a dedicated h2 WebSocket client library.
/// </summary>
[TestClass]
public class Rfc8441TunnelTests
{
    private static readonly Encoding Ascii = Encoding.ASCII;

    /// <summary>
    ///     When RFC 8441 is disabled (default), the proxy MUST reject any extended CONNECT request —
    ///     either with an RST_STREAM (if SETTINGS don't even advertise support) or with a non-200
    ///     synthetic response (e.g. 501). Either way the client must not receive 200.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_Rfc8441Disabled_DoesNotReturn200()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("not-a-websocket"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = false; // explicitly disabled

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port,
            "localhost", server.TcpListeningPort);

        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "http"),
                (":authority", $"localhost:{server.TcpListeningPort}"), (":path", "/ws")
            },
            new[] { ("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);

        // Read frames until we get a HEADERS response OR a control frame that terminates the stream.
        // When RFC 8441 is disabled, the proxy sends RST_STREAM(PROTOCOL_ERROR) rather than a HEADERS
        // response - ReadHeaderBlockAsync skips control frames and would hang, so read raw frames here.
        var receivedStatus = string.Empty;
        var receivedRst = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var decoder = new Titanium.Web.Proxy.Http2.Hpack.Decoder(8192, 4096);
        while (!cts.IsCancellationRequested && !receivedRst && receivedStatus == string.Empty)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.RstStream || frame.Type == Http2FrameType.GoAway)
            {
                receivedRst = true;
            }
            else if (frame.Type == Http2FrameType.Headers && frame.StreamId == 1)
            {
                var decodedHeaders = rawClient.Connection.DecodeHeaders(frame.Payload);
                receivedStatus = decodedHeaders.FirstOrDefault(h => h.Name == ":status").Value ?? string.Empty;
            }
        }

        // The proxy must NOT have accepted the tunnel — either it sent RST_STREAM or a non-200 response.
        if (receivedRst)
            return; // RST_STREAM is a correct rejection

        Assert.AreNotEqual("200", receivedStatus,
            "Proxy must not accept an extended CONNECT tunnel when EnableRfc8441 = false.");
    }

    /// <summary>
    ///     When RFC 8441 is enabled and the proxy is in h2-to-h1 bridge mode, an extended CONNECT
    ///     for `:protocol = websocket` MUST result in a 200 (not 501) response from the proxy,
    ///     and the proxy MUST then relay DATA frames bidirectionally between the h2 client and the
    ///     h1 origin's TCP stream (after a successful WebSocket 101 handshake).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_Rfc8441Enabled_H2ToH1_TunnelOpensAndEchoesData()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        // Minimal TCP origin: reads the WebSocket upgrade request, sends 101, then echoes raw bytes.
        server.HandleTcpRequest(async context =>
        {
            // Drain the HTTP upgrade request headers sent by the proxy.
            var upgradeRequest = await ReadRequestHeadersAsync(context);
            var keyLine = upgradeRequest.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var wsKey = keyLine.Substring(keyLine.IndexOf(':') + 1).Trim();
            var wsAccept = Convert.ToBase64String(SHA1.HashData(
                Ascii.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            // Respond with 101 Switching Protocols (minimal WebSocket handshake response).
            await context.Transport.Output.WriteAsync(Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {wsAccept}\r\n\r\n"));
            await context.Transport.Output.FlushAsync();

            // Echo loop: relay every byte received back to the sender.
            while (true)
            {
                var result = await context.Transport.Input.ReadAsync();
                if (result.Buffer.IsEmpty && result.IsCompleted) break;
                foreach (var segment in result.Buffer)
                    await context.Transport.Output.WriteAsync(segment.ToArray());
                context.Transport.Input.AdvanceTo(result.Buffer.End);
                await context.Transport.Output.FlushAsync();
                if (result.IsCompleted) break;
            }

            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        // Connect to the proxy targeting the raw TCP port; use :scheme=http so the proxy
        // opens a plain TCP connection (no TLS) to the origin's raw handler.
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port,
            "localhost", server.TcpListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "http"),
                (":authority", $"localhost:{server.TcpListeningPort}"), (":path", "/ws")
            },
            new[] { ("sec-websocket-version", "13"), ("sec-websocket-key", "dGhlIHNhbXBsZSBub25jZQ==") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        // The proxy must answer 200 (tunnel open), not 501.
        var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(1, streamId, "Response must be on stream 1.");
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value,
            "RFC 8441 tunnel must respond 200 OK, not 501 Not Implemented.");
        Assert.IsFalse(endStream,
            "200 HEADERS must NOT carry END_STREAM — the tunnel stream must stay open for DATA relay.");

        // Send a raw WebSocket frame (masked text frame, as required client→server per RFC 6455 §5.3)
        // as h2 DATA. The origin is a raw echo server so we can verify the relay by checking that
        // the exact same bytes arrive back through the tunnel.
        var payload = Ascii.GetBytes("hello-rfc8441");
        var wsFrame = BuildMaskedTextFrame(payload);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, wsFrame);

        // Read DATA frames back until we have accumulated at least as many bytes as we sent.
        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < wsFrame.Length && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
                received.AddRange(frame.Payload);
        }

        Assert.IsFalse(cts.IsCancellationRequested,
            "Timed out waiting for the echoed DATA frame from the origin via the RFC 8441 tunnel.");
        CollectionAssert.AreEqual(wsFrame, received.ToArray(),
            "The bytes relayed through the tunnel must be identical to what was sent.");

        // Close the tunnel gracefully (END_STREAM from client side).
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream,
            Array.Empty<byte>());
    }

    /// <summary>
    ///     Verifies that a non-websocket :protocol value (e.g. "connect-tcp") is rejected with 501
    ///     rather than silently ignored or causing a hang, matching the proxy's documented constraint
    ///     that only the <c>websocket</c> protocol is implemented.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtendedConnect_UnknownProtocol_Returns501()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port,
            "localhost", server.TcpListeningPort);

        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "connect-tcp"), (":scheme", "http"),
                (":authority", $"localhost:{server.TcpListeningPort}"), (":path", "/proxy")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("501", responseHeaders.Single(h => h.Name == ":status").Value,
            "Unknown :protocol in extended CONNECT must be rejected with 501 Not Implemented.");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     Reads raw bytes from <paramref name="context"/> until the HTTP request header terminator
    ///     (<c>\r\n\r\n</c>) is found, draining the header section without buffering the body.
    /// </summary>
    private static async Task<string> ReadRequestHeadersAsync(ConnectionContext context)
    {
        var accumulated = string.Empty;
        while (!accumulated.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer)
                accumulated += Ascii.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }

        return accumulated;
    }

    /// <summary>
    ///     Builds a minimal, single-frame, masked WebSocket text frame (RFC 6455 §5.2) around
    ///     <paramref name="payload"/>, using a fixed masking key <c>0x37fa213d</c> for
    ///     deterministic test output. Masking is required for client-to-server frames.
    /// </summary>
    private static byte[] BuildMaskedTextFrame(byte[] payload)
    {
        // FIN=1, opcode=1 (text), MASK=1, payload length (≤125 for these test payloads)
        var maskKey = new byte[] { 0x37, 0xfa, 0x21, 0x3d };
        var frame = new List<byte> { 0x81, (byte)(0x80 | payload.Length) };
        frame.AddRange(maskKey);
        for (var i = 0; i < payload.Length; i++)
            frame.Add((byte)(payload[i] ^ maskKey[i % 4]));
        return frame.ToArray();
    }
}

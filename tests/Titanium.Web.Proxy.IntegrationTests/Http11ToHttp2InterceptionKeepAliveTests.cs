using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression: H1→H2 intercept deliver must not write the response body twice after
///     <c>WriteResponseAsync</c> coalesces tiny Content-Length bodies (sets <c>IsBodySent</c>).
///     A second body write desyncs HTTP/1 keep-alive and was the GHA MITM H1→H2 sustain-0 bug.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http11ToHttp2InterceptionKeepAliveTests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_To_H2c_WithInterception_KeepAlive_TwoGets_BothSucceed()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            for (var i = 0; i < 8; i++)
            {
                var (streamId, _, _) = await connection.ReadRequestAsync();
                var headers = connection.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                    "keepalive"u8.ToArray());
            }
        });

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        try
        {
            var beforeRequestHits = 0;
            var beforeResponseHits = 0;
            proxy.BeforeRequest += (_, _) =>
            {
                Interlocked.Increment(ref beforeRequestHits);
                return Task.CompletedTask;
            };
            proxy.BeforeResponse += (_, _) =>
            {
                Interlocked.Increment(ref beforeResponseHits);
                return Task.CompletedTask;
            };

            var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
            endpoint.ForwardHost = "127.0.0.1";
            endpoint.ForwardPort = rawServer.Port;
            endpoint.ForwardCleartext = true;
            endpoint.BeforeHttpAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };

            Assert.IsTrue(proxy.NeedsHttpInterception());

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);

            async Task<(int Status, string Body)> ExchangeAsync()
            {
                var request = "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

                var statusLine = await reader.ReadLineAsync();
                Assert.IsNotNull(statusLine);
                Assert.IsTrue(statusLine!.StartsWith("HTTP/1.1 ", StringComparison.Ordinal), statusLine);

                var status = int.Parse(statusLine.AsSpan("HTTP/1.1 ".Length, 3));
                var contentLength = -1;
                var chunked = false;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
                    else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                             && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                        chunked = true;
                }

                // Tiny H2 bodies may already be complete (Content-Length) or still open (chunked)
                // depending on scheduling. Either framing must leave the keep-alive socket aligned.
                var body = await ReadKeepAliveBodyAsync(reader, contentLength, chunked);
                return (status, body);
            }

            var (status1, body1) = await ExchangeAsync();
            Assert.AreEqual(200, status1);
            Assert.AreEqual("keepalive", body1);

            var (status2, body2) = await ExchangeAsync();
            Assert.AreEqual(200, status2);
            Assert.AreEqual("keepalive", body2);

            Assert.AreEqual(2, beforeRequestHits, "Both requests must hit the session path.");
            Assert.AreEqual(2, beforeResponseHits);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // 103 Early Hints relay — Http11ToHttp2BridgeHandler relayInterim callback
    // -------------------------------------------------------------------------

    /// <summary>
    ///     When the H2 origin sends a 103 Early Hints interim response, the H1→H2 bridge must relay it
    ///     to the HTTP/1.1 client before the final response, not discard it.
    ///     The relay path (<c>relayInterim</c> callback, ~line 670 of <c>Http11ToHttp2BridgeHandler.cs</c>)
    ///     is only active when <see cref="ProxyServer.NeedsHttpInterception" /> returns true — i.e. when at
    ///     least one <c>BeforeRequest</c> or <c>BeforeResponse</c> handler is registered.
    ///     Bug class: the bridge uses the lite-headers path (no interim channel) and silently drops 1xx
    ///     interim responses when interception is off, but must relay them when interception is on.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H11ToH2Bridge_103_EarlyHints_IsRelayedToH1Client()
    {
        // Stand up a raw h2c (cleartext) origin that sends 103 then 200.
        using var rawOrigin = Http2RawOriginServer.CreateCleartext();
        rawOrigin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();

            // 103 Early Hints — the bridge's relayInterim callback must forward this to the H1 client.
            var hints = conn.EncodeHeaders(
                new[] { (":status", "103") },
                new[] { ("link", "</style.css>; rel=preload; as=style") });
            await conn.WriteHeaderBlockAsync(streamId, hints, endStream: false);

            // Small pause so the proxy can relay the 103 before the 200 arrives.
            await Task.Delay(50);

            // Final 200 response.
            var resp200 = conn.EncodeHeaders(
                new[] { (":status", "200") },
                new[] { ("content-length", "5") });
            await conn.WriteHeaderBlockAsync(streamId, resp200, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                "hello"u8.ToArray());
        });

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        try
        {
            // BeforeRequest registration makes NeedsHttpInterception() return true, which activates
            // the relayInterim callback path in RunHttp11ToHttp2ExchangeAsync.
            proxy.BeforeRequest += (_, _) => Task.CompletedTask;

            var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
            endpoint.ForwardHost = "127.0.0.1";
            endpoint.ForwardPort = rawOrigin.Port;
            endpoint.ForwardCleartext = true;
            endpoint.BeforeHttpAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
                e.AllowHttpProtocolTranslation = true;
                return Task.CompletedTask;
            };

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port);
            await using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);

            var request = "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: keep-alive\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

            // Read the first status line — must be 103 if the bridge relays Early Hints.
            var firstStatus = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(firstStatus, "Expected a response status line.");
            Assert.IsTrue(firstStatus!.StartsWith("HTTP/1.1 103", StringComparison.Ordinal),
                $"Bridge must relay 103 Early Hints before the final 200, but got: {firstStatus}");

            // Drain the 103 headers.
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            // Now the final 200 response must follow.
            var finalStatus = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(finalStatus, "Expected the 200 final status line after 103.");
            Assert.IsTrue(finalStatus!.StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
                $"Expected 200 after the 103 Early Hints, got: {finalStatus}");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static async Task<string> ReadKeepAliveBodyAsync(StreamReader reader, int contentLength, bool chunked)
    {
        if (contentLength >= 0)
        {
            var buf = new char[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await reader.ReadAsync(buf.AsMemory(read, contentLength - read));
                Assert.IsTrue(n > 0, "Unexpected EOF reading body.");
                read += n;
            }

            return new string(buf);
        }

        Assert.IsTrue(chunked, "Keep-alive response had neither Content-Length nor chunked Transfer-Encoding.");
        var body = new StringBuilder();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync();
            Assert.IsNotNull(sizeLine, "Chunked body ended before the zero chunk.");
            var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                    // trailers
                }

                break;
            }

            var buffer = new char[size];
            var read = 0;
            while (read < size)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(read, size - read));
                Assert.AreNotEqual(0, n, "Chunk ended before its declared length.");
                read += n;
            }

            body.Append(buffer);
            Assert.AreEqual(string.Empty, await reader.ReadLineAsync(), "Expected CRLF after a chunk.");
        }

        return body.ToString();
    }
}

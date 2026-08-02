#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/1.1 WebSocket Upgrade → HTTP/2 origin (RFC 8441) via
///     <c>Http11ToHttp2BridgeHandler</c>, including the HTTP/1.1 origin fallback when the h2 origin
///     does not advertise <c>SETTINGS_ENABLE_CONNECT_PROTOCOL</c>.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Rfc8441H11ToH2TunnelTests
{
    private static TestServer sharedServer = null!;
    private static readonly Encoding Ascii = Encoding.ASCII;
    private const string SampleWsKey = "dGhlIHNhbXBsZSBub25jZQ==";

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

    [TestMethod]
    [Timeout(30_000)]
    public async Task H11Upgrade_Rfc8441Disabled_StillReturns501()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            // Should never receive the CONNECT when the feature is disabled.
            try
            {
                while (true) _ = await originConn.ReadFrameAsync();
            }
            catch
            {
                // connection closed by proxy
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = false;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        var request = BuildHttp11WebSocketUpgradeRequest($"localhost:{rawOrigin.Port}");
        await tunnel.SslStream.WriteAsync(request);

        var responseText = await ReadHttp11ResponseAsync(tunnel.SslStream);
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 501", StringComparison.Ordinal),
            $"EnableRfc8441=false must keep the historical 501; got: {FirstLine(responseText)}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H11Upgrade_Rfc8441Enabled_H2OriginWithSetting_Returns101AndEchoes()
    {
        string? observedMethod = null;
        string? observedProtocol = null;
        string? observedSubprotocol = null;

        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();

            var (reqStreamId, reqHeaders, endStream) = await originConn.ReadHeaderBlockAsync();
            observedMethod = reqHeaders.Single(h => h.Name == ":method").Value;
            observedProtocol = reqHeaders.SingleOrDefault(h => h.Name == ":protocol").Value;
            observedSubprotocol = reqHeaders.SingleOrDefault(h => h.Name == "sec-websocket-protocol").Value;
            Assert.IsFalse(endStream, "Extended CONNECT HEADERS must not carry END_STREAM.");

            var resp200 = originConn.EncodeHeaders(
                new[] { (":status", "200") },
                new[] { ("sec-websocket-protocol", "chat") });
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp200, false);

            while (true)
            {
                var frame = await originConn.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == reqStreamId)
                {
                    if (frame.Payload.Length > 0)
                        await originConn.WriteFrameAsync(Http2FrameType.Data, reqStreamId, 0, frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        await originConn.WriteFrameAsync(Http2FrameType.Data, reqStreamId,
                            Http2FrameFlag.EndStream, Array.Empty<byte>());
                        break;
                    }
                }
                else if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway)
                {
                    break;
                }
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        var request = BuildHttp11WebSocketUpgradeRequest($"localhost:{rawOrigin.Port}", "chat");
        await tunnel.SslStream.WriteAsync(request);

        var (statusLine, headers, remainder) = await ReadHttp11HeadersAsync(tunnel.SslStream);
        Assert.IsTrue(statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
            $"Expected 101 Switching Protocols; got: {statusLine}");

        var expectedAccept = Convert.ToBase64String(SHA1.HashData(
            Ascii.GetBytes(SampleWsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        Assert.AreEqual(expectedAccept, GetHeader(headers, "Sec-WebSocket-Accept"),
            "Proxy must synthesize Sec-WebSocket-Accept from the client's Sec-WebSocket-Key.");
        Assert.AreEqual("chat", GetHeader(headers, "Sec-WebSocket-Protocol"),
            "Negotiated subprotocol from the h2 origin must be relayed on the 101.");

        Assert.AreEqual("CONNECT", observedMethod, "Origin must receive extended CONNECT.");
        Assert.AreEqual("websocket", observedProtocol, "Origin must receive :protocol=websocket.");
        Assert.AreEqual("chat", observedSubprotocol, "Subprotocol must be forwarded to the h2 origin.");

        var payload = Ascii.GetBytes("hello-h11-to-h2");
        var wsFrame = BuildMaskedTextFrame(payload);
        await tunnel.SslStream.WriteAsync(wsFrame);

        var echoed = await ReadExactWithPrefixAsync(tunnel.SslStream, remainder, wsFrame.Length,
            TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(wsFrame, echoed,
            "WebSocket frame bytes must be relayed byte-for-byte through the h2 DATA tunnel.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H11Upgrade_MissingSecWebSocketKey_Returns400()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            try
            {
                while (true) _ = await originConn.ReadFrameAsync();
            }
            catch
            {
                // closed
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        var request = Ascii.GetBytes(
            "GET /ws HTTP/1.1\r\n" +
            $"Host: localhost:{rawOrigin.Port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n");
        await tunnel.SslStream.WriteAsync(request);

        var responseText = await ReadHttp11ResponseAsync(tunnel.SslStream);
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
            $"Missing Sec-WebSocket-Key must return 400; got: {FirstLine(responseText)}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H11Upgrade_OriginRejectsExtendedConnect_SurfacesStatus()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (reqStreamId, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp403 = originConn.EncodeHeaders(new[] { (":status", "403") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp403, true);
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        await tunnel.SslStream.WriteAsync(BuildHttp11WebSocketUpgradeRequest($"localhost:{rawOrigin.Port}"));
        var responseText = await ReadHttp11ResponseAsync(tunnel.SslStream);
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 403", StringComparison.Ordinal),
            $"Origin rejection must surface to the H1 client; got: {FirstLine(responseText)}");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H11Upgrade_BeforeResponseDenial_DoesNotOpenRelay()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var dataFramesFromClient = 0;
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (reqStreamId, _, _) = await originConn.ReadHeaderBlockAsync();
            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await originConn.WriteHeaderBlockAsync(reqStreamId, resp200, false);

            try
            {
                while (true)
                {
                    var frame = await originConn.ReadFrameAsync();
                    if (frame.Type == Http2FrameType.Data && frame.StreamId == reqStreamId &&
                        frame.Payload.Length > 0)
                        Interlocked.Increment(ref dataFramesFromClient);
                }
            }
            catch
            {
                // closed
            }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        proxy.BeforeResponse += (_, e) =>
        {
            if (e.HttpClient.Response.StatusCode == 101)
                e.GenericResponse("denied", HttpStatusCode.Forbidden);
            return Task.CompletedTask;
        };

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        await tunnel.SslStream.WriteAsync(BuildHttp11WebSocketUpgradeRequest($"localhost:{rawOrigin.Port}"));
        var responseText = await ReadHttp11ResponseAsync(tunnel.SslStream);
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 403", StringComparison.Ordinal),
            $"BeforeResponse denial must replace the 101; got: {FirstLine(responseText)}");

        // Give any mistaken relay a moment; the origin must not see application DATA.
        await Task.Delay(300);
        Assert.AreEqual(0, dataFramesFromClient,
            "Denied upgrades must not start the WebSocket DATA relay.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H11Upgrade_Rfc8441Enabled_OriginWithoutSetting_FallsBackToHttp11()
    {
        using var dualOrigin = new DualAlpnWebSocketOrigin(TestCertificateAuthority.ServerCertificate);

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", dualOrigin.Port,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        var request = BuildHttp11WebSocketUpgradeRequest($"localhost:{dualOrigin.Port}");
        await tunnel.SslStream.WriteAsync(request);

        var (statusLine, headers, _) = await ReadHttp11HeadersAsync(tunnel.SslStream);
        Assert.IsTrue(statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
            $"Fallback path must return 101; got: {statusLine}");

        var expectedAccept = Convert.ToBase64String(SHA1.HashData(
            Ascii.GetBytes(SampleWsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        Assert.AreEqual(expectedAccept, GetHeader(headers, "Sec-WebSocket-Accept"));

        Assert.IsTrue(dualOrigin.Http2ConnectionsAccepted > 0,
            "Bridge must still open the h2 origin connection (forced UpstreamHttpProtocol.Http2).");
        Assert.IsTrue(dualOrigin.Http11ConnectionsAccepted > 0,
            "Without ENABLE_CONNECT_PROTOCOL the proxy must fall back to a dedicated HTTP/1.1 origin connection.");

        var payload = Ascii.GetBytes("fallback-echo");
        var wsFrame = BuildMaskedTextFrame(payload);
        await tunnel.SslStream.WriteAsync(wsFrame);

        var echoed = await ReadExactAsync(tunnel.SslStream, wsFrame.Length, TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(wsFrame, echoed,
            "Fallback HTTP/1.1 WebSocket relay must echo the frame bytes.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] BuildHttp11WebSocketUpgradeRequest(string host, string? subprotocol = null)
    {
        var sb = new StringBuilder();
        sb.Append("GET /ws HTTP/1.1\r\n");
        sb.Append($"Host: {host}\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append($"Sec-WebSocket-Key: {SampleWsKey}\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        if (subprotocol != null)
            sb.Append($"Sec-WebSocket-Protocol: {subprotocol}\r\n");
        sb.Append("\r\n");
        return Ascii.GetBytes(sb.ToString());
    }

    private static async Task<string> ReadHttp11ResponseAsync(Stream stream)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token);
            if (read == 0) break;
            total += read;
            var text = Ascii.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                return text;
        }

        return Ascii.GetString(buffer, 0, total);
    }

    private static async Task<(string StatusLine, Dictionary<string, string> Headers, byte[] Remainder)>
        ReadHttp11HeadersAsync(Stream stream)
    {
        var buffer = new byte[16384];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token);
            if (read == 0) break;
            total += read;
            var text = Ascii.GetString(buffer, 0, total);
            var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx < 0) continue;

            var headerSection = text.Substring(0, idx);
            var lines = headerSection.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var statusLine = lines[0];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            var headerBytes = idx + 4;
            var remainder = total > headerBytes
                ? buffer.AsSpan(headerBytes, total - headerBytes).ToArray()
                : Array.Empty<byte>();
            return (statusLine, headers, remainder);
        }

        throw new TimeoutException("Timed out waiting for an HTTP/1.1 response header block.");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, TimeSpan timeout) =>
        await ReadExactWithPrefixAsync(stream, Array.Empty<byte>(), count, timeout);

    private static async Task<byte[]> ReadExactWithPrefixAsync(Stream stream, byte[] prefix, int count,
        TimeSpan timeout)
    {
        var buffer = new byte[count];
        var offset = 0;
        if (prefix.Length > 0)
        {
            var fromPrefix = Math.Min(prefix.Length, count);
            Buffer.BlockCopy(prefix, 0, buffer, 0, fromPrefix);
            offset = fromPrefix;
        }

        using var cts = new CancellationTokenSource(timeout);
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cts.Token);
            if (read == 0)
                throw new IOException($"Stream closed after {offset} of {count} bytes.");
            offset += read;
        }

        return buffer;
    }

    private static string? GetHeader(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\r');
        return idx < 0 ? text : text.Substring(0, idx);
    }

    private static byte[] BuildMaskedTextFrame(byte[] payload)
    {
        var maskKey = new byte[] { 0x37, 0xfa, 0x21, 0x3d };
        var frame = new List<byte> { 0x81, (byte)(0x80 | payload.Length) };
        frame.AddRange(maskKey);
        for (var i = 0; i < payload.Length; i++)
            frame.Add((byte)(payload[i] ^ maskKey[i % 4]));
        return frame.ToArray();
    }

    /// <summary>
    ///     Origin that accepts TLS with both <c>h2</c> and <c>http/1.1</c> ALPN: h2 without
    ///     ENABLE_CONNECT_PROTOCOL (so the bridge falls back), and http/1.1 that performs a minimal
    ///     WebSocket 101 + echo.
    /// </summary>
    private sealed class DualAlpnWebSocketOrigin : IDisposable
    {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private bool disposed;

        public DualAlpnWebSocketOrigin(X509Certificate2 certificate)
        {
            this.certificate = certificate;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Http2ConnectionsAccepted { get; private set; }
        public int Http11ConnectionsAccepted { get; private set; }

        private async Task AcceptLoopAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var ssl = new SslStream(client.GetStream(), false);
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            ApplicationProtocols = new List<SslApplicationProtocol>
                            {
                                SslApplicationProtocol.Http2,
                                SslApplicationProtocol.Http11
                            },
                            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
                        });

                        if (ssl.NegotiatedApplicationProtocol.Equals(SslApplicationProtocol.Http2))
                        {
                            Http2ConnectionsAccepted++;
                            var preface = new byte[Http2Helper.ConnectionPreface.Length];
                            await Http2RawFrame.ReadExactAsync(ssl, preface, 0, preface.Length);
                            var conn = new Http2RawFrame.Connection(ssl);
                            await conn.SendInitialSettingsAsync(); // no ENABLE_CONNECT_PROTOCOL
                            try
                            {
                                while (true) _ = await conn.ReadFrameAsync();
                            }
                            catch
                            {
                                // closed
                            }
                        }
                        else
                        {
                            Http11ConnectionsAccepted++;
                            await HandleHttp11WebSocketAsync(ssl);
                        }
                    }
                    catch
                    {
                        // test side asserts
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
            }
        }

        private static async Task HandleHttp11WebSocketAsync(Stream stream)
        {
            var upgradeRequest = await ReadUntilDoubleCrLfAsync(stream);
            var keyLine = upgradeRequest.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
            var wsKey = keyLine.Substring(keyLine.IndexOf(':') + 1).Trim();
            var wsAccept = Convert.ToBase64String(SHA1.HashData(
                Ascii.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            await stream.WriteAsync(Ascii.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {wsAccept}\r\n\r\n"));
            await stream.FlushAsync();

            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read));
                await stream.FlushAsync();
            }
        }

        private static async Task<string> ReadUntilDoubleCrLfAsync(Stream stream)
        {
            var accumulated = new StringBuilder();
            var buffer = new byte[1024];
            while (!accumulated.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0) break;
                accumulated.Append(Ascii.GetString(buffer, 0, read));
            }

            return accumulated.ToString();
        }

        public void Dispose()
        {
            disposed = true;
            listener.Stop();
        }
    }
}

#pragma warning disable TWP001
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>Terminate listener opens a pinned HTTP/1.1 WebTransport origin. It is not a UDP relay.</summary>
[DoNotParallelize]
[TestClass]
public class WebTransportListenerTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Listener_RejectsConnectUdp()
    {
        using var origin = new UpgradeEcho();
        using var session = Start(origin);
        using var client = await ConnectAsync(session.Port);
        var headers = await RequestAsync(client, 1, "connect-udp", "/.well-known/masque/udp/127.0.0.1/443/");
        Assert.AreEqual("501", Status(headers));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Listener_RejectsWebSocket()
    {
        using var origin = new UpgradeEcho();
        using var session = Start(origin);
        using var client = await ConnectAsync(session.Port);
        var headers = await RequestAsync(client, 1, "websocket", "/app");
        Assert.AreEqual("501", Status(headers));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Listener_EchoesCapsuleBytesToPinnedOrigin()
    {
        using var origin = new UpgradeEcho();
        using var session = Start(origin);
        using var client = await ConnectAsync(session.Port);
        var headers = await RequestAsync(client, 1, "webtransport", "/app");
        Assert.AreEqual("200", Status(headers));

        var payload = Encoding.ASCII.GetBytes("wt-listener");
        await client.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, payload);
        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < payload.Length && !cts.IsCancellationRequested)
        {
            var frame = await client.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1 && frame.Payload.Length > 0)
            {
                received.AddRange(frame.Payload);
            }
        }

        Assert.IsFalse(cts.IsCancellationRequested);
        CollectionAssert.AreEqual(payload, received.ToArray());
        Assert.IsTrue(origin.SawUpgrade);
    }

    private static ListenerSession Start(UpgradeEcho origin)
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var endPoint = new WebTransportProxyEndPoint(IPAddress.Loopback, 0)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
        };
        endPoint.Options.RequireAuth = false;
        endPoint.Options.OriginHost = "127.0.0.1";
        endPoint.Options.OriginPort = origin.Port;
        endPoint.Options.OriginHttps = false;
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new ListenerSession(proxy, endPoint);
    }

    private static async Task<Http2RawClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols = [SslApplicationProtocol.Http2],
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        });
        await ssl.WriteAsync(Http2Helper.ConnectionPreface);
        var connection = new Http2RawFrame.Connection(ssl);
        await connection.SendInitialSettingsAsync();
        return Http2RawClient.Adopt(tcp, connection);
    }

    private static async Task<List<(string Name, string Value)>> RequestAsync(
        Http2RawClient client, int streamId, string protocol, string path)
    {
        var block = client.Connection.EncodeHeaders(
        [
            (":method", "CONNECT"),
            (":protocol", protocol),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        ], [("capsule-protocol", "?1")]);
        await client.Connection.WriteHeaderBlockAsync(streamId, block, false);
        var (_, headers, _) = await client.Connection.ReadHeaderBlockAsync();
        return headers;
    }

    private static string Status(List<(string Name, string Value)> headers) =>
        headers.Find(header => header.Name == ":status").Value;

    private sealed class ListenerSession : IDisposable
    {
        private readonly ProxyServer proxy;

        public ListenerSession(ProxyServer proxy, WebTransportProxyEndPoint endPoint)
        {
            this.proxy = proxy;
            Port = endPoint.Port;
        }

        public int Port { get; }

        public void Dispose() => proxy.Dispose();
    }

    private sealed class UpgradeEcho : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();

        public UpgradeEcho()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public bool SawUpgrade { get; private set; }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = EchoAsync(client);
                }
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                // Listener stopped.
            }
        }

        private async Task EchoAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new MemoryStream();
                var one = new byte[1];
                while (header.Length < 8192)
                {
                    if (await stream.ReadAsync(one) == 0)
                    {
                        return;
                    }

                    header.WriteByte(one[0]);
                    if (header.Length >= 4)
                    {
                        var tail = header.GetBuffer();
                        var n = (int)header.Length;
                        if (tail[n - 4] == '\r' && tail[n - 3] == '\n' && tail[n - 2] == '\r' && tail[n - 1] == '\n')
                        {
                            break;
                        }
                    }
                }

                var text = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
                if (!text.Contains("Upgrade: webtransport", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SawUpgrade = true;
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: webtransport\r\n\r\n");
                await stream.WriteAsync(response);
                var buffer = new byte[4096];
                while (!stop.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    if (read == 0)
                    {
                        return;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), stop.Token);
                }
            }
        }
    }
}

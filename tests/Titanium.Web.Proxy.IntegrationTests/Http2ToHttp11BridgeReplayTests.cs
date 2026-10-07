using System;
using System.IO;
using System.Linq;
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

/// <summary>
///     Regression-2026-10-07: an h2 client bridged to an HTTP/1.1 origin reuses pooled keep-alive sockets. When
///     the origin closes one while idle (so the next request hits a dead socket), an idempotent bodiless
///     request is replayed once on a fresh connection instead of surfacing "failed" to the browser; a request
///     that may not be replayed (POST) still gets a clean 502.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2ToHttp11BridgeReplayTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task StaleReusedConnection_IdempotentGet_IsReplayedOnce()
    {
        using var origin = new StaleKeepAliveOrigin();
        var (status, body) = await RunAsync(origin, secondMethod: "GET");

        Assert.AreEqual("200", status);
        Assert.AreEqual("ok", body);
        Assert.AreEqual(2, origin.Accepted, "the replay must open exactly one fresh origin connection");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task StaleReusedConnection_Post_IsNotReplayed_Gets502()
    {
        using var origin = new StaleKeepAliveOrigin();
        var (status, _) = await RunAsync(origin, secondMethod: "POST");

        Assert.AreEqual("502", status);
        Assert.AreEqual(1, origin.Accepted, "a POST must never be replayed on a new connection");
    }

    private static async Task<(string Status, string Body)> RunAsync(StaleKeepAliveOrigin origin, string secondMethod)
    {
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost", origin.Port);

        var first = await SendAsync(raw, 1, "GET");
        Assert.AreEqual("200", first.Status, "warm-up request must succeed and pool the origin connection");

        // Let the proxy return the connection to its pool before the origin kills it.
        await Task.Delay(300);
        return await SendAsync(raw, 3, secondMethod);
    }

    private static async Task<(string Status, string Body)> SendAsync(Http2RawClient raw, int streamId, string method)
    {
        var hasBody = method == "POST";
        var pseudo = new[]
        {
            (":method", method), (":scheme", "https"), (":authority", "localhost"), (":path", "/"),
        };
        var regular = hasBody ? new[] { ("content-length", "3") } : Array.Empty<(string, string)>();

        var block = raw.Connection.EncodeHeaders(pseudo, regular);
        await raw.Connection.WriteHeaderBlockAsync(streamId, block, endStream: !hasBody);
        if (hasBody)
            await raw.Connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                Encoding.ASCII.GetBytes("abc"));

        var (id, responseHeaders, endStream) = await raw.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual(streamId, id);
        var status = responseHeaders.Single(h => h.Name == ":status").Value;

        var body = new MemoryStream();
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await raw.Connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                    body.Write(frame.Payload, 0, frame.Payload.Length);
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        return (status, Encoding.ASCII.GetString(body.ToArray()));
    }

    /// <summary>
    ///     TLS HTTP/1.1 origin. The first connection answers one request with keep-alive and then reads the next
    ///     request and closes without answering (a socket the origin timed out while the proxy had it pooled).
    ///     Every later connection answers normally.
    /// </summary>
    private sealed class StaleKeepAliveOrigin : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        private int accepted;

        public StaleKeepAliveOrigin()
        {
            listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Accepted => Volatile.Read(ref accepted);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var tcp = await listener.AcceptTcpClientAsync(cts.Token);
                    var index = Interlocked.Increment(ref accepted);
                    _ = Task.Run(() => ServeAsync(tcp, index));
                }
            }
            catch (Exception)
            {
                // listener stopped
            }
        }

        private async Task ServeAsync(TcpClient tcp, int index)
        {
            try
            {
                using (tcp)
                await using (var ssl = new SslStream(tcp.GetStream(), false))
                {
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = TestCertificateAuthority.ServerCertificate,
                        ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
                    }, cts.Token);

                    for (var served = 0; !cts.IsCancellationRequested; served++)
                    {
                        if (!await ReadRequestHeadAsync(ssl))
                            return;

                        if (index == 1 && served >= 1)
                            return; // stale: swallow the request, close without a response

                        var payload = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\n\r\nok");
                        await ssl.WriteAsync(payload, cts.Token);
                        await ssl.FlushAsync(cts.Token);
                    }
                }
            }
            catch (Exception)
            {
                // client side closed
            }
        }

        private async Task<bool> ReadRequestHeadAsync(Stream stream)
        {
            var window = 0u;
            var buf = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(buf, cts.Token) == 0)
                    return false;
                window = (window << 8) | buf[0];
                if (window == 0x0D0A0D0A)
                    return true;
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
            cts.Dispose();
        }
    }
}

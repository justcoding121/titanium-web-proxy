using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
///     Opt-in <c>:protocol=connect-tcp</c>. Off by default (still 501). On, bytes relay
///     h2-to-h2 and h2-to-raw-TCP without a WebSocket upgrade.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ConnectTcpTunnelTests
{
    private static TestServer sharedServer = null!;
    private static readonly Encoding Ascii = Encoding.ASCII;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectTcp_OptInOff_StillReturns501()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            try { while (true) await originConn.ReadFrameAsync(); } catch { /* expected */ }
        });

        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        Assert.IsFalse(proxy.EnableConnectTcp);

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);
        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "connect-tcp"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);

        var (responseHeaders, _, _) = await rawClient.Connection.ReadHeadersOrRstAsync(1);
        Assert.IsNotNull(responseHeaders);
        Assert.AreEqual("501", responseHeaders!.Single(h => h.Name == ":status").Value);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectTcp_ExplicitOptIn_H2ToH2_EchoesBytes()
    {
        using var rawOrigin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        rawOrigin.HandleConnection(async originConn =>
        {
            await originConn.SendInitialSettingsWithConnectProtocolAsync();
            var (reqStreamId, reqHeaders, endStream) = await originConn.ReadHeaderBlockAsync();
            Assert.AreEqual("connect-tcp", reqHeaders.Single(h => h.Name == ":protocol").Value);
            Assert.IsFalse(endStream);
            var resp200 = originConn.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
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
        proxy.EnableRfc8441 = false;
        proxy.ProxyEndPoints[0].AcceptConnectTcp = true;

        using var rawClient = await Http2RawClient.ConnectAsync(
            proxy.ProxyEndPoints[0].Port, "localhost", rawOrigin.Port);
        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "connect-tcp"), (":scheme", "https"),
                (":authority", $"localhost:{rawOrigin.Port}"), (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        var payload = Ascii.GetBytes("hello-connect-tcp");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, payload);
        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < payload.Length && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
                received.AddRange(frame.Payload);
        }

        Assert.IsFalse(cts.IsCancellationRequested, "Timed out waiting for connect-tcp echo.");
        CollectionAssert.AreEqual(payload, received.ToArray());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectTcp_ExplicitOptIn_H2ToTcp_EchoesBytes()
    {
        using var echo = new TcpEchoServer();
        using var testSuite = new TestSuite(sharedServer);
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableConnectTcp = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectAsync(
            endpoint.Port, "localhost", echo.Port);
        var headers = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "CONNECT"), (":protocol", "connect-tcp"), (":scheme", "http"),
                (":authority", $"localhost:{echo.Port}"), (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, headers, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        var payload = Ascii.GetBytes("tcp-echo");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, payload);
        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count < payload.Length && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
                received.AddRange(frame.Payload);
        }

        Assert.IsFalse(cts.IsCancellationRequested, "Timed out waiting for raw TCP echo.");
        CollectionAssert.AreEqual(payload, received.ToArray());
    }

    private sealed class TcpEchoServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();

        public TcpEchoServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
            cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = EchoAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
                // listener stopped
            }
            catch (ObjectDisposedException)
            {
                // listener stopped
            }
            catch (SocketException)
            {
                // listener stopped
            }
        }

        private async Task EchoAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, cts.Token);
                        if (read == 0)
                            break;
                        await stream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // closed
                }
                catch (Exception)
                {
                    // peer closed
                }
            }
        }
    }
}

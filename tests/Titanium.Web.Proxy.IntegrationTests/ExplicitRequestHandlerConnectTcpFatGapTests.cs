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
///     Fat explicit <c>RequestHandler</c> / H2 bridge suite: sequential <c>:protocol=connect-tcp</c>
///     tunnels on one client connection.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerConnectTcpFatGapTests
{
    private static TestServer sharedServer = null!;
    private static readonly Encoding Ascii = Encoding.ASCII;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: connect-tcp to a raw TCP origin returns 501 when only the server flag is set.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectTcp_ServerEnable_EchoesBytes()
    {
        using var echo = new TcpEchoServer();
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
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
        await RunConnectTcpEchoAsync(rawClient, echo.Port, streamId: 1, Ascii.GetBytes("gap-connect-tcp"));
    }

    private static async Task RunConnectTcpEchoAsync(
        Http2RawClient rawClient, int echoPort, int streamId, byte[] payload)
    {
        var headers = rawClient.Connection.EncodeHeaders(
        [
            (":method", "CONNECT"), (":protocol", "connect-tcp"), (":scheme", "http"),
            (":authority", $"localhost:{echoPort}"), (":path", "/")
        ],
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(streamId, headers, false);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync()
            .WaitAsync(TimeSpan.FromSeconds(12));
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0, payload);
        var received = new List<byte>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (received.Count < payload.Length && !cts.IsCancellationRequested)
        {
            var frame = await rawClient.Connection.ReadFrameAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
            {
                received.AddRange(frame.Payload);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }
        }

        Assert.IsFalse(cts.IsCancellationRequested, "Timed out waiting for connect-tcp echo.");
        CollectionAssert.AreEqual(payload, received.ToArray());
        await rawClient.Connection.WriteFrameAsync(
            Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, Array.Empty<byte>());
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
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
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
                catch (OperationCanceledException) { }
                catch (Exception) { }
            }
        }
    }
}

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy.Headers</c> + CONNECT-UDP suite: MASQUE relay on an opted-in
///     explicit listener, 501 without <see cref="ExplicitProxyEndPoint.AcceptConnectUdp" />, and
///     blocked target port policy.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyConnectUdpMasqueFatGapTests
{
    /// <summary>
    ///     Bug class: connect-udp without AcceptConnectUdp still opens a UDP relay instead of 501.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectUdp_WithoutOptIn_Returns501()
    {
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];

        using var rawClient = await Http2RawClient.ConnectAsync(
            endpoint.Port, "localhost", testSuite.GetServer().HttpsListeningPort);
        var path = "/.well-known/masque/udp/127.0.0.1/9/";
        var block = rawClient.Connection.EncodeHeaders(
        [
            (":method", "CONNECT"),
            (":protocol", "connect-udp"),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        ], [("capsule-protocol", "?1")]);
        await rawClient.Connection.WriteHeaderBlockAsync(1, block, false);
        var (_, headers, _) = await rawClient.Connection.ReadHeaderBlockAsync()
            .WaitAsync(TimeSpan.FromSeconds(12));
        var status = headers.First(h => h.Name == ":status").Value;
        Assert.AreEqual("501", status,
            "connect-udp on a listener without AcceptConnectUdp must 501.");
    }

    /// <summary>
    ///     Bug class: connect-udp capsule bytes are not relayed to the loopback UDP target.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectUdp_AcceptConnectUdp_RelaysDatagram()
    {
        using var echo = new UdpEcho();
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.AcceptConnectUdp = true;
        var policy = endpoint.ConnectUdpHost.Options;
        policy.RequireAuth = false;
        policy.BlockPrivateTargets = false;
        policy.AllowedPorts = [echo.Port];

        using var rawClient = await Http2RawClient.ConnectAsync(
            endpoint.Port, "localhost", testSuite.GetServer().TcpListeningPort);
        var path = $"/.well-known/masque/udp/127.0.0.1/{echo.Port}/";
        var block = rawClient.Connection.EncodeHeaders(
        [
            (":method", "CONNECT"),
            (":protocol", "connect-udp"),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        ], [("capsule-protocol", "?1")]);
        await rawClient.Connection.WriteHeaderBlockAsync(1, block, false);
        var (_, headers, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", headers.Find(header => header.Name == ":status").Value);

        var payload = new byte[] { 4, 3, 2, 1 };
        var capsule = new byte[payload.Length + 16];
        var written = CapsuleReader.WriteDatagram(capsule, payload);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, capsule.AsSpan(0, written).ToArray());

        var reader = new CapsuleReader();
        byte[]? echoed = null;
        var deadline = Environment.TickCount64 + 10_000;
        while (echoed is null && Environment.TickCount64 < deadline)
        {
            var frame = await rawClient.Connection.ReadFrameAsync();
            if (frame.Type != Http2FrameType.Data || frame.StreamId != 1 || frame.Payload.Length == 0)
                continue;

            reader.Feed(frame.Payload, span => echoed = span.ToArray());
        }

        CollectionAssert.AreEqual(payload, echoed);
    }

    /// <summary>
    ///     Bug class: connect-udp to a disallowed port is accepted and dials anyway.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectUdp_DisallowedPort_Returns403()
    {
        using var echo = new UdpEcho();
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.AcceptConnectUdp = true;
        var policy = endpoint.ConnectUdpHost.Options;
        policy.RequireAuth = false;
        policy.BlockPrivateTargets = false;
        policy.AllowedPorts = [echo.Port + 1];

        using var rawClient = await Http2RawClient.ConnectAsync(
            endpoint.Port, "localhost", testSuite.GetServer().TcpListeningPort);
        var path = $"/.well-known/masque/udp/127.0.0.1/{echo.Port}/";
        var block = rawClient.Connection.EncodeHeaders(
        [
            (":method", "CONNECT"),
            (":protocol", "connect-udp"),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        ], [("capsule-protocol", "?1")]);
        await rawClient.Connection.WriteHeaderBlockAsync(1, block, false);
        var (_, headers, _) = await rawClient.Connection.ReadHeaderBlockAsync()
            .WaitAsync(TimeSpan.FromSeconds(12));
        Assert.AreEqual("403", headers.First(h => h.Name == ":status").Value);
    }

    private sealed class UdpEcho : IDisposable
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource cancellation = new();

        public UdpEcho()
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public void Dispose()
        {
            cancellation.Cancel();
            socket.Dispose();
            cancellation.Dispose();
        }

        private async Task LoopAsync()
        {
            var buffer = new byte[2048];
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                        new IPEndPoint(IPAddress.Loopback, 0), cancellation.Token);
                    await socket.SendToAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None,
                        received.RemoteEndPoint, cancellation.Token);
                }
            }
            catch (Exception)
            {
                // Stopped by dispose.
            }
        }
    }
}

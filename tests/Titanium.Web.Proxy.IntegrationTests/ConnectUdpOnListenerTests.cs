#pragma warning disable TWP001
using System;
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

/// <summary>connect-udp on an explicit listener uses MASQUE policy and stays 501 until opted in.</summary>
[DoNotParallelize]
[TestClass]
public class ConnectUdpOnListenerTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task AcceptConnectUdp_RelaysOneDatagram()
    {
        using var echo = new UdpEcho();
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.AcceptConnectUdp = true;
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
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

        var payload = new byte[] { 9, 8, 7 };
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
            {
                continue;
            }

            reader.Feed(frame.Payload, span => echoed = span.ToArray());
        }

        CollectionAssert.AreEqual(payload, echoed);
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
                // The test disposes the socket to stop the loop.
            }
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>SOCKS5 UDP ASSOCIATE relays one datagram and stops when the control connection closes.</summary>
[DoNotParallelize]
[TestClass]
public class Socks5UdpAssociateTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpAssociate_EchoesOneDatagram_ThenStopsWhenControlCloses()
    {
        using var echo = new UdpEcho();
        var proxy = BuildProxy();
        try
        {
            var socksPort = proxy.ProxyEndPoints[0].Port;
            using var control = new TcpClient(IPAddress.Loopback.ToString(), socksPort);
            var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var method = new byte[2];
            await ReadExactAsync(stream, method);
            Assert.AreEqual(0, method[1]);

            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 });
            var reply = new byte[10];
            await ReadExactAsync(stream, reply);
            Assert.AreEqual(5, reply[0]);
            Assert.AreEqual(0, reply[1]);
            Assert.AreEqual(1, reply[3]);
            var relayPort = (reply[8] << 8) + reply[9];
            Assert.AreNotEqual(0, relayPort);

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var payload = new byte[] { 4, 5, 6 };
            var header = new byte[10 + payload.Length];
            header[3] = 1;
            IPAddress.Loopback.GetAddressBytes().CopyTo(header, 4);
            header[8] = (byte)(echo.Port >> 8);
            header[9] = (byte)echo.Port;
            payload.CopyTo(header, 10);
            await client.SendToAsync(header, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, relayPort));

            var received = new byte[64];
            client.ReceiveTimeout = 10_000;
            var from = new IPEndPoint(IPAddress.Loopback, 0);
            var result = await client.ReceiveFromAsync(received, SocketFlags.None, from);
            Assert.IsTrue(result.ReceivedBytes > 10);
            CollectionAssert.AreEqual(payload, received.AsSpan(10, payload.Length).ToArray());

            control.Close();
            await Task.Delay(200);
            await client.SendToAsync(header, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, relayPort));
            client.ReceiveTimeout = 500;
            await Assert.ThrowsExactlyAsync<SocketException>(async () =>
                await client.ReceiveFromAsync(received, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, 0)));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static ProxyServer BuildProxy()
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        return proxy;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new InvalidOperationException("SOCKS control connection closed early.");
            }

            offset += read;
        }
    }

    private sealed class UdpEcho : IDisposable
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly System.Threading.CancellationTokenSource cancellation = new();

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

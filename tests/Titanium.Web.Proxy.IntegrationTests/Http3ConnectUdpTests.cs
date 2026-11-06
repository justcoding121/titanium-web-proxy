#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>CONNECT-UDP capsules on an HTTP/3 request stream when the listener opts in.</summary>
[DoNotParallelize]
[TestClass]
public class Http3ConnectUdpTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task AcceptConnectUdp_RelaysOneDatagramOnTheRequestStream()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
        {
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
        }

        using var echo = new UdpEcho();
        using var testSuite = new TestSuite();
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = testSuite.GetServer().HttpsListeningPort,
            AcceptConnectUdp = true,
        };
        var policy = quicEp.ConnectUdpHost.Options;
        policy.RequireAuth = false;
        policy.BlockPrivateTargets = false;
        policy.AllowedPorts = [echo.Port];
        policy.IdleTimeout = TimeSpan.FromSeconds(5);

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
            EnableRfc8441 = false,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await client.WaitForPeerSettingsAsync();
            Assert.IsTrue(client.PeerSettings.TryGetValue(8, out var enableConnect) && enableConnect == 1);
            Assert.IsFalse(proxy.EnableRfc8441);

            var path = $"/.well-known/masque/udp/127.0.0.1/{echo.Port}/";
            await using var tunnel = await client.OpenExtendedConnectAsync(
                "localhost", path, "connect-udp", [("capsule-protocol", "?1")]);
            Assert.AreEqual(200, tunnel.Status);

            var payload = new byte[] { 9, 8, 7 };
            var capsule = new byte[payload.Length + 16];
            var written = CapsuleReader.WriteDatagram(capsule, payload);
            await tunnel.SendAsync(capsule.AsSpan(0, written).ToArray());

            var reader = new CapsuleReader();
            byte[]? echoed = null;
            var deadline = Environment.TickCount64 + 10_000;
            while (echoed is null && Environment.TickCount64 < deadline)
            {
                var frame = await tunnel.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (frame.Length == 0)
                {
                    continue;
                }

                reader.Feed(frame, span => echoed = span.ToArray());
            }

            CollectionAssert.AreEqual(payload, echoed);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConnectUdp_WithoutTheListenerFlag_Returns501()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
        {
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
        }

        using var testSuite = new TestSuite();
        var quicEp = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = testSuite.GetServer().HttpsListeningPort,
        };
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpsSvcbDnsDiscovery = false,
            EnableRfc8441 = true,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.AddEndPoint(quicEp);
        proxy.Start();
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, quicEp.Port), "localhost");
            await using var tunnel = await client.OpenExtendedConnectAsync(
                "localhost", "/.well-known/masque/udp/127.0.0.1/443/", "connect-udp");
            Assert.AreEqual(501, tunnel.Status);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
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

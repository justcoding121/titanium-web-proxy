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
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     CONNECT-UDP over HTTP/2 capsules on a MASQUE listener. Existing listeners are covered by
///     <c>ExtendedConnect_ConnectUdp_OnExplicitListener_Returns501</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MasqueRelayTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task RoundTrip_EchoesBothWays_AndEndStreamReturnsActiveToZero()
    {
        using var echo = new UdpEcho();
        using var proxy = StartMasque(options =>
        {
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
        });

        using var client = await ConnectAsync(proxy.Port);
        await OpenTunnelAsync(client, 1, echo.Port);
        var echoed = await ExchangeAsync(client, 1, "alpha"u8.ToArray());
        CollectionAssert.AreEqual("alpha"u8.ToArray(), echoed);

        await client.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, []);
        await WaitUntilAsync(() => proxy.Active == 0);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TwoTunnels_OnOneConnection_DoNotInterfere()
    {
        using var echo = new UdpEcho();
        using var proxy = StartMasque(options =>
        {
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
        });

        using var client = await ConnectAsync(proxy.Port);
        await OpenTunnelAsync(client, 1, echo.Port);
        await OpenTunnelAsync(client, 3, echo.Port);
        await WriteDatagramAsync(client, 1, "one"u8.ToArray());
        await WriteDatagramAsync(client, 3, "two"u8.ToArray());
        var echoes = await ReadEchoesAsync(client, 1, 3);
        CollectionAssert.AreEqual("one"u8.ToArray(), echoes[1]);
        CollectionAssert.AreEqual("two"u8.ToArray(), echoes[3]);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MissingCredentials_Return407_AndBlockedTargetsReturn403()
    {
        using var echo = new UdpEcho();
        using var proxy = StartMasque(options =>
        {
            options.RequireAuth = true;
            options.BlockPrivateTargets = true;
            options.AllowedPorts = [echo.Port];
        });
        proxy.Proxy.ProxyBasicAuthenticateFunc = (_, user, password) =>
            Task.FromResult(user == "user" && password == "secret");

        using var client = await ConnectAsync(proxy.Port);
        var denied = await RequestAsync(client, 1, echo.Port, null);
        Assert.AreEqual("407", Status(denied));
        Assert.IsTrue(denied.Exists(header =>
            header.Name == "proxy-authenticate" && header.Value.Contains("Basic", StringComparison.Ordinal)));

        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes("user:secret"));
        var blockedPort = await RequestAsync(client, 3, 53, token);
        Assert.AreEqual("403", Status(blockedPort));

        var blockedAddress = await RequestAsync(client, 5, echo.Port, token);
        Assert.AreEqual("403", Status(blockedAddress));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BadPath_Returns400_AndSecondTunnelOverTheCap_Returns429()
    {
        using var echo = new UdpEcho();
        using var proxy = StartMasque(options =>
        {
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
            options.MaxTunnels = 1;
        });

        using var client = await ConnectAsync(proxy.Port);
        var bad = await RequestAsync(client, 1, echo.Port, null, "/.well-known/masque/udp/not-a-path");
        Assert.AreEqual("400", Status(bad));

        await OpenTunnelAsync(client, 3, echo.Port);
        var limited = await RequestAsync(client, 5, echo.Port, null);
        Assert.AreEqual("429", Status(limited));
        Assert.AreEqual(1, proxy.Active);

        await client.Connection.WriteFrameAsync(Http2FrameType.Data, 3, Http2FrameFlag.EndStream, []);
        await WaitUntilAsync(() => proxy.Active == 0);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task IdleTimeout_ClosesTheTunnel()
    {
        using var echo = new UdpEcho();
        using var proxy = StartMasque(options =>
        {
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
            options.IdleTimeout = TimeSpan.FromMilliseconds(200);
        });

        using var client = await ConnectAsync(proxy.Port);
        await OpenTunnelAsync(client, 1, echo.Port);
        Assert.AreEqual(1, proxy.Active);
        await WaitUntilAsync(() => proxy.Active == 0);
    }

    private static MasqueSession StartMasque(Action<MasqueOptions> configure)
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var endPoint = new MasqueProxyEndPoint(IPAddress.Loopback, 0)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
        };
        configure(endPoint.Options);
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new MasqueSession(proxy, endPoint);
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

    private static async Task OpenTunnelAsync(Http2RawClient client, int streamId, int port)
    {
        var headers = await RequestAsync(client, streamId, port, null);
        Assert.AreEqual("200", Status(headers), "CONNECT-UDP must open with 200.");
        Assert.IsFalse(headers.Exists(header => header.Name == ":status" && header.Value != "200"));
    }

    private static async Task<List<(string Name, string Value)>> RequestAsync(Http2RawClient client, int streamId,
        int port, string? basicToken, string? path = null)
    {
        path ??= $"/.well-known/masque/udp/127.0.0.1/{port}/";
        var regular = new List<(string Name, string Value)> { ("capsule-protocol", "?1") };
        if (basicToken is not null)
        {
            regular.Add(("proxy-authorization", "Basic " + basicToken));
        }

        var block = client.Connection.EncodeHeaders(
        [
            (":method", "CONNECT"),
            (":protocol", "connect-udp"),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        ], regular);
        await client.Connection.WriteHeaderBlockAsync(streamId, block, false);
        var (_, headers, _) = await client.Connection.ReadHeaderBlockAsync();
        return headers;
    }

    private static async Task<byte[]> ExchangeAsync(Http2RawClient client, int streamId, byte[] payload)
    {
        await WriteDatagramAsync(client, streamId, payload);
        var echoes = await ReadEchoesAsync(client, streamId);
        return echoes[streamId];
    }

    private static async Task WriteDatagramAsync(Http2RawClient client, int streamId, byte[] payload)
    {
        var capsule = new byte[payload.Length + 16];
        var written = CapsuleReader.WriteDatagram(capsule, payload);
        await client.Connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0, capsule.AsSpan(0, written).ToArray());
    }

    private static async Task<Dictionary<int, byte[]>> ReadEchoesAsync(Http2RawClient client, params int[] streamIds)
    {
        var pending = new HashSet<int>(streamIds);
        var readers = new Dictionary<int, CapsuleReader>();
        var got = new Dictionary<int, byte[]>();
        foreach (var streamId in streamIds)
        {
            readers[streamId] = new CapsuleReader();
        }

        var deadline = Environment.TickCount64 + 10_000;
        while (pending.Count > 0 && Environment.TickCount64 < deadline)
        {
            var frame = await client.Connection.ReadFrameAsync();
            if (frame.Type != Http2FrameType.Data || !readers.ContainsKey(frame.StreamId) || frame.Payload.Length == 0)
            {
                continue;
            }

            readers[frame.StreamId].Feed(frame.Payload, span =>
            {
                got[frame.StreamId] = span.ToArray();
                pending.Remove(frame.StreamId);
            });
        }

        Assert.AreEqual(0, pending.Count, "The UDP echo did not return a capsule for every tunnel.");
        return got;
    }

    private static string Status(List<(string Name, string Value)> headers) =>
        headers.Find(header => header.Name == ":status").Value;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(condition());
    }

    private sealed class MasqueSession : IDisposable
    {
        public MasqueSession(ProxyServer proxy, MasqueProxyEndPoint endPoint)
        {
            Proxy = proxy;
            EndPoint = endPoint;
        }

        public ProxyServer Proxy { get; }

        public MasqueProxyEndPoint EndPoint { get; }

        public int Port => EndPoint.Port;

        public int Active => EndPoint.Active;

        public void Dispose()
        {
            Proxy.Stop();
            Proxy.Dispose();
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
                        new IPEndPoint(IPAddress.Any, 0), cancellation.Token);
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

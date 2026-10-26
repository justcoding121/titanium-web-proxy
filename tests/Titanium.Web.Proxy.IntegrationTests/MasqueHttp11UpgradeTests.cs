#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>RFC 9298 section 3.3 CONNECT-UDP over HTTP/1.1 Upgrade. Off unless the listener opts in.</summary>
[TestClass]
[DoNotParallelize]
public class MasqueHttp11UpgradeTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task FlagOn_UpgradeEchoesADatagram_IncludingBytesPipelinedWithTheHeaders()
    {
        using var echo = new UdpEcho();
        using var proxy = Start(options =>
        {
            options.AllowHttp11Upgrade = true;
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
        });

        using var client = await ConnectAsync(proxy.Port, http11: true);
        var payload = "ping"u8.ToArray();
        var capsule = new byte[payload.Length + 16];
        var written = CapsuleReader.WriteDatagram(capsule, payload);
        var request = RequestBytes(echo.Port, basic: null, contentLength: null);
        var combined = new byte[request.Length + written];
        request.CopyTo(combined, 0);
        capsule.AsSpan(0, written).CopyTo(combined.AsSpan(request.Length));
        await client.Stream.WriteAsync(combined);

        var (status, headers, remainder) = await ReadHeadAsync(client.Stream);
        Assert.AreEqual(101, status);
        Assert.IsTrue(headers.Contains("Connection: Upgrade", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(headers.Contains("Upgrade: connect-udp", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(headers.Contains("Capsule-Protocol: ?1", StringComparison.OrdinalIgnoreCase));

        var echoed = await ReadDatagramAsync(client.Stream, remainder);
        CollectionAssert.AreEqual(payload, echoed);
        Assert.AreEqual(1, proxy.Active);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task FlagOff_Http11OnlyClientIsRefused()
    {
        using var proxy = Start(options => options.RequireAuth = false);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            }));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NoCredentials_Is407_BeforeDns()
    {
        var lookups = 0;
        using var proxy = Start(options =>
        {
            options.AllowHttp11Upgrade = true;
            options.DnsResolver = (_, _) =>
            {
                Interlocked.Increment(ref lookups);
                return Task.FromResult(new[] { IPAddress.Loopback });
            };
        });

        using var client = await ConnectAsync(proxy.Port, http11: true);
        await client.Stream.WriteAsync(RequestBytes(443, basic: null, contentLength: null,
            host: "example.test"));
        var (status, headers, _) = await ReadHeadAsync(client.Stream);
        Assert.AreEqual(407, status);
        Assert.IsTrue(headers.Contains("Proxy-Authenticate:", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, lookups);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DeniedPort_Is403()
    {
        using var echo = new UdpEcho();
        using var proxy = Start(options =>
        {
            options.AllowHttp11Upgrade = true;
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [9];
        });

        using var client = await ConnectAsync(proxy.Port, http11: true);
        await client.Stream.WriteAsync(RequestBytes(echo.Port, basic: null, contentLength: null));
        var (status, _, _) = await ReadHeadAsync(client.Stream);
        Assert.AreEqual(403, status);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PrivateTarget_Is403()
    {
        using var echo = new UdpEcho();
        using var proxy = Start(options =>
        {
            options.AllowHttp11Upgrade = true;
            options.RequireAuth = false;
            options.AllowedPorts = [echo.Port];
        });

        using var client = await ConnectAsync(proxy.Port, http11: true);
        await client.Stream.WriteAsync(RequestBytes(echo.Port, basic: null, contentLength: null));
        var (status, _, _) = await ReadHeadAsync(client.Stream);
        Assert.AreEqual(403, status);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Body_Is400()
    {
        using var echo = new UdpEcho();
        using var proxy = Start(options =>
        {
            options.AllowHttp11Upgrade = true;
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
        });

        using var client = await ConnectAsync(proxy.Port, http11: true);
        var head = RequestBytes(echo.Port, basic: null, contentLength: 4);
        var message = new byte[head.Length + 4];
        head.CopyTo(message, 0);
        "body"u8.CopyTo(message.AsSpan(head.Length));
        await client.Stream.WriteAsync(message);
        var (status, _, _) = await ReadHeadAsync(client.Stream);
        Assert.AreEqual(400, status);
        Assert.AreEqual(0, proxy.Active);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SecondTunnelFromTheSameClient_Is429_AndIdleClosesTheFirst()
    {
        using var echo = new UdpEcho();
        using var proxy = Start(options =>
        {
            options.AllowHttp11Upgrade = true;
            options.RequireAuth = false;
            options.BlockPrivateTargets = false;
            options.AllowedPorts = [echo.Port];
            options.MaxTunnelsPerClient = 1;
            options.IdleTimeout = TimeSpan.FromMilliseconds(200);
        });

        using var first = await ConnectAsync(proxy.Port, http11: true);
        await first.Stream.WriteAsync(RequestBytes(echo.Port, basic: null, contentLength: null));
        var (opened, _, _) = await ReadHeadAsync(first.Stream);
        Assert.AreEqual(101, opened);

        using var second = await ConnectAsync(proxy.Port, http11: true);
        await second.Stream.WriteAsync(RequestBytes(echo.Port, basic: null, contentLength: null));
        var (limited, _, _) = await ReadHeadAsync(second.Stream);
        Assert.AreEqual(429, limited);
        Assert.AreEqual(1, proxy.Active);

        await WaitUntilAsync(() => proxy.Active == 0);
    }

    private static Session Start(Action<MasqueOptions> configure)
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
        return new Session(proxy, endPoint);
    }

    private static async Task<Client> ConnectAsync(int port, bool http11)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols = http11
                ? [SslApplicationProtocol.Http11]
                : [SslApplicationProtocol.Http2],
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        });
        return new Client(tcp, ssl);
    }

    private static byte[] RequestBytes(int port, string? basic, int? contentLength, string host = "127.0.0.1")
    {
        var authorization = basic is null ? string.Empty : $"Proxy-Authorization: Basic {basic}\r\n";
        var length = contentLength is null ? string.Empty : $"Content-Length: {contentLength}\r\n";
        var text =
            $"GET /.well-known/masque/udp/{host}/{port}/ HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: connect-udp\r\n" +
            "Capsule-Protocol: ?1\r\n" +
            authorization +
            length +
            "\r\n";
        return Encoding.ASCII.GetBytes(text);
    }

    private static async Task<(int Status, string Headers, byte[] Remainder)> ReadHeadAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            var read = await stream.ReadAsync(one);
            if (read == 0)
            {
                break;
            }

            buffer.Write(one, 0, read);
            var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            var head = text[..split];
            var status = int.Parse(head.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            var rest = new byte[buffer.Length - (split + 4)];
            Buffer.BlockCopy(buffer.GetBuffer(), split + 4, rest, 0, rest.Length);
            return (status, head, rest);
        }

        Assert.Fail("The HTTP/1.1 response did not arrive.");
        return default;
    }

    private static async Task<byte[]> ReadDatagramAsync(Stream stream, byte[] already)
    {
        var reader = new CapsuleReader();
        byte[]? got = null;
        void Take(ReadOnlySpan<byte> span) => got = span.ToArray();
        if (already.Length > 0)
        {
            reader.Feed(already, Take);
        }

        var buffer = new byte[2048];
        var deadline = Environment.TickCount64 + 10_000;
        while (got is null && Environment.TickCount64 < deadline)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            reader.Feed(buffer.AsSpan(0, read), Take);
        }

        Assert.IsNotNull(got);
        return got;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(condition());
    }

    private sealed class Session : IDisposable
    {
        public Session(ProxyServer proxy, MasqueProxyEndPoint listener)
        {
            Proxy = proxy;
            Listener = listener;
        }

        public ProxyServer Proxy { get; }

        private MasqueProxyEndPoint Listener { get; }

        public int Port => Listener.Port;

        public int Active => Listener.Active;

        public void Dispose()
        {
            Proxy.Stop();
            Proxy.Dispose();
        }
    }

    private sealed class Client : IDisposable
    {
        public Client(TcpClient tcp, SslStream stream)
        {
            Tcp = tcp;
            Stream = stream;
        }

        public TcpClient Tcp { get; }

        public SslStream Stream { get; }

        public void Dispose()
        {
            Stream.Dispose();
            Tcp.Dispose();
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
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }
}

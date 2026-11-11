using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat SOCKS5 listener suite: cleartext HTTP must be intercepted (not relayed opaquely),
///     mirroring the SOCKS4 FatGap characterization.
/// </summary>
[TestClass]
public class Socks5EndpointPlainHttpFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: plain HTTP over SOCKS5 is relayed without firing <see cref="ProxyServer.BeforeRequest" />.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Socks5_PlainHttpGet_FiresBeforeRequest()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("socks5-http"));

        using var proxy = BuildSocks5Proxy();
        var saw = false;
        proxy.BeforeRequest += (_, _) =>
        {
            saw = true;
            return Task.CompletedTask;
        };

        var socksPort = proxy.ProxyEndPoints[0].Port;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, socksPort);
        await using var stream = tcp.GetStream();
        await Socks5HandshakeAsync(stream, IPAddress.Loopback, server.HttpListeningPort);

        var request = Encoding.ASCII.GetBytes(
            $"GET /gap-s5 HTTP/1.1\r\nHost: localhost:{server.HttpListeningPort}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        var response = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        var text = Encoding.ASCII.GetString(response);
        Assert.IsTrue(saw);
        Assert.IsTrue(text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "socks5-http");
    }

    /// <summary>
    ///     Bug class: a second HTTP GET on the same SOCKS5 tunnel drops interception state.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Socks5_TwoHttpGets_BothIntercepted()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            hits++;
            return ctx.Response.WriteAsync("hit-" + hits);
        });

        using var proxy = BuildSocks5Proxy();
        var beforeCount = 0;
        proxy.BeforeRequest += (_, _) =>
        {
            beforeCount++;
            return Task.CompletedTask;
        };

        var socksPort = proxy.ProxyEndPoints[0].Port;

        for (var i = 1; i <= 2; i++)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, socksPort);
            await using var stream = tcp.GetStream();
            await Socks5HandshakeAsync(stream, IPAddress.Loopback, server.HttpListeningPort);
            var request = Encoding.ASCII.GetBytes(
                $"GET /{i} HTTP/1.1\r\nHost: localhost:{server.HttpListeningPort}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request);
            var response = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
            var text = Encoding.ASCII.GetString(response);
            Assert.IsTrue(text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), text);
            StringAssert.Contains(text, "hit-" + i);
        }

        Assert.AreEqual(2, hits);
        Assert.AreEqual(2, beforeCount);
    }

    private static ProxyServer BuildSocks5Proxy()
    {
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        return proxy;
    }

    private static async Task Socks5HandshakeAsync(NetworkStream stream, IPAddress address, int port)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x00, methodResponse[1]);

        var addrBytes = address.GetAddressBytes();
        var request = new byte[4 + addrBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x01;
        addrBytes.CopyTo(request, 4);
        request[4 + addrBytes.Length] = (byte)(port >> 8);
        request[4 + addrBytes.Length + 1] = (byte)port;
        await stream.WriteAsync(request);

        var replyHeader = new byte[4];
        await ReadExactAsync(stream, replyHeader);
        Assert.AreEqual(0x00, replyHeader[1]);
        await DrainSocks5BoundAddressAsync(stream, replyHeader[3]);
    }

    private static async Task DrainSocks5BoundAddressAsync(NetworkStream stream, byte atyp)
    {
        var addrLen = atyp switch
        {
            0x01 => 4,
            0x03 => await ReadOneByte(stream),
            0x04 => 16,
            _ => 0,
        };
        var drain = new byte[addrLen + 2];
        if (drain.Length > 0)
            await ReadExactAsync(stream, drain);
    }

    private static async Task<int> ReadOneByte(NetworkStream stream)
    {
        var b = new byte[1];
        await ReadExactAsync(stream, b);
        return b[0];
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
            read += await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
    }

    private static async Task<byte[]> ReadAllAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            try
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n <= 0) break;
                ms.Write(buffer, 0, n);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return ms.ToArray();
    }

}

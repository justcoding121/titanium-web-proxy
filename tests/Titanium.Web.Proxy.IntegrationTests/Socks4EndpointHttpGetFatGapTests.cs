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
///     Fat SOCKS4 listener suite: cleartext HTTP must still be intercepted (not relayed opaquely).
/// </summary>
[TestClass]
public class Socks4EndpointHttpGetFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: plain HTTP over SOCKS4 is relayed without firing <see cref="ProxyServer.BeforeRequest" />.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Socks4_PlainHttpGet_FiresBeforeRequest()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("socks4-http");
        });

        using var proxy = BuildSocks4Proxy();
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
        await Socks4HandshakeAsync(stream, IPAddress.Loopback, server.HttpListeningPort);

        var request = Encoding.ASCII.GetBytes(
            $"GET /gap HTTP/1.1\r\nHost: localhost:{server.HttpListeningPort}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        var response = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        var text = Encoding.ASCII.GetString(response);
        Assert.IsTrue(saw);
        Assert.IsTrue(text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "socks4-http");
    }

    /// <summary>
    ///     Bug class: a second HTTP GET on the same SOCKS4 tunnel drops interception state.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task Socks4_TwoHttpGets_BothIntercepted()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            hits++;
            return ctx.Response.WriteAsync("n=" + hits);
        });

        using var proxy = BuildSocks4Proxy();
        var socksPort = proxy.ProxyEndPoints[0].Port;

        for (var i = 1; i <= 2; i++)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, socksPort);
            await using var stream = tcp.GetStream();
            await Socks4HandshakeAsync(stream, IPAddress.Loopback, server.HttpListeningPort);
            var request = Encoding.ASCII.GetBytes(
                $"GET /{i} HTTP/1.1\r\nHost: localhost:{server.HttpListeningPort}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request);
            var response = await ReadAllAsync(stream, TimeSpan.FromSeconds(8));
            StringAssert.Contains(Encoding.ASCII.GetString(response), "n=" + i);
        }

        Assert.AreEqual(2, hits);
    }

    private static ProxyServer BuildSocks4Proxy()
    {
        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        proxy.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        return proxy;
    }

    private static async Task Socks4HandshakeAsync(NetworkStream stream, IPAddress address, int port)
    {
        var packet = new byte[9];
        packet[0] = 0x04;
        packet[1] = 0x01;
        packet[2] = (byte)(port >> 8);
        packet[3] = (byte)port;
        var bytes = address.GetAddressBytes();
        packet[4] = bytes[0];
        packet[5] = bytes[1];
        packet[6] = bytes[2];
        packet[7] = bytes[3];
        packet[8] = 0;
        await stream.WriteAsync(packet);
        var reply = new byte[8];
        var read = 0;
        while (read < 8)
            read += await stream.ReadAsync(reply.AsMemory(read, 8 - read));
        Assert.AreEqual(0x5a, reply[1], "SOCKS4 granted reply expected.");
    }

    private static async Task<byte[]> ReadAllAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        using var ms = new System.IO.MemoryStream();
        while (true)
        {
            try
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n <= 0) break;
                ms.Write(buffer, 0, n);
            }
            catch (System.OperationCanceledException)
            {
                break;
            }
        }

        return ms.ToArray();
    }
}

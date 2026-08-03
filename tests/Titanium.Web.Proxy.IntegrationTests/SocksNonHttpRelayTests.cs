using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     End-to-end tests verifying that non-HTTP, non-TLS traffic arriving on a
///     <see cref="SocksProxyEndPoint" /> is relayed opaquely to the SOCKS destination
///     rather than failing with a parse error.
/// </summary>
[DoNotParallelize]
[TestClass]
public class SocksNonHttpRelayTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    // All bytes are below 0x80 so PeekClientHello's SSL-2 heuristic (high-bit test) is not
    // triggered, which would otherwise block waiting for more header bytes.
    private static readonly byte[] BinaryPayload = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

    /// <summary>
    ///     SOCKS5 with domain-name address type: non-HTTP binary traffic sent from client reaches
    ///     the raw TCP server and the server's echo arrives back at the client unchanged.
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_DomainName_NonHttpBinaryTraffic_IsRelayedOpaquely()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();

        // Echo server: read all bytes sent by the client, write them back verbatim, then close.
        // Closing the server side first lets SendRaw's receive-relay direction complete naturally,
        // which triggers cancellation of the send-relay (rather than the reverse).
        server.HandleTcpRequest(async context =>
        {
            var result = await context.Transport.Input.ReadAsync();
            if (!result.Buffer.IsEmpty)
                foreach (var segment in result.Buffer)
                    await context.Transport.Output.WriteAsync(segment.ToArray());

            context.Transport.Input.AdvanceTo(result.Buffer.End);
            context.Transport.Output.Complete();
        });

        using var proxyServer = BuildSocksProxy();

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        var targetPort = server.TcpListeningPort;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks5HandshakeAsync(stream, "localhost", targetPort);

        await stream.WriteAsync(BinaryPayload);

        // Do not shut down send side here � let the server-close trigger relay teardown.
        // The echo server reads, echoes and closes its write side; the proxy relay's
        // server?client direction completes first, cancels client?server, and the
        // client can then read the full echo before getting EOF.
        var echoed = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(BinaryPayload, echoed,
            "Non-HTTP binary payload must be echoed back unchanged through the SOCKS relay.");
    }

    /// <summary>
    ///     SOCKS5 with IPv4 address type: non-HTTP binary traffic is relayed opaquely.
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_IPv4_NonHttpBinaryTraffic_IsRelayedOpaquely()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            var result = await context.Transport.Input.ReadAsync();
            if (!result.Buffer.IsEmpty)
                foreach (var segment in result.Buffer)
                    await context.Transport.Output.WriteAsync(segment.ToArray());

            context.Transport.Input.AdvanceTo(result.Buffer.End);
            context.Transport.Output.Complete();
        });

        using var proxyServer = BuildSocksProxy();

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        var targetPort = server.TcpListeningPort;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks5HandshakeAsync(stream, IPAddress.Loopback, targetPort);

        await stream.WriteAsync(BinaryPayload);

        var echoed = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(BinaryPayload, echoed,
            "Non-HTTP binary payload must be echoed back unchanged through the SOCKS relay.");
    }

    /// <summary>
    ///     SOCKS4 with IPv4 address type: non-HTTP binary traffic is relayed opaquely.
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks4_IPv4_NonHttpBinaryTraffic_IsRelayedOpaquely()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            var result = await context.Transport.Input.ReadAsync();
            if (!result.Buffer.IsEmpty)
                foreach (var segment in result.Buffer)
                    await context.Transport.Output.WriteAsync(segment.ToArray());

            context.Transport.Input.AdvanceTo(result.Buffer.End);
            context.Transport.Output.Complete();
        });

        using var proxyServer = BuildSocksProxy();

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        var targetPort = server.TcpListeningPort;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks4HandshakeAsync(stream, IPAddress.Loopback, targetPort);

        await stream.WriteAsync(BinaryPayload);

        var echoed = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(BinaryPayload, echoed,
            "Non-HTTP binary payload must be echoed back unchanged through the SOCKS relay.");
    }

    /// <summary>
    ///     Verify that normal plain HTTP traffic on a SOCKS endpoint is still intercepted
    ///     (not accidentally caught by the non-HTTP relay fallback).
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_PlainHttpTraffic_IsIntercepted()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();

        server.HandleRequest(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("ok");
        });

        using var proxyServer = BuildSocksProxy();

        var intercepted = false;
        proxyServer.BeforeRequest += (_, e) =>
        {
            intercepted = true;
            return Task.CompletedTask;
        };

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        var targetPort = server.HttpListeningPort;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks5HandshakeAsync(stream, "localhost", targetPort);

        var request = Encoding.ASCII.GetBytes(
            $"GET / HTTP/1.1\r\nHost: localhost:{targetPort}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        var response = await ReadAllAsync(stream, TimeSpan.FromSeconds(10));
        var responseText = Encoding.ASCII.GetString(response);

        Assert.IsTrue(intercepted, "BeforeRequest must fire for plain HTTP over SOCKS.");
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
            $"Expected HTTP 200 response from server. Got: {responseText}");
    }

    // Helpers

    private static ProxyServer BuildSocksProxy()
    {
        var proxyServer = new ProxyServer(false, false, false);
        proxyServer.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxyServer.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        // decryptSsl: false so TLS traffic is also relayed opaquely; the focus here is non-HTTP plain traffic.
        proxyServer.AddEndPoint(new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxyServer.Start();
        return proxyServer;
    }

    /// <summary>Performs a SOCKS5 no-auth handshake to a domain-name target.</summary>
    private static async Task Socks5HandshakeAsync(NetworkStream stream, string host, int port)
    {
        // Greeting: VER=5, NMETHODS=1, METHOD=0 (no auth)
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x05, methodResponse[0], "SOCKS5 version mismatch in method response.");
        Assert.AreEqual(0x00, methodResponse[1], "SOCKS5 server did not accept no-auth method.");

        // Request: VER=5, CMD=CONNECT, RSV=0, ATYP=3 (domain), host, port
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[5 + hostBytes.Length + 2];
        request[0] = 0x05; // VER
        request[1] = 0x01; // CMD = CONNECT
        request[2] = 0x00; // RSV
        request[3] = 0x03; // ATYP = domain
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[5 + hostBytes.Length] = (byte)(port >> 8);
        request[5 + hostBytes.Length + 1] = (byte)port;
        await stream.WriteAsync(request);

        // Reply: VER=5, REP=0 (success), RSV=0, then bound address/port
        var replyHeader = new byte[4];
        await ReadExactAsync(stream, replyHeader);
        Assert.AreEqual(0x05, replyHeader[0], "SOCKS5 version mismatch in connect reply.");
        Assert.AreEqual(0x00, replyHeader[1], "SOCKS5 CONNECT was not granted.");

        // Drain the bound address/port bytes from the reply
        await DrainSocks5BoundAddressAsync(stream, replyHeader[3]);
    }

    /// <summary>Performs a SOCKS5 no-auth handshake to an IPv4 target.</summary>
    private static async Task Socks5HandshakeAsync(NetworkStream stream, IPAddress address, int port)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x05, methodResponse[0]);
        Assert.AreEqual(0x00, methodResponse[1]);

        var addrBytes = address.GetAddressBytes(); // 4 bytes for IPv4
        var request = new byte[4 + addrBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x01; // ATYP = IPv4
        addrBytes.CopyTo(request, 4);
        request[4 + addrBytes.Length] = (byte)(port >> 8);
        request[4 + addrBytes.Length + 1] = (byte)port;
        await stream.WriteAsync(request);

        var replyHeader = new byte[4];
        await ReadExactAsync(stream, replyHeader);
        Assert.AreEqual(0x05, replyHeader[0]);
        Assert.AreEqual(0x00, replyHeader[1]);
        await DrainSocks5BoundAddressAsync(stream, replyHeader[3]);
    }

    private static async Task DrainSocks5BoundAddressAsync(NetworkStream stream, byte atyp)
    {
        int addrLen = atyp switch
        {
            0x01 => 4,       // IPv4
            0x03 => await ReadOneByte(stream) + 0, // domain: length prefix then name
            0x04 => 16,      // IPv6
            _ => throw new InvalidOperationException($"Unknown ATYP {atyp} in SOCKS5 reply.")
        };

        if (atyp == 0x03)
        {
            // addrLen here is the domain length already read above; drain it
            var domainBuf = new byte[addrLen];
            await ReadExactAsync(stream, domainBuf);
        }
        else
        {
            var addrBuf = new byte[addrLen];
            await ReadExactAsync(stream, addrBuf);
        }

        var portBuf = new byte[2];
        await ReadExactAsync(stream, portBuf);
    }

    private static async Task<int> ReadOneByte(NetworkStream stream)
    {
        var buf = new byte[1];
        await ReadExactAsync(stream, buf);
        return buf[0];
    }

    /// <summary>Performs a SOCKS4 handshake to an IPv4 target.</summary>
    private static async Task Socks4HandshakeAsync(NetworkStream stream, IPAddress address, int port)
    {
        var addrBytes = address.GetAddressBytes();
        // VER=4, CMD=1 (CONNECT), port (2 bytes), IP (4 bytes), NULL userID
        var request = new byte[9];
        request[0] = 0x04;
        request[1] = 0x01;
        request[2] = (byte)(port >> 8);
        request[3] = (byte)port;
        addrBytes.CopyTo(request, 4);
        request[8] = 0x00; // null-terminated userID
        await stream.WriteAsync(request);

        var response = new byte[8];
        await ReadExactAsync(stream, response);
        Assert.AreEqual(0x00, response[0], "SOCKS4 reply VN must be 0.");
        Assert.AreEqual(90, response[1], "SOCKS4 request was not granted (expected reply code 90).");
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var remaining = buffer.Length;
        var offset = 0;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, remaining), cts.Token);
            if (read == 0) throw new IOException("Connection closed before all expected bytes were read.");
            offset += read;
            remaining -= read;
        }
    }

    private static async Task<byte[]> ReadAllAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                ms.Write(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
            // timeout is acceptable if the connection stays open (e.g. no explicit FIN)
        }

        return ms.ToArray();
    }
}

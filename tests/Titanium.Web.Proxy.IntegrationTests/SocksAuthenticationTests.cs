using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class SocksAuthenticationTests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_EndpointAuth_Succeeds_With_Valid_Credentials()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ok"));

        IPEndPoint capturedClient = null;
        SocksProxyEndPoint capturedEndpoint = null;

        using var proxyServer = BuildSocksProxy(endpoint =>
        {
            endpoint.AuthenticateUserFunc = args =>
            {
                capturedClient = args.ClientRemoteEndPoint;
                capturedEndpoint = args.ProxyEndPoint;
                return Task.FromResult(args.UserName == "alice" && args.Password == "secret");
            };
        });

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks5UserPassHandshakeAsync(stream, "alice", "secret", "localhost", server.HttpListeningPort);
        await WriteHttpGetAsync(stream, server.HttpListeningPort);

        var response = Encoding.ASCII.GetString(await ReadAllAsync(stream, TimeSpan.FromSeconds(10)));
        Assert.IsTrue(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), response);
        Assert.IsNotNull(capturedClient);
        Assert.IsNotNull(capturedEndpoint);
        Assert.AreSame(proxyServer.ProxyEndPoints[0], capturedEndpoint);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_EndpointAuth_Rejects_Invalid_Credentials()
    {
        using var proxyServer = BuildSocksProxy(endpoint =>
        {
            endpoint.AuthenticateUserFunc = args =>
                Task.FromResult(args.UserName == "alice" && args.Password == "secret");
        });

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        // Greeting offers user/pass
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 });
        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x02, methodResponse[1]);

        // Wrong password
        await WriteUserPassAsync(stream, "alice", "wrong");
        var authResponse = new byte[2];
        await ReadExactAsync(stream, authResponse);
        Assert.AreEqual(0x01, authResponse[0]);
        Assert.AreEqual(0x01, authResponse[1], "Auth failure status expected.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_EndpointAuth_Takes_Precedence_Over_Global()
    {
        var globalCalled = false;
        var endpointCalled = false;

        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ok"));

        using var proxyServer = BuildSocksProxy(endpoint =>
        {
            endpoint.AuthenticateUserFunc = args =>
            {
                endpointCalled = true;
                return Task.FromResult(true);
            };
        });
        proxyServer.ProxyBasicAuthenticateFunc = (_, _, _) =>
        {
            globalCalled = true;
            return Task.FromResult(false);
        };

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks5UserPassHandshakeAsync(stream, "u", "p", "localhost", server.HttpListeningPort);
        await WriteHttpGetAsync(stream, server.HttpListeningPort);
        var response = Encoding.ASCII.GetString(await ReadAllAsync(stream, TimeSpan.FromSeconds(10)));

        Assert.IsTrue(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), response);
        Assert.IsTrue(endpointCalled);
        Assert.IsFalse(globalCalled, "Global callback must not run when endpoint callback is set.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_Without_Validator_Does_Not_Offer_UserPass_Method()
    {
        using var proxyServer = BuildSocksProxy();

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        // Client offers only username/password
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 });
        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x05, methodResponse[0]);
        Assert.AreEqual(0xFF, methodResponse[1], "Must reject user/pass when no validator is configured.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_Auth_Handles_Fragmented_Packets()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ok"));

        using var proxyServer = BuildSocksProxy(endpoint =>
        {
            endpoint.AuthenticateUserFunc = args =>
                Task.FromResult(args.UserName == "bob" && args.Password == "pw");
        });

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        // Fragment greeting
        await stream.WriteAsync(new byte[] { 0x05 });
        await Task.Delay(20);
        await stream.WriteAsync(new byte[] { 0x01 });
        await Task.Delay(20);
        await stream.WriteAsync(new byte[] { 0x02 });

        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x02, methodResponse[1]);

        // Fragment RFC 1929 credentials: VER ULEN | UNAME | PLEN | PASSWD
        await stream.WriteAsync(new byte[] { 0x01, 0x03 });
        await Task.Delay(20);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("bob"));
        await Task.Delay(20);
        await stream.WriteAsync(new byte[] { 0x02 });
        await Task.Delay(20);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("pw"));

        var authResponse = new byte[2];
        await ReadExactAsync(stream, authResponse);
        Assert.AreEqual(0x00, authResponse[1]);

        // Fragment CONNECT request for domain localhost
        var hostBytes = Encoding.ASCII.GetBytes("localhost");
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x03 });
        await Task.Delay(20);
        await stream.WriteAsync(new[] { (byte)hostBytes.Length });
        await Task.Delay(20);
        await stream.WriteAsync(hostBytes);
        await Task.Delay(20);
        var port = server.HttpListeningPort;
        await stream.WriteAsync(new[] { (byte)(port >> 8), (byte)port });

        var replyHeader = new byte[4];
        await ReadExactAsync(stream, replyHeader);
        Assert.AreEqual(0x00, replyHeader[1]);
        await DrainSocks5BoundAddressAsync(stream, replyHeader[3]);

        await WriteHttpGetAsync(stream, port);
        var response = Encoding.ASCII.GetString(await ReadAllAsync(stream, TimeSpan.FromSeconds(10)));
        Assert.IsTrue(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), response);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks4_Rejected_When_Auth_Configured()
    {
        using var proxyServer = BuildSocksProxy(endpoint =>
        {
            endpoint.AuthenticateUserFunc = _ => Task.FromResult(true);
        });

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        // SOCKS4 CONNECT — should be closed without a grant when auth is required.
        var request = new byte[] { 0x04, 0x01, 0x00, 0x50, 127, 0, 0, 1, 0x00 };
        await stream.WriteAsync(request);

        var buffer = new byte[8];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            if (read > 0)
                Assert.AreNotEqual(90, buffer[1], "SOCKS4 must not be granted when auth is configured.");
        }
        catch (IOException)
        {
            // Connection reset/closed is acceptable rejection.
        }
        catch (OperationCanceledException)
        {
            // Idle close without reply is also acceptable rejection.
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks4_Still_Works_Without_Auth()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleTcpRequest(async context =>
        {
            var result = await context.Transport.Input.ReadAsync();
            context.Transport.Input.AdvanceTo(result.Buffer.End);
            context.Transport.Output.Complete();
        });

        using var proxyServer = BuildSocksProxy();
        var socksPort = proxyServer.ProxyEndPoints[0].Port;

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        var addrBytes = IPAddress.Loopback.GetAddressBytes();
        var port = server.TcpListeningPort;
        var request = new byte[9];
        request[0] = 0x04;
        request[1] = 0x01;
        request[2] = (byte)(port >> 8);
        request[3] = (byte)port;
        addrBytes.CopyTo(request, 4);
        request[8] = 0x00;
        await stream.WriteAsync(request);

        var response = new byte[8];
        await ReadExactAsync(stream, response);
        Assert.AreEqual(90, response[1]);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Socks5_Global_ProxyBasicAuthenticateFunc_Works_For_Socks()
    {
        using var suite = new TestSuite();
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ok"));

        using var proxyServer = BuildSocksProxy();
        proxyServer.ProxyBasicAuthenticateFunc = (_, user, pass) =>
            Task.FromResult(user == "g" && pass == "p");

        var socksPort = proxyServer.ProxyEndPoints[0].Port;
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort);
        var stream = tcpClient.GetStream();

        await Socks5UserPassHandshakeAsync(stream, "g", "p", "localhost", server.HttpListeningPort);
        await WriteHttpGetAsync(stream, server.HttpListeningPort);
        var response = Encoding.ASCII.GetString(await ReadAllAsync(stream, TimeSpan.FromSeconds(10)));
        Assert.IsTrue(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), response);
    }

    private static ProxyServer BuildSocksProxy(Action<SocksProxyEndPoint> configure = null)
    {
        var proxyServer = new ProxyServer(false, false, false);
        proxyServer.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxyServer.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        var endpoint = new SocksProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        configure?.Invoke(endpoint);
        proxyServer.AddEndPoint(endpoint);
        proxyServer.Start();
        return proxyServer;
    }

    private static async Task Socks5UserPassHandshakeAsync(NetworkStream stream, string user, string pass,
        string host, int port)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x02 });
        var methodResponse = new byte[2];
        await ReadExactAsync(stream, methodResponse);
        Assert.AreEqual(0x02, methodResponse[1], "Expected username/password method.");

        await WriteUserPassAsync(stream, user, pass);
        var authResponse = new byte[2];
        await ReadExactAsync(stream, authResponse);
        Assert.AreEqual(0x00, authResponse[1], "SOCKS5 authentication failed.");

        var hostBytes = Encoding.ASCII.GetBytes(host);
        var request = new byte[5 + hostBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x03;
        request[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, 5);
        request[5 + hostBytes.Length] = (byte)(port >> 8);
        request[5 + hostBytes.Length + 1] = (byte)port;
        await stream.WriteAsync(request);

        var replyHeader = new byte[4];
        await ReadExactAsync(stream, replyHeader);
        Assert.AreEqual(0x00, replyHeader[1]);
        await DrainSocks5BoundAddressAsync(stream, replyHeader[3]);
    }

    private static async Task WriteUserPassAsync(NetworkStream stream, string user, string pass)
    {
        var userBytes = Encoding.ASCII.GetBytes(user);
        var passBytes = Encoding.ASCII.GetBytes(pass);
        var packet = new byte[3 + userBytes.Length + passBytes.Length];
        packet[0] = 0x01;
        packet[1] = (byte)userBytes.Length;
        userBytes.CopyTo(packet, 2);
        packet[2 + userBytes.Length] = (byte)passBytes.Length;
        passBytes.CopyTo(packet, 3 + userBytes.Length);
        await stream.WriteAsync(packet);
    }

    private static async Task WriteHttpGetAsync(NetworkStream stream, int port)
    {
        var request = Encoding.ASCII.GetBytes(
            $"GET / HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
    }

    private static async Task DrainSocks5BoundAddressAsync(NetworkStream stream, byte atyp)
    {
        int addrLen = atyp switch
        {
            0x01 => 4,
            0x03 => await ReadOneByte(stream),
            0x04 => 16,
            _ => throw new InvalidOperationException($"Unknown ATYP {atyp}")
        };

        var addrBuf = new byte[addrLen];
        await ReadExactAsync(stream, addrBuf);
        var portBuf = new byte[2];
        await ReadExactAsync(stream, portBuf);
    }

    private static async Task<int> ReadOneByte(NetworkStream stream)
    {
        var buf = new byte[1];
        await ReadExactAsync(stream, buf);
        return buf[0];
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var remaining = buffer.Length;
        var offset = 0;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer, offset, remaining, cts.Token);
            if (read == 0) throw new IOException("Connection closed early.");
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
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                ms.Write(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
        }

        return ms.ToArray();
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
/// The client keep-alive loop keeps the client socket when a synthetic response asked to close
/// only the origin socket, and still drops the client when a non-synthetic read is short.
/// </summary>
[TestClass]
[DoNotParallelize]
public class SyntheticKeepAliveClientTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task UserRespond_CloseServerConnection_KeepsClientAndDropsOriginSocket()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originConnections = new ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            originConnections.Add(context.Connection.Id);
            if (context.Request.Path.Value?.Contains("replace", StringComparison.Ordinal) == true)
            {
                var payload = new string('Z', 8000);
                context.Response.ContentLength = payload.Length;
                return context.Response.WriteAsync(payload);
            }

            context.Response.ContentLength = "next-ok".Length;
            return context.Response.WriteAsync("next-ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeResponse += (_, args) =>
        {
            if ((args.HttpClient.Request.Url ?? string.Empty).Contains("replace", StringComparison.Ordinal))
            {
                var body = Encoding.UTF8.GetBytes("synthetic");
                var response = new Response(body)
                {
                    StatusCode = 200,
                    StatusDescription = "OK",
                    HttpVersion = new Version(1, 1)
                };
                args.Respond(response, closeServerConnection: true);
            }

            return Task.CompletedTask;
        };

        var origin = new Uri(server.ListeningHttpUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        using var stream = client.GetStream();

        var first = await ExchangeAsync(stream, Request(origin, "/replace"));
        var second = await ExchangeAsync(stream, Request(origin, "/next"));

        StringAssert.Contains(first, "synthetic");
        StringAssert.Contains(second, "next-ok");
        Assert.IsFalse(second.Contains("ZZZZ", StringComparison.Ordinal), second);
        Assert.IsTrue(originConnections.Count >= 2, "both requests must reach the origin");
        Assert.AreEqual(2, new System.Collections.Generic.HashSet<string>(originConnections).Count,
            "the unread origin socket must not be reused from the pool");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeRequest_PostBody_CloseServerConnection_DrainsAndKeepsClient()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originHits = 0;
        server.HandleRequest(context =>
        {
            Interlocked.Increment(ref originHits);
            context.Response.ContentLength = "next-ok".Length;
            return context.Response.WriteAsync("next-ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeRequest += async (_, args) =>
        {
            if (!(args.HttpClient.Request.Url ?? string.Empty).Contains("short", StringComparison.Ordinal))
                return;

            // Drain so keep-alive does not treat the POST body as the next request line.
            await args.GetRequestBody();
            var body = Encoding.UTF8.GetBytes("synthetic");
            var response = new Response(body)
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = new Version(1, 1)
            };
            args.Respond(response, closeServerConnection: true);
        };

        var origin = new Uri(server.ListeningHttpUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        using var stream = client.GetStream();

        var postBody = "payload-bytes";
        var post = $"POST http://{origin.Host}:{origin.Port}/short HTTP/1.1\r\n"
            + $"Host: {origin.Host}:{origin.Port}\r\n"
            + "Connection: keep-alive\r\n"
            + $"Content-Length: {postBody.Length}\r\n\r\n"
            + postBody;
        var first = await ExchangeAsync(stream, post);
        var second = await ExchangeAsync(stream, Request(origin, "/next"));

        StringAssert.Contains(first, "synthetic");
        StringAssert.Contains(second, "next-ok");
        Assert.AreEqual(1, originHits, "the short-circuited POST must not reach the origin");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Synthetic_ConnectionClose_EndsTheClientConnection()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.ContentLength = 2;
            return context.Response.WriteAsync("no");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeResponse += (_, args) =>
        {
            var body = Encoding.UTF8.GetBytes("bye");
            var response = new Response(body)
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = new Version(1, 1)
            };
            response.Headers.AddHeader("Connection", "close");
            args.Respond(response, closeServerConnection: true);
            return Task.CompletedTask;
        };

        var origin = new Uri(server.ListeningHttpUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        using var stream = client.GetStream();

        var first = await ExchangeAsync(stream, Request(origin, "/close"));
        StringAssert.Contains(first, "bye");

        try
        {
            var followUp = await stream.ReadAsync(new byte[32]);
            Assert.AreEqual(0, followUp, "Connection: close on a synthetic response must end the client");
        }
        catch (IOException)
        {
            // A reset is the client connection ending after Connection: close.
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MitmHttps_SyntheticCloseServer_KeepsClientTunnel()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originConnections = new ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            originConnections.Add(context.Connection.Id);
            if (context.Request.Path.Value?.Contains("replace", StringComparison.Ordinal) == true)
            {
                var payload = new string('Z', 4000);
                context.Response.ContentLength = payload.Length;
                return context.Response.WriteAsync(payload);
            }

            context.Response.ContentLength = "next-ok".Length;
            return context.Response.WriteAsync("next-ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = false;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        Assert.IsTrue(endpoint.DecryptSsl, "explicit test endpoint must decrypt HTTPS");
        proxy.BeforeResponse += (_, args) =>
        {
            if ((args.HttpClient.Request.Url ?? string.Empty).Contains("replace", StringComparison.Ordinal))
            {
                var body = Encoding.UTF8.GetBytes("synthetic");
                var response = new Response(body)
                {
                    StatusCode = 200,
                    StatusDescription = "OK",
                    HttpVersion = new Version(1, 1)
                };
                args.Respond(response, closeServerConnection: true);
            }

            return Task.CompletedTask;
        };

        var origin = new Uri(server.ListeningHttpsUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var tcp = client.GetStream();
        var connect = Encoding.ASCII.GetBytes(
            $"CONNECT {origin.Host}:{origin.Port} HTTP/1.1\r\nHost: {origin.Host}:{origin.Port}\r\n\r\n");
        await tcp.WriteAsync(connect);
        var connectBuf = new byte[512];
        var connectRead = await tcp.ReadAsync(connectBuf);
        var connectText = Encoding.ASCII.GetString(connectBuf, 0, connectRead);
        Assert.IsTrue(connectText.Contains("200", StringComparison.Ordinal), connectText);

        await using var ssl = new SslStream(tcp, false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(origin.Host);

        var first = await ExchangeAsync(ssl, $"GET /replace HTTP/1.1\r\nHost: {origin.Host}:{origin.Port}\r\nConnection: keep-alive\r\n\r\n");
        var second = await ExchangeAsync(ssl, $"GET /next HTTP/1.1\r\nHost: {origin.Host}:{origin.Port}\r\nConnection: keep-alive\r\n\r\n");

        StringAssert.Contains(first, "synthetic");
        StringAssert.Contains(second, "next-ok");
        Assert.IsFalse(second.Contains("ZZZZ", StringComparison.Ordinal), second);
        Assert.AreEqual(2, new System.Collections.Generic.HashSet<string>(originConnections).Count,
            "the unread origin socket must not be reused from the pool");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NonSyntheticShortRead_ClosesTheClientConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var originPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeShortBodyAsync(listener);

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = false;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{originPort}/short HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\nConnection: keep-alive\r\n\r\n");
        await stream.WriteAsync(request);

        using var readLimit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var received = await ReadUntilClosedAsync(stream, readLimit.Token);
        var text = Encoding.ASCII.GetString(received);
        StringAssert.Contains(text, "short");

        try
        {
            var second = Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{originPort}/again HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(second);
            var followUp = await stream.ReadAsync(new byte[32]);
            Assert.AreEqual(0, followUp, "a short origin read must end the client connection");
        }
        catch (IOException)
        {
            // A reset is the client connection ending after the short read.
        }
        await served;
        listener.Stop();
    }

    private static string Request(Uri origin, string path) =>
        $"GET http://{origin.Host}:{origin.Port}{path} HTTP/1.1\r\nHost: {origin.Host}:{origin.Port}\r\nConnection: keep-alive\r\n\r\n";

    private static Task<string> ExchangeAsync(NetworkStream stream, string request) =>
        ExchangeAsync((Stream)stream, request);

    private static async Task<string> ExchangeAsync(Stream stream, string request)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var header = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, limit.Token) == 0)
            {
                throw new IOException("client connection closed before the response headers");
            }

            header.Write(one, 0, 1);
            if (header.Length >= 4)
            {
                var bytes = header.GetBuffer();
                var length = (int)header.Length;
                if (bytes[length - 4] == '\r' && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                {
                    break;
                }
            }
        }

        var headerText = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
        var contentLength = ContentLengthOf(headerText);
        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), limit.Token);
            if (n == 0)
            {
                throw new IOException("client connection closed before the response body");
            }

            read += n;
        }

        return headerText + Encoding.UTF8.GetString(body);
    }

    private static int ContentLengthOf(string headerText)
    {
        foreach (var line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var length))
            {
                return length;
            }
        }

        throw new InvalidOperationException("response has no Content-Length: " + headerText);
    }

    private static async Task ServeShortBodyAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var request = new byte[4096];
        var got = 0;
        while (got < request.Length)
        {
            var n = await stream.ReadAsync(request.AsMemory(got, request.Length - got));
            if (n == 0)
            {
                break;
            }

            got += n;
            if (got >= 4
                && request[got - 4] == '\r'
                && request[got - 3] == '\n'
                && request[got - 2] == '\r'
                && request[got - 1] == '\n')
            {
                break;
            }
        }
        var payload = "HTTP/1.1 200 OK\r\nContent-Length: 40\r\nConnection: keep-alive\r\n\r\nshort"u8.ToArray();
        await stream.WriteAsync(payload);
    }

    private static async Task<byte[]> ReadUntilClosedAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[256];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(chunk, cancellationToken);
                if (n == 0)
                {
                    return buffer.ToArray();
                }

                buffer.Write(chunk, 0, n);
            }
        }
        catch (IOException)
        {
            return buffer.ToArray();
        }
    }
}

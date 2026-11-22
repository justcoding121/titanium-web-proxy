using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Setup;

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

    private static async Task<string> ExchangeAsync(NetworkStream stream, string request)
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

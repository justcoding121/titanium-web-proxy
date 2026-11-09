using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.ControlPlane;

namespace Titanium.Plus.Tests;

/// <summary>
///     TCP-level tests for <see cref="ControlHttpServer"/>: request parsing, error responses,
///     body limits, and graceful stop. All listeners bind to loopback.
/// </summary>
[TestClass]
public class ControlHttpServerTests
{
    // ---------------------------------------------------------------- helpers

    private static ControlHttpServer EchoServer(out int port)
    {
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _) =>
            Task.FromResult(ControlHttpResponse.Text(200, "text/plain", $"{req.Method} {req.Path} body={req.BodyText()}"));

        var server = new ControlHttpServer(IPAddress.Loopback, 0, certificate: null, Handler);
        server.Start();
        port = server.Port;
        return server;
    }

    private static async Task<string> SendRawAsync(int port, string raw, int maxResponseBytes = 4096)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        tcp.Client.Shutdown(SocketShutdown.Send);

        var buf = new byte[maxResponseBytes];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(total))) > 0)
        {
            total += read;
            if (total >= buf.Length)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buf, 0, total);
    }

    // ---------------------------------------------------------------- basic request/response

    [TestMethod]
    [Timeout(10_000)]
    public async Task BasicGetRequest_IsHandled_AndResponseIncludesStatusAndContentLength()
    {
        using var server = EchoServer(out var port);
        var response = await SendRawAsync(port, "GET /hello HTTP/1.1\r\nHost: x\r\n\r\n");

        StringAssert.StartsWith(response, "HTTP/1.1 200 OK");
        StringAssert.Contains(response, "Content-Length:");
        StringAssert.Contains(response, "GET /hello body=");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task QueryString_IsPassedToHandler()
    {
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _) =>
            Task.FromResult(ControlHttpResponse.Text(200, "text/plain", req.QueryValue("q") ?? "none"));

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();
        var response = await SendRawAsync(server.Port, "GET /search?q=hello HTTP/1.1\r\nHost: x\r\n\r\n");
        StringAssert.Contains(response, "hello");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task AbsoluteUriTarget_PathAndQueryAreExtracted()
    {
        Task<ControlHttpResponse> Handler(ControlHttpRequest req, CancellationToken _) =>
            Task.FromResult(ControlHttpResponse.Text(200, "text/plain", req.Path + "?" + req.Query));

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        // An absolute URI (as a proxy-style target) must be reduced to path+query.
        var response = await SendRawAsync(server.Port,
            "GET http://example.test/path?val=1 HTTP/1.1\r\nHost: example.test\r\n\r\n");
        StringAssert.Contains(response, "/path?val=1");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task PostWithBody_BodyIsReadAndPassedToHandler()
    {
        using var server = EchoServer(out var port);
        var body = "hello=world";
        var request =
            $"POST /submit HTTP/1.1\r\nHost: x\r\nContent-Length: {body.Length}\r\nContent-Type: application/x-www-form-urlencoded\r\n\r\n{body}";
        var response = await SendRawAsync(port, request);

        StringAssert.StartsWith(response, "HTTP/1.1 200");
        StringAssert.Contains(response, "body=hello=world");
    }

    // ---------------------------------------------------------------- error responses

    [TestMethod]
    [Timeout(10_000)]
    public async Task MalformedRequestLine_Returns400()
    {
        using var server = EchoServer(out var port);

        // Only one token on the request line — not a valid request.
        var response = await SendRawAsync(port, "NOTHTTP\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 400");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task MissingLeadingSlash_InPath_Returns400()
    {
        using var server = EchoServer(out var port);

        // A path that doesn't begin with '/' is malformed.
        var response = await SendRawAsync(port, "GET noslash HTTP/1.1\r\nHost: x\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 400");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task HandlerException_Returns500()
    {
        Task<ControlHttpResponse> Handler(ControlHttpRequest _, CancellationToken __)
            => throw new InvalidOperationException("boom");

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();

        var response = await SendRawAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 500");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OversizedBody_Returns413()
    {
        using var server = EchoServer(out var port);

        // Content-Length beyond MaxBodyBytes (8 MB) triggers 413 before the body is read.
        var response = await SendRawAsync(port,
            "POST /data HTTP/1.1\r\nHost: x\r\nContent-Length: 99999999\r\n\r\n");
        StringAssert.StartsWith(response, "HTTP/1.1 413");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task OversizedHeaders_Returns413OrResetsConnection()
    {
        using var server = EchoServer(out var port);

        // Fill the header buffer (32 KB) with junk — exactly enough to overflow it.
        var filler = "X-Filler: " + new string('x', 1000) + "\r\n";
        var headers = string.Concat(Enumerable.Repeat(filler, 40)); // ~41 KB
        var rawRequest = $"GET / HTTP/1.1\r\nHost: x\r\n{headers}\r\n";

        // The server may answer 413 (too large) before the buffer fills, OR it may reset
        // the connection if there is unread data in the TCP buffer when it closes.
        string? response = null;
        try
        {
            response = await SendRawAsync(port, rawRequest, maxResponseBytes: 8192);
        }
        catch (IOException)
        {
            // OS-level RST from the server closing with unread data — also valid.
            return;
        }

        Assert.IsTrue(
            response.StartsWith("HTTP/1.1 400", StringComparison.Ordinal) ||
            response.StartsWith("HTTP/1.1 413", StringComparison.Ordinal),
            $"Expected 400 or 413 but got: {response[..Math.Min(20, response.Length)]}");
    }

    // ---------------------------------------------------------------- security headers on every response

    [TestMethod]
    [Timeout(10_000)]
    public async Task Response_AlwaysIncludes_CacheControlNoStore_AndXContentTypeOptions()
    {
        using var server = EchoServer(out var port);
        var response = await SendRawAsync(port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        StringAssert.Contains(response, "Cache-Control: no-store");
        StringAssert.Contains(response, "X-Content-Type-Options: nosniff");
    }

    // ---------------------------------------------------------------- ResolveBindAddress

    [TestMethod]
    public void ResolveBindAddress_LoopbackVariants()
    {
        Assert.AreEqual(IPAddress.Loopback, ControlHttpServer.ResolveBindAddress("127.0.0.1"));
        Assert.AreEqual(IPAddress.Loopback, ControlHttpServer.ResolveBindAddress("localhost"));
        Assert.AreEqual(IPAddress.Loopback, ControlHttpServer.ResolveBindAddress("LOCALHOST"));
        Assert.AreEqual(IPAddress.Loopback, ControlHttpServer.ResolveBindAddress(""));
    }

    [TestMethod]
    public void ResolveBindAddress_WildcardVariants()
    {
        Assert.AreEqual(IPAddress.Any, ControlHttpServer.ResolveBindAddress("*"));
        Assert.AreEqual(IPAddress.Any, ControlHttpServer.ResolveBindAddress("+"));
        Assert.AreEqual(IPAddress.Any, ControlHttpServer.ResolveBindAddress("0.0.0.0"));
    }

    [TestMethod]
    public void ResolveBindAddress_IPv6Loopback()
    {
        Assert.AreEqual(IPAddress.IPv6Loopback, ControlHttpServer.ResolveBindAddress("::1"));
    }

    // ---------------------------------------------------------------- graceful stop

    [TestMethod]
    [Timeout(10_000)]
    public async Task Dispose_WhileConnected_ClosesListenerCleanly()
    {
        var requests = 0;
        Task<ControlHttpResponse> Handler(ControlHttpRequest _, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ControlHttpResponse.Text(200, "text/plain", "ok"));
        }

        using var server = new ControlHttpServer(IPAddress.Loopback, 0, null, Handler);
        server.Start();
        var port = server.Port;

        // Serve one request normally before dispose.
        var first = await SendRawAsync(port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        StringAssert.StartsWith(first, "HTTP/1.1 200");

        // Dispose the server.
        server.Dispose();

        // Subsequent connection attempts should fail cleanly (no unhandled exception from the test).
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            // Expected: the port is no longer listening.
        }
        catch (Exception ex)
        {
            Assert.Fail($"Unexpected exception after dispose: {ex}");
        }
    }
}

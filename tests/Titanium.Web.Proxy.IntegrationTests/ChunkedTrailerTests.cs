using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/1.1 chunked trailer handling (RFC 9110 �6.5 / RFC 9112 �7.1.2).
///     Previously the proxy relayed a syntactically valid, trailer-less terminator ("0\r\n\r\n") to the
///     client regardless of whether the upstream message actually carried trailers, silently dropping them.
///     These tests assert the corrected behavior: trailers are forwarded byte-for-byte, and - critically -
///     the source connection is always fully drained through the terminating blank line so a pooled
///     connection is never left in a corrupt state for the next message, whether or not the caller cares
///     about the trailer's contents.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ChunkedTrailerTests
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

    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    public async Task Chunked_Response_Trailer_Is_Forwarded_To_Client()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var response = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n" +
                "5\r\nhello\r\n" +
                "0\r\n" +
                "X-Trailer: trailer-value\r\n" +
                "\r\n");

            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("hello", body);

        Assert.IsTrue(response.TrailingHeaders.Contains("X-Trailer"),
            "The trailer header should now be forwarded to the client.");
        Assert.AreEqual("trailer-value", response.TrailingHeaders.GetValues("X-Trailer").Single());
    }

    [TestMethod]
    public async Task Chunked_Response_With_Multiple_Trailers_Are_All_Forwarded()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var response = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n" +
                "5\r\nhello\r\n" +
                "0\r\n" +
                "X-Checksum: abc123\r\n" +
                "X-Digest: def456\r\n" +
                "\r\n");

            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("hello", body);
        Assert.AreEqual("abc123", response.TrailingHeaders.GetValues("X-Checksum").Single());
        Assert.AreEqual("def456", response.TrailingHeaders.GetValues("X-Digest").Single());
    }

    [TestMethod]
    public async Task Chunked_Response_Without_Trailers_Still_Relays_Body_And_Terminator_Correctly()
    {
        // Regression guard for the common case (no trailers at all): the terminating blank line must
        // still be written so chunked framing stays valid even when there is nothing to forward.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var response = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n" +
                "5\r\nhello\r\n" +
                "0\r\n" +
                "\r\n");

            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.GetAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("hello", body);
        Assert.IsFalse(response.TrailingHeaders.GetEnumerator().MoveNext());
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Chunked_Request_Trailer_Is_Forwarded_To_Server()
    {
        // Mirrors the response-side test above but for the client -> proxy -> server direction, which goes
        // through the same HttpStream.HandleBodyWrite path. HttpClient has no public API for setting request
        // trailers, so a raw socket is used to send a hand-built chunked request with a trailer.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        var receivedRequestText = string.Empty;
        var trailerReceivedSignal = new SemaphoreSlim(0, 1);

        server.HandleTcpRequest(async context =>
        {
            while (!(receivedRequestText.Contains("\r\n0\r\n", StringComparison.Ordinal) &&
                     receivedRequestText.EndsWith("\r\n\r\n", StringComparison.Ordinal)))
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer) receivedRequestText += MsgEncoding.GetString(seg.Span);
                context.Transport.Input.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted) break;
            }

            trailerReceivedSignal.Release();

            var response = MsgEncoding.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        var request = MsgEncoding.GetBytes(
            "POST / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            "5\r\nhello\r\n" +
            "0\r\n" +
            "X-Trailer: req-trailer-value\r\n" +
            "\r\n");
        await stream.WriteAsync(request);

        Assert.IsTrue(await trailerReceivedSignal.WaitAsync(TimeSpan.FromSeconds(20)),
            "The server never observed the request's terminating blank line.");

        using var responseReader = new System.IO.StreamReader(stream, MsgEncoding);
        var statusLine = await responseReader.ReadLineAsync();
        Assert.AreEqual("HTTP/1.1 200 OK", statusLine);

        Assert.IsTrue(receivedRequestText.Contains("X-Trailer: req-trailer-value", StringComparison.Ordinal),
            "The request trailer should have been forwarded to the server.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Chunked_Response_Trailer_Fully_Drained_Allows_Pooled_Connection_Reuse()
    {
        // Proves the fix drains all the way through the trailer's terminating blank line (not just the
        // zero-length chunk) by reusing the SAME raw upstream TCP connection for two sequential requests:
        // if any trailer bytes were left unconsumed on the wire, the second request's response would be
        // parsed starting mid-trailer and the test would fail well before reaching the final assertion.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        var connectionCount = 0;

        server.HandleTcpRequest(async context =>
        {
            Interlocked.Increment(ref connectionCount);

            for (var i = 0; i < 2; i++)
            {
                var requestText = string.Empty;
                while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var result = await context.Transport.Input.ReadAsync();
                    foreach (var seg in result.Buffer) requestText += MsgEncoding.GetString(seg.Span);
                    context.Transport.Input.AdvanceTo(result.Buffer.End);
                }

                var chunkBody = "hello" + new string('!', i);
                var response = MsgEncoding.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Transfer-Encoding: chunked\r\n" +
                    "\r\n" +
                    $"{chunkBody.Length:x}\r\n{chunkBody}\r\n" +
                    "0\r\n" +
                    $"X-Response-Index: {i}\r\n" +
                    "\r\n");

                await context.Transport.Output.WriteAsync(response);
            }

            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        var proxyUrl = new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}");

        var firstResponse = await client.GetAsync(proxyUrl);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        Assert.AreEqual("hello", firstBody);
        Assert.AreEqual("0", firstResponse.TrailingHeaders.GetValues("X-Response-Index").Single());

        var secondResponse = await client.GetAsync(proxyUrl);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual("hello!", secondBody);
        Assert.AreEqual("1", secondResponse.TrailingHeaders.GetValues("X-Response-Index").Single());

        Assert.AreEqual(1, connectionCount,
            "the proxy should have reused the single pooled upstream TCP connection for both requests");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task BeforeResponse_Respond_With_Custom_Response_Drains_Original_Chunked_Body_Without_Throwing()
    {
        // Regression test: NullWriter.WriteLineAsync used to throw NotImplementedException. That method is
        // invoked whenever SessionEventArgs.SyphonOutBodyAsync drains an unread *chunked* body/trailer -
        // here triggered by a BeforeResponse handler that overrides a chunked upstream response with
        // e.Ok(...) before the original body is read. Draining must succeed silently so the pooled
        // proxy -> server connection is left clean for reuse.
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        var connectionIds = new ConcurrentBag<string>();
        var requestIndex = 0;
        server.HandleRequest(async context =>
        {
            connectionIds.Add(context.Connection.Id);
            var n = Interlocked.Increment(ref requestIndex);

            // No Content-Length is set, so ASP.NET Core uses chunked transfer-encoding for this HTTP/1.1 response.
            await context.Response.WriteAsync(n == 1
                ? "original-upstream-body-that-should-be-discarded"
                : "second-request-body");
        });

        var proxy = testSuite.GetProxy();
        var respondedOnce = false;
        proxy.BeforeResponse += (sender, e) =>
        {
            if (!respondedOnce)
            {
                respondedOnce = true;
                e.Ok("custom-response-body");
            }

            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);

        var firstResponse = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.AreEqual("custom-response-body", firstBody);

        // If draining the first (discarded) chunked body/trailer had thrown or left stray bytes on the
        // wire, this second request over the same pooled connection would fail or return garbage.
        var secondResponse = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var secondBody = await secondResponse.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.AreEqual("second-request-body", secondBody);

        Assert.AreEqual(1, connectionIds.Distinct().Count(),
            "the pooled upstream connection should have been safely drained and reused");
    }

    /// <summary>
    ///     Characterization for issue #547: when an origin response carries both Content-Length and
    ///     Transfer-Encoding, the proxy must not forward both (RFC 9112 �6.3). Content-Length is stripped.
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Response_With_ContentLength_And_TransferEncoding_DoesNotForwardBoth()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var response = MsgEncoding.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 5\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n" +
                "5\r\nhello\r\n" +
                "0\r\n\r\n");
            await context.Transport.Output.WriteAsync(response);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                ms.Write(buffer, 0, read);
        }
        catch (OperationCanceledException)
        {
            // partial response is enough for header assertions
        }

        var text = MsgEncoding.GetString(ms.ToArray());
        Assert.IsTrue(text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
            $"Expected 200. Got:\n{text}");
        Assert.IsTrue(text.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase),
            "Transfer-Encoding must be preserved.");
        Assert.IsFalse(
            text.Contains("Content-Length:", StringComparison.OrdinalIgnoreCase),
            "Content-Length must be stripped when Transfer-Encoding is also present.");
        Assert.IsTrue(text.Contains("hello", StringComparison.Ordinal),
            "Chunked body must still be delivered.");
    }

    private static async Task DrainRequestHeaders(Microsoft.AspNetCore.Connections.ConnectionContext context)
    {
        // Drains the (headers-only, bodyless GET) request so the write below completes cleanly.
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer) requestText += MsgEncoding.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }
}

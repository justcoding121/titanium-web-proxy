§w
kD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\ChunkedTrailerTests.cs¢vusing System;
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
///     Integration tests for HTTP/1.1 chunked trailer handling (RFC 9110 Â§6.5 / RFC 9112 Â§7.1.2).
///     Previously the proxy relayed a syntactically valid, trailer-less terminator ("0\r\n\r\n") to the
///     client regardless of whether the upstream message actually carried trailers, silently dropping them.
///     These tests assert the corrected behavior: trailers are forwarded byte-for-byte, and - critically -
///     the source connection is always fully drained through the terminating blank line so a pooled
///     connection is never left in a corrupt state for the next message, whether or not the caller cares
///     about the trailer's contents.
/// </summary>
[TestClass]
public class ChunkedTrailerTests
{
    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    public async Task Chunked_Response_Trailer_Is_Forwarded_To_Client()
    {
        using var testSuite = new TestSuite();
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
        using var testSuite = new TestSuite();
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
        using var testSuite = new TestSuite();
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
        using var testSuite = new TestSuite();
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
        using var testSuite = new TestSuite();
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
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        var connectionIds = new ConcurrentBag<string>();
        var requestIndex = 0;
        server.HandleRequest(async context =>
        {
            connectionIds.Add(context.Connection.Id);
            var n = Interlocked.Increment(ref requestIndex);

            // No Content-Length is set, so Kestrel uses chunked transfer-encoding for this HTTP/1.1 response.
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
ParseOptions.0.jsonƒ
kD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\ConnectionPoolTests.csþusing System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ConnectionPoolTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Connection_Pool_Is_Enabled_By_Default_And_Reuses_Server_Connection()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();

        // Kestrel assigns a distinct Connection.Id per upstream TCP connection, so reuse of a pooled
        // proxy -> server connection shows up as the same id across sequential requests.
        var connectionIds = new ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            connectionIds.Add(context.Connection.Id);
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        Assert.IsTrue(proxy.EnableConnectionPool, "connection pool should be enabled by default");

        using var client = testSuite.GetClient(proxy);

        // sequential requests over the same client connection: the proxy should reuse one upstream connection
        for (var i = 0; i < 4; i++)
        {
            var body = await client.GetStringAsync(server.ListeningHttpUrl);
            Assert.AreEqual("ok", body);
        }

        Assert.AreEqual(1, connectionIds.Distinct().Count(),
            "the proxy should have reused a single pooled upstream connection across the requests");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Connection_Pool_Disabled_Does_Not_Reuse_Across_Client_Connections()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();

        var connectionIds = new ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            connectionIds.Add(context.Connection.Id);
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableConnectionPool = false;

        // Two separate client connections. Pooling governs reuse ACROSS client connections, so with it
        // disabled each client connection must open its own upstream connection.
        // (Within a single client connection the upstream connection is reused regardless of pooling.)
        using (var client1 = testSuite.GetClient(proxy))
            Assert.AreEqual("ok", await client1.GetStringAsync(server.ListeningHttpUrl));

        using (var client2 = testSuite.GetClient(proxy))
            Assert.AreEqual("ok", await client2.GetStringAsync(server.ListeningHttpUrl));

        Assert.AreEqual(2, connectionIds.Distinct().Count(),
            "without pooling each client connection should get its own upstream connection");
    }
}
ParseOptions.0.json×(
kD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\ExpectContinueTests.csÒ'using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ExpectContinueTests
{
    [TestMethod]
    public async Task ReverseProxy_GotContinueAndOkResponse()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer
        {
            ExpectationResponse = HttpStatusCode.Continue, ResponseBody = "I am server. I received your greetings."
        };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "Hello server. I am a client.");

        Assert.IsNotNull(response, "No response to 'expect: 100-continue' request");
        Assert.AreEqual((int)HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(continueServer.ResponseBody, response.BodyString);
    }

    [TestMethod]
    public async Task ReverseProxy_GotExpectationFailedResponse()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer { ExpectationResponse = HttpStatusCode.ExpectationFailed };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "Hello server. I am a client.");

        Assert.IsNotNull(response, "No response to 'expect: 100-continue' request");
        Assert.AreEqual((int)HttpStatusCode.ExpectationFailed, response.StatusCode);
    }

    [TestMethod]
    public async Task ReverseProxy_GotNotFoundResponse()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer { ExpectationResponse = HttpStatusCode.NotFound };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "Hello server. I am a client.");

        Assert.IsNotNull(response, "No response to 'expect: 100-continue' request");
        Assert.AreEqual((int)HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ReverseProxy_BeforeRequestThrows()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer { ExpectationResponse = HttpStatusCode.Continue };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var dbzEx = new DivideByZeroException("Undefined");
        var dbzString = $"{dbzEx.GetType()}: {dbzEx.Message}";

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (sender, e) =>
        {
            try
            {
                e.HttpClient.Request.Url = server.ListeningTcpUrl;
                throw dbzEx;
            }
            catch
            {
                var serverError = new Response(Encoding.ASCII.GetBytes(dbzString))
                {
                    HttpVersion = new Version(1, 1),
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    StatusDescription = HttpStatusCode.InternalServerError.ToString()
                };

                e.Respond(serverError);
            }

            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "Hello server. I am a client.");

        Assert.IsNotNull(response, "No response to 'expect: 100-continue' request");
        Assert.AreEqual(response.StatusCode, (int)HttpStatusCode.InternalServerError);
        Assert.AreEqual(response.BodyString, dbzString);
    }
}
ParseOptions.0.json­5
qD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\FakeUpstreamProxy.cs¢4#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

internal sealed class FakeUpstreamProxy : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly ConcurrentBag<Task> clientTasks = new();
    private readonly TcpListener listener;
    private readonly int httpsTargetPort;
    private readonly Task acceptTask;

    internal FakeUpstreamProxy(int httpsTargetPort)
    {
        this.httpsTargetPort = httpsTargetPort;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        acceptTask = AcceptClientsAsync();
    }

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    internal ConcurrentQueue<string> ProxyAuthorizationValues { get; } = new();

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        listener.Stop();

        try
        {
            acceptTask.GetAwaiter().GetResult();
            Task.WaitAll(clientTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException)
        {
        }

        cancellationTokenSource.Dispose();
    }

    private async Task AcceptClientsAsync()
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationTokenSource.Token);
                clientTasks.Add(HandleClientAsync(client, cancellationTokenSource.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var requestHeaders = await ReadHeadersAsync(stream, cancellationToken);
                if (requestHeaders == null) return;

                var requestLine = requestHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                var proxyAuthorization = GetHeaderValue(requestHeaders, "Proxy-Authorization") ?? string.Empty;
                ProxyAuthorizationValues.Enqueue(proxyAuthorization);

                if (proxyAuthorization.Length == 0)
                {
                    await Write407Async(stream, "NTLM", cancellationToken);
                    continue;
                }

                if (proxyAuthorization.Equals("NTLM t1", StringComparison.Ordinal))
                {
                    await Write407Async(stream, "NTLM challenge", cancellationToken);
                    continue;
                }

                if (!proxyAuthorization.Equals("NTLM t2", StringComparison.Ordinal))
                {
                    await WriteAsync(stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        cancellationToken);
                    return;
                }

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    await TunnelHttpsAsync(stream, cancellationToken);
                    return;
                }

                const string body = "authenticated plain HTTP";
                await WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}",
                    cancellationToken);
                return;
            }
        }
    }

    private async Task TunnelHttpsAsync(NetworkStream clientStream, CancellationToken cancellationToken)
    {
        using var target = new TcpClient();
        await target.ConnectAsync(IPAddress.Loopback, httpsTargetPort, cancellationToken);
        await WriteAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", cancellationToken);

        var targetStream = target.GetStream();
        var clientToTarget = clientStream.CopyToAsync(targetStream, cancellationToken);
        var targetToClient = targetStream.CopyToAsync(clientStream, cancellationToken);
        await Task.WhenAny(clientToTarget, targetToClient);
    }

    private static async Task Write407Async(NetworkStream stream, string challenge,
        CancellationToken cancellationToken)
    {
        const string body = "deny";
        await WriteAsync(stream,
            "HTTP/1.1 407 Proxy Authentication Required\r\n" +
            $"Proxy-Authenticate: {challenge}\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n{body}",
            cancellationToken);
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var value = new byte[1];
        var matched = 0;
        var terminator = new byte[] { 13, 10, 13, 10 };

        while (buffer.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(value, cancellationToken);
            if (read == 0) return null;

            buffer.WriteByte(value[0]);
            matched = value[0] == terminator[matched] ? matched + 1 : value[0] == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
                return Encoding.ASCII.GetString(buffer.ToArray());
        }

        throw new InvalidDataException("Proxy request headers exceeded the test limit.");
    }

    private static string? GetHeaderValue(string headers, string name)
    {
        var prefix = name + ":";
        return headers.Split(new[] { "\r\n" }, StringSplitOptions.None)
            .FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Substring(prefix.Length).Trim();
    }

    private static Task WriteAsync(NetworkStream stream, string value, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).AsTask();
    }
}
ParseOptions.0.json¢!
nD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\Http2RawClient.csš using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal, hand-rolled HTTP/2 client used to exercise proxy behavior a real HTTP/2 client
///     (<see cref="System.Net.Http.SocketsHttpHandler" />) either has no public API for (sending request
///     trailers, splitting a request header block across CONTINUATION frames) or does not reliably surface
///     to test code (interim 1xx informational responses over h2). Establishes a real HTTP CONNECT tunnel
///     through the proxy under test, then performs a real TLS/ALPN "h2" handshake with the (proxy-generated,
///     MITM'd) leaf certificate for the target host - trusting it the same way <see cref="TestProxyServer" />
///     configures the proxy to trust upstream certificates, via <see cref="TestCertificateAuthority" /> - so
///     everything downstream of the socket is indistinguishable, from the proxy's point of view, from a real
///     HTTP/2 browser/client. See <see cref="Http2RawFrame" /> for the underlying frame helpers, shared with
///     <see cref="Http2RawOriginServer" />.
/// </summary>
internal sealed class Http2RawClient : IDisposable
{
    private readonly TcpClient tcpClient;

    private Http2RawClient(TcpClient tcpClient, Http2RawFrame.Connection connection)
    {
        this.tcpClient = tcpClient;
        Connection = connection;
    }

    public Http2RawFrame.Connection Connection { get; }

    public static async Task<Http2RawClient> ConnectAsync(int proxyPort, string targetHost, int targetPort)
    {
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", proxyPort);

        var networkStream = tcpClient.GetStream();
        var connectRequest = $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n";
        var connectBytes = Encoding.ASCII.GetBytes(connectRequest);
        await networkStream.WriteAsync(connectBytes, 0, connectBytes.Length);

        await ReadUntilBlankLineAsync(networkStream);

        var sslStream = new SslStream(networkStream, false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol>
                { SslApplicationProtocol.Http2 },
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
        });

        await sslStream.WriteAsync(Http2Helper.ConnectionPreface, 0, Http2Helper.ConnectionPreface.Length);

        var connection = new Http2RawFrame.Connection(sslStream);
        await connection.SendInitialSettingsAsync();

        return new Http2RawClient(tcpClient, connection);
    }

    /// <summary>
    ///     Reads (and discards) bytes until the terminating blank line ("\r\n\r\n") of the proxy's CONNECT
    ///     response has been consumed, leaving the stream positioned exactly at the first byte of the TLS
    ///     handshake that follows.
    /// </summary>
    private static async Task ReadUntilBlankLineAsync(Stream stream)
    {
        const string terminator = "\r\n\r\n";
        var buffer = new byte[1];
        var matched = 0;
        while (matched < terminator.Length)
        {
            var read = await stream.ReadAsync(buffer, 0, 1);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Proxy closed the connection before completing the CONNECT handshake.");
            }

            matched = buffer[0] == terminator[matched] ? matched + 1 : buffer[0] == terminator[0] ? 1 : 0;
        }
    }

    public void Dispose()
    {
        tcpClient.Dispose();
    }
}
ParseOptions.0.jsonéZ
mD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\Http2RawFrame.csâYusing System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     Frame-level read/write helpers shared by <see cref="Http2RawOriginServer" />, built directly on the
///     proxy's own internal <see cref="Http2FrameType" />/<see cref="Http2FrameFlag" />/HPACK
///     <see cref="Encoder" />/<see cref="Decoder" /> types (accessible here via InternalsVisibleTo) so tests
///     get real, protocol-accurate framing without re-implementing HPACK.
/// </summary>
internal static class Http2RawFrame
{
    public readonly record struct Frame(Http2FrameType Type, int StreamId, Http2FrameFlag Flags, byte[] Payload);

    public static async Task WriteAsync(Stream stream, Http2FrameType type, int streamId, Http2FrameFlag flags,
        byte[] payload)
    {
        var header = new byte[9];
        var length = payload.Length;
        header[0] = (byte)((length >> 16) & 0xff);
        header[1] = (byte)((length >> 8) & 0xff);
        header[2] = (byte)(length & 0xff);
        header[3] = (byte)type;
        header[4] = (byte)flags;
        header[5] = (byte)((streamId >> 24) & 0x7f);
        header[6] = (byte)((streamId >> 16) & 0xff);
        header[7] = (byte)((streamId >> 8) & 0xff);
        header[8] = (byte)(streamId & 0xff);

        await stream.WriteAsync(header, 0, header.Length);
        if (length > 0)
        {
            await stream.WriteAsync(payload, 0, length);
        }
    }

    public static async Task<Frame> ReadAsync(Stream stream)
    {
        var header = new byte[9];
        await ReadExactAsync(stream, header, 0, header.Length);

        int length = (header[0] << 16) + (header[1] << 8) + header[2];
        var type = (Http2FrameType)header[3];
        var flags = (Http2FrameFlag)header[4];
        int streamId = ((header[5] & 0x7f) << 24) + (header[6] << 16) + (header[7] << 8) + header[8];

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(stream, payload, 0, length);
        }

        return new Frame(type, streamId, flags, payload);
    }

    public static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer, offset, count);
            if (read == 0)
            {
                throw new EndOfStreamException("The peer closed the connection before the expected bytes arrived.");
            }

            offset += read;
            count -= read;
        }
    }

    /// <summary>
    ///     Encodes the given pseudo-headers (sent first, without static-table name reuse suppression - not
    ///     needed for a short-lived, single-purpose test encoder) followed by regular headers, using a fresh
    ///     <see cref="Encoder" />. A fresh encoder per connection is fine here: unlike the proxy itself, these
    ///     tests do not need to exercise dynamic-table reuse across many messages.
    /// </summary>
    public static byte[] EncodeHeaderBlock(Encoder encoder, IEnumerable<(string Name, string Value)> pseudoHeaders,
        IEnumerable<(string Name, string Value)> headers)
    {
        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);

        foreach (var (name, value) in pseudoHeaders)
        {
            encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString(), false,
                HpackUtil.IndexType.None, false);
        }

        foreach (var (name, value) in headers)
        {
            encoder.EncodeHeader(writer, name.GetByteString(), value.GetByteString());
        }

        return ms.ToArray();
    }

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        public readonly List<(string Name, string Value)> Headers = new();

        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
            Headers.Add((name.GetString(), value.GetString()));
        }
    }

    public static List<(string Name, string Value)> DecodeHeaderBlock(Decoder decoder, byte[] compressed)
    {
        var listener = new RecordingHeaderListener();
        decoder.Decode(new BinaryReader(new MemoryStream(compressed)), listener);
        decoder.EndHeaderBlock();
        return listener.Headers;
    }

    /// <summary>
    ///     One accepted, already-TLS/ALPN/preface-established raw HTTP/2 connection.
    /// </summary>
    public sealed class Connection
    {
        private readonly Stream stream;
        private readonly Encoder encoder = new(4096);
        private readonly Decoder decoder = new(8192, 4096);

        public Connection(Stream stream)
        {
            this.stream = stream;
        }

        public Task WriteFrameAsync(Http2FrameType type, int streamId, Http2FrameFlag flags, byte[] payload)
        {
            return WriteAsync(stream, type, streamId, flags, payload);
        }

        public Task<Frame> ReadFrameAsync()
        {
            return ReadAsync(stream);
        }

        public byte[] EncodeHeaders(IEnumerable<(string Name, string Value)> pseudoHeaders,
            IEnumerable<(string Name, string Value)> headers)
        {
            return EncodeHeaderBlock(encoder, pseudoHeaders, headers);
        }

        public List<(string Name, string Value)> DecodeHeaders(byte[] compressed)
        {
            return DecodeHeaderBlock(decoder, compressed);
        }

        /// <summary>
        ///     Sends an initial (possibly empty) SETTINGS frame, as any real HTTP/2 endpoint must as its
        ///     first frame - the real client on the other side of the proxy relay expects one before it will
        ///     consider the connection usable.
        /// </summary>
        public Task SendInitialSettingsAsync()
        {
            return WriteFrameAsync(Http2FrameType.Settings, 0, 0, Array.Empty<byte>());
        }

        /// <summary>
        ///     Reads frames until the request HEADERS block (assumed to fit in one frame - true for the
        ///     small test requests these tests send) and any DATA frames are fully consumed through
        ///     END_STREAM. SETTINGS/WINDOW_UPDATE/PING frames encountered along the way are ignored.
        /// </summary>
        public async Task<(int StreamId, List<(string Name, string Value)> Headers, byte[] Body)> ReadRequestAsync()
        {
            int streamId = -1;
            List<(string Name, string Value)> requestHeaders = null;
            var body = new MemoryStream();

            while (true)
            {
                var frame = await ReadFrameAsync();
                if (frame.Type == Http2FrameType.Headers)
                {
                    streamId = frame.StreamId;
                    requestHeaders = DecodeHeaders(frame.Payload);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.Data && frame.StreamId == streamId)
                {
                    body.Write(frame.Payload, 0, frame.Payload.Length);
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.Continuation && frame.StreamId == streamId)
                {
                    // not expected for the small test requests these tests send; ignore defensively.
                }
            }

            return (streamId, requestHeaders, body.ToArray());
        }

        /// <summary>
        ///     Reads the next HEADERS (or PUSH_PROMISE) frame - skipping any interleaved SETTINGS,
        ///     WINDOW_UPDATE, PING or GOAWAY frames encountered while waiting for it, since those are
        ///     transparently relayed by the proxy and are not what the caller is looking for - then keeps
        ///     reading CONTINUATION frames until END_HEADERS, reassembling and decoding the full header
        ///     block. This mirrors (a simplified, test-only version of) the reassembly the proxy itself does
        ///     in <c>Http2Helper.CopyHttp2FrameAsync</c>, so it can be used on either side of the proxy to
        ///     observe the result of that reassembly/re-splitting.
        /// </summary>
        public async Task<(int StreamId, List<(string Name, string Value)> Headers, bool EndStream)>
            ReadHeaderBlockAsync()
        {
            Frame frame;
            do
            {
                frame = await ReadFrameAsync();
            } while (frame.Type != Http2FrameType.Headers && frame.Type != Http2FrameType.PushPromise);

            var streamId = frame.StreamId;
            var endStream = (frame.Flags & Http2FrameFlag.EndStream) != 0;
            var compressed = new MemoryStream();
            compressed.Write(frame.Payload, 0, frame.Payload.Length);

            while ((frame.Flags & Http2FrameFlag.EndHeaders) == 0)
            {
                frame = await ReadFrameAsync();
                if (frame.Type != Http2FrameType.Continuation || frame.StreamId != streamId)
                {
                    throw new InvalidOperationException(
                        $"Expected a CONTINUATION frame for stream {streamId} but got {frame.Type} for stream {frame.StreamId}.");
                }

                compressed.Write(frame.Payload, 0, frame.Payload.Length);
            }

            return (streamId, DecodeHeaders(compressed.ToArray()), endStream);
        }

        /// <summary>
        ///     Writes one already-HPACK-encoded header block as a HEADERS frame followed by as many
        ///     CONTINUATION frames as needed so that no single frame's payload exceeds
        ///     <paramref name="maxFrameSize" />, letting tests deliberately force the proxy's inbound
        ///     HEADERS/CONTINUATION reassembly path regardless of the encoded block's actual size.
        /// </summary>
        public async Task WriteHeaderBlockAsync(int streamId, byte[] compressed, bool endStream,
            int maxFrameSize = 16384)
        {
            var pos = 0;
            var first = true;
            do
            {
                var chunkLength = Math.Min(maxFrameSize, compressed.Length - pos);
                var isLast = pos + chunkLength >= compressed.Length;

                var flags = (Http2FrameFlag)0;
                if (isLast) flags |= Http2FrameFlag.EndHeaders;
                if (first && endStream) flags |= Http2FrameFlag.EndStream;

                var chunk = compressed.AsSpan(pos, chunkLength).ToArray();
                await WriteFrameAsync(first ? Http2FrameType.Headers : Http2FrameType.Continuation, streamId, flags,
                    chunk);

                pos += chunkLength;
                first = false;
            } while (pos < compressed.Length);
        }
    }
}
ParseOptions.0.json•#
tD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\Http2RawOriginServer.cs‡"using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal, hand-rolled HTTP/2 origin server used to exercise proxy behavior that a real HTTP/2
///     server (Kestrel) either cannot easily be told to do (send an interim 1xx, split a header block
///     across CONTINUATION frames, send trailers with exact byte control) or a real HTTP/2 client
///     (SocketsHttpHandler) has no public API for on the request side (see <see cref="Http2RawFrame" />
///     for the shared frame read/write helpers).
///     <para>
///         Speaks real TLS with ALPN "h2", using a certificate issued by the same test root CA the proxy
///         under test is configured to trust for upstream connections (see
///         <see cref="TestCertificateAuthority" />), so it can be used as a normal HTTPS upstream target
///         (<see cref="Url" />) exactly as a real origin server would be - the proxy itself cannot tell the
///         difference.
///     </para>
/// </summary>
internal sealed class Http2RawOriginServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private Func<Http2RawFrame.Connection, Task> handler;
    private bool disposed;

    public Http2RawOriginServer(X509Certificate2 certificate)
    {
        this.certificate = certificate;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public string Url => $"https://localhost:{Port}/";

    /// <summary>
    ///     Sets the handler invoked for each accepted connection, after the TLS/ALPN handshake and the
    ///     client connection preface have already been consumed.
    /// </summary>
    public void HandleConnection(Func<Http2RawFrame.Connection, Task> connectionHandler)
    {
        handler = connectionHandler;
    }

    private async Task AcceptLoopAsync()
    {
        while (!disposed)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var sslStream = new SslStream(client.GetStream(), false);
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 },
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
                    });

                    var preface = new byte[Http2Helper.ConnectionPreface.Length];
                    await Http2RawFrame.ReadExactAsync(sslStream, preface, 0, preface.Length);

                    var connection = new Http2RawFrame.Connection(sslStream);
                    var currentHandler = handler;
                    if (currentHandler != null)
                    {
                        await currentHandler(connection);
                    }
                }
                catch (Exception ex)
                {
                    // swallow - test assertions on the client/proxy side will surface the failure, but log
                    // for diagnostics since an exception here otherwise fails silently.
                    Console.WriteLine("Http2RawOriginServer connection handler failed: " + ex);
                }
                finally
                {
                    client.Dispose();
                }
            });
        }
    }

    public void Dispose()
    {
        disposed = true;
        listener.Stop();
    }
}
ParseOptions.0.jsonî
rD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\HttpContinueClient.csâusing System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

internal class HttpContinueClient
{
    private const int WaitTimeout = 500;

    private static readonly Encoding _msgEncoding = HttpHelper.GetEncodingFromContentType(null);

    public async Task<Response> Post(string server, int port, string content)
    {
        var message = _msgEncoding.GetBytes(content);
        var client = new TcpClient(server, port);
        client.SendTimeout = client.ReceiveTimeout = 500;

        var request = new Request { Method = "POST", RequestUriString = "/", HttpVersion = new Version(1, 1) };
        request.Headers.AddHeader(KnownHeaders.Host, server);
        request.Headers.AddHeader(KnownHeaders.ContentLength, message.Length.ToString());
        request.Headers.AddHeader(KnownHeaders.Expect, KnownHeaders.Expect100Continue);

        var header = _msgEncoding.GetBytes(request.HeaderText);
        await client.GetStream().WriteAsync(header, 0, header.Length);

        var buffer = new byte[1024];
        var responseMsg = string.Empty;
        Response response;

        while ((response = HttpMessageParsing.ParseResponse(responseMsg)) == null)
        {
            var readTask = client.GetStream().ReadAsync(buffer, 0, 1024);
            if (!readTask.Wait(WaitTimeout))
            {
                return null;
            }

            responseMsg += _msgEncoding.GetString(buffer, 0, readTask.Result);
        }

        if (response.StatusCode == 100)
        {
            await client.GetStream().WriteAsync(message);

            responseMsg = string.Empty;

            while ((response = HttpMessageParsing.ParseResponse(responseMsg)) == null)
            {
                var readTask = client.GetStream().ReadAsync(buffer, 0, 1024);
                if (!readTask.Wait(WaitTimeout))
                {
                    return null;
                }

                responseMsg += _msgEncoding.GetString(buffer, 0, readTask.Result);
            }

            return response;
        }

        return response;
    }
}
ParseOptions.0.jsonû
rD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\HttpContinueServer.csïusing System;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

internal class HttpContinueServer
{
    private static readonly Encoding _msgEncoding = HttpHelper.GetEncodingFromContentType(null);
    public HttpStatusCode ExpectationResponse;
    public string ResponseBody;

    public async Task HandleRequest(ConnectionContext context)
    {
        var request = await ReadHeaders(context.Transport.Input);

        if (request.ExpectContinue)
        {
            var respondContinue = new Response
            {
                HttpVersion = request.HttpVersion,
                StatusCode = (int)ExpectationResponse,
                StatusDescription = ExpectationResponse.ToString()
            };
            await context.Transport.Output.WriteAsync(_msgEncoding.GetBytes(respondContinue.HeaderText));

            if (ExpectationResponse != HttpStatusCode.Continue)
            {
                return;
            }
        }

        request = await ReadBody(request, context.Transport.Input);

        var responseMsg = _msgEncoding.GetBytes(ResponseBody);
        var respondOk = new Response(responseMsg)
        {
            HttpVersion = new Version(1, 1),
            StatusCode = (int)HttpStatusCode.OK,
            StatusDescription = HttpStatusCode.OK.ToString()
        };
        await context.Transport.Output.WriteAsync(_msgEncoding.GetBytes(respondOk.HeaderText));
        await context.Transport.Output.WriteAsync(responseMsg);
        context.Transport.Output.Complete();
    }

    private async Task<Request> ReadHeaders(PipeReader input)
    {
        Request request = null;
        try
        {
            var requestMsg = string.Empty;
            while ((request = HttpMessageParsing.ParseRequest(requestMsg, false)) == null)
            {
                var result = await input.ReadAsync();
                foreach (var seg in result.Buffer)
                {
                    requestMsg += _msgEncoding.GetString(seg.Span);
                }

                input.AdvanceTo(result.Buffer.End);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.GetType()}: {ex.Message}");
        }

        return request;
    }

    private async Task<Request> ReadBody(Request request, PipeReader input)
    {
        var msg = request.HeaderText;
        try
        {
            while ((request = HttpMessageParsing.ParseRequest(msg, true)) == null)
            {
                var result = await input.ReadAsync();
                foreach (var seg in result.Buffer)
                {
                    msg += _msgEncoding.GetString(seg.Span);
                }

                input.AdvanceTo(result.Buffer.End);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.GetType()}: {ex.Message}");
        }

        return request;
    }
}
ParseOptions.0.jsonÜ
rD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\HttpMessageParsing.csÐusing System.IO;
using System.Text;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

internal static class HttpMessageParsing
{
    private static readonly char[] colonSplit = { ':' };

    /// <summary>
    ///     This is a terribly inefficient way of reading & parsing an
    ///     http request, but it's good enough for testing purposes.
    /// </summary>
    /// <param name="messageText">The request message</param>
    /// <param name="requireBody"></param>
    /// <returns>Request object if message complete, null otherwise</returns>
    internal static Request ParseRequest(string messageText, bool requireBody)
    {
        var reader = new StringReader(messageText);
        var line = reader.ReadLine();
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        try
        {
            Request.ParseRequestLine(line, out var method, out var url, out var version);
            var request = new Request { Method = method, RequestUriString8 = url, HttpVersion = version };
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var header = line.Split(colonSplit, 2);
                request.Headers.AddHeader(header[0].Trim(), header[1].Trim());
            }

            // First zero-length line denotes end of headers. If we
            // didn't get one, then we're not done with request
            if (line?.Length != 0)
            {
                return null;
            }

            if (!requireBody)
            {
                return request;
            }

            if (ParseBody(reader, request))
            {
                return request;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>
    ///     This is a terribly inefficient way of reading & parsing an
    ///     http response, but it's good enough for testing purposes.
    /// </summary>
    /// <param name="messageText">The response message</param>
    /// <returns>Response object if message complete, null otherwise</returns>
    internal static Response ParseResponse(string messageText)
    {
        var reader = new StringReader(messageText);
        var line = reader.ReadLine();
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        try
        {
            Response.ParseResponseLine(line, out var version, out var status, out var desc);
            var response = new Response { HttpVersion = version, StatusCode = status, StatusDescription = desc };

            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var header = line.Split(colonSplit, 2);
                response.Headers.AddHeader(header[0], header[1]);
            }

            // First zero-length line denotes end of headers. If we
            // didn't get one, then we're not done with response
            if (line?.Length != 0)
            {
                return null;
            }

            if (ParseBody(reader, response))
            {
                return response;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool ParseBody(StringReader reader, RequestResponseBase obj)
    {
        obj.OriginalContentLength = obj.ContentLength;
        if (obj.ContentLength <= 0)
        {
            // no body, done
            return true;
        }

        obj.Body = Encoding.ASCII.GetBytes(reader.ReadToEnd());

        // done reading body
        return obj.ContentLength == obj.OriginalContentLength;
    }
}
ParseOptions.0.jsonš
jD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\TestHelper.cs–using System;
using System.Net;
using System.Net.Http;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

public static class TestHelper
{
    public static HttpClient GetHttpClient(int localProxyPort,
        bool enableBasicProxyAuthorization = false)
    {
        var proxy = new TestProxy($"http://localhost:{localProxyPort}", enableBasicProxyAuthorization);

        var handler = CreateHandler();
        handler.Proxy = proxy;
        handler.UseProxy = true;

        return new HttpClient(handler);
    }

    public static HttpClient GetHttpClient()
    {
        return new HttpClient(CreateHandler());
    }

    /// <summary>
    ///     An HttpClient forced onto HTTP/2 (via a fixed proxy and RequestVersionExact) for exercising the
    ///     proxy's HTTP/2 relay. A single instance reuses one underlying HTTP/2 connection (and therefore one
    ///     HPACK encoder/decoder pair on each leg) across multiple requests, which is what tests of
    ///     connection-scoped state (e.g. HPACK dynamic table reuse) need.
    /// </summary>
    public static HttpClient GetHttp2Client(ProxyServer proxy)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };

        return new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
        };
    }

    public class TestProxy : IWebProxy
    {
        public TestProxy(string proxyUri, bool enableAuthorization)
            : this(new Uri(proxyUri))
        {
            if (enableAuthorization)
            {
                Credentials = new NetworkCredential("test", "Test56");
            }
        }

        private TestProxy(Uri proxyUri)
        {
            ProxyUri = proxyUri;
        }

        public Uri ProxyUri { get; set; }
        public ICredentials Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            return ProxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            return false;
        }
    }
}
ParseOptions.0.jsonä4
bD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Http2Tests.csè3using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     HTTP/2 integration tests. Complements the existing HTTP/2 coverage in
///     <see cref="StreamingBodyTests" /> (body-write hooks, RespondStreaming) with tests for HPACK
///     encoder persistence: previously <c>Http2Helper.SendHeader</c> constructed a brand-new
///     <c>Encoder</c> (with an empty dynamic table) on every call, so repeated headers across streams/requests
///     on the same HTTP/2 connection were never indexed - see the characterization tests in
///     <c>Http2HpackEncoderTests</c>. The encoder is now persisted per connection direction, matching how the
///     decoder was already handled, so these tests exercise many requests over one HTTP/2 connection to prove
///     the dynamic table is actually being reused end-to-end without corrupting headers.
/// </summary>
[TestClass]
public class Http2Tests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Repeated_Response_Header_Round_Trips_Correctly_Across_Multiple_Requests()
    {
        // A long, distinctive value so it would dominate a naive per-call HPACK encoding if it were re-sent
        // literally on every response; a persistent encoder should index it after the first response and
        // reference it on every subsequent one, on the same underlying HTTP/2 connection.
        const string repeatedValue =
            "a-fairly-long-repeated-header-value-used-to-exercise-http2-hpack-dynamic-table-reuse-across-requests";

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.Headers["X-Custom-Repeated"] = repeatedValue;
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        for (var i = 0; i < 10; i++)
        {
            var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(new Version(2, 0), response.Version);
            Assert.AreEqual("ok", body);
            Assert.IsTrue(response.Headers.TryGetValues("X-Custom-Repeated", out var values),
                $"Request #{i} is missing the repeated header.");
            Assert.AreEqual(repeatedValue, values.Single(),
                $"Request #{i}'s repeated header value was corrupted - possible HPACK dynamic-table indexing bug.");
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Repeated_Request_Header_Round_Trips_Correctly_Across_Multiple_Requests()
    {
        // Same as above but for the client -> proxy -> server direction (the encoder for that direction is
        // used only within a single relay task, unlike the client-bound one which is shared across both relay
        // tasks for synthetic responses - so this exercises the simpler, but still previously-unindexed, path).
        const string repeatedValue =
            "another-fairly-long-repeated-header-value-for-the-request-direction-hpack-dynamic-table";

        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var receivedValues = new System.Collections.Concurrent.ConcurrentBag<string>();
        server.HandleRequest(context =>
        {
            receivedValues.Add(context.Request.Headers["X-Custom-Repeated"].ToString());
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ListeningHttpsUrl));
            request.Headers.Add("X-Custom-Repeated", repeatedValue);

            var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.AreEqual(10, receivedValues.Count);
        Assert.IsTrue(receivedValues.All(v => v == repeatedValue),
            "Every request's repeated header value should have round-tripped intact - possible HPACK dynamic-table indexing bug.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Many_Concurrent_Streams_With_Distinct_Headers_Do_Not_Cross_Contaminate()
    {
        // Fires many concurrent requests over the same HTTP/2 connection (true multiplexing, interleaved
        // frames) each with a stream-specific header value, guarding against the shared encoder/decoder
        // introducing cross-stream contamination now that state is persisted per connection direction.
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            var echo = context.Request.Headers["X-Stream-Id"].ToString();
            context.Response.Headers["X-Stream-Id-Echo"] = echo;
            return context.Response.WriteAsync(echo);
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);

        const int concurrency = 20;
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ListeningHttpsUrl));
            request.Headers.Add("X-Stream-Id", i.ToString());

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(i.ToString(), body, $"Stream #{i}'s response body was cross-contaminated.");
            Assert.AreEqual(i.ToString(), response.Headers.GetValues("X-Stream-Id-Echo").Single(),
                $"Stream #{i}'s response header was cross-contaminated.");
        });

        await Task.WhenAll(tasks);
    }
}
ParseOptions.0.json¹f
|D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Http2TrailerInterimContinuationTests.cs£eusing System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/2 behavior that cannot be driven through a real HttpClient on both ends
///     of the proxy at once: response and request trailers (RFC 7540 Â§8.1.2.1), interim (1xx) informational
///     responses relayed over h2 (RFC 9110 Â§15.2), and HEADERS/CONTINUATION reassembly + re-splitting (RFC
///     7540 Â§4.3/Â§6.10). HttpClient has no public API to send request trailers or to deliberately fragment
///     a header block across CONTINUATION frames, and does not reliably surface informational responses to
///     test code - so these tests use <see cref="Http2RawClient" /> and <see cref="Http2RawOriginServer" />
///     (hand-rolled but protocol-accurate h2 endpoints built on the proxy's own internal frame/HPACK types)
///     on both sides, while still routing every request through a completely real <see cref="ProxyServer" />.
///     Complements the HPACK dynamic-table-reuse coverage in <see cref="Http2Tests" /> and the trailer/interim
///     coverage already in <see cref="ChunkedTrailerTests" />/<see cref="InterimResponseTests" /> for HTTP/1.x.
/// </summary>
[TestClass]
public class Http2TrailerInterimContinuationTests
{
    private static X509Certificate2 CreateOriginCertificate()
    {
        using var dummyProxy = new ProxyServer(false, false, false);
        dummyProxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        return dummyProxy.CertificateManager.CreateServerCertificate("localhost").Result;
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Response_Trailers_From_Origin_Are_Relayed_To_Client()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, false);

            var body = Encoding.ASCII.GetBytes("hello");
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, 0, body);

            var trailers = connection.EncodeHeaders(Array.Empty<(string, string)>(),
                new[] { ("x-trailer", "trailer-value") });
            await connection.WriteHeaderBlockAsync(streamId, trailers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, mainResponse, mainEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", mainResponse.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(mainEndStream);

        var dataFrame = await rawClient.Connection.ReadFrameAsync();
        Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
        Assert.AreEqual("hello", Encoding.ASCII.GetString(dataFrame.Payload));
        Assert.IsFalse((dataFrame.Flags & Http2FrameFlag.EndStream) != 0);

        var (_, trailerHeaders, trailerEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.IsTrue(trailerEndStream, "The trailer HEADERS block should have carried END_STREAM.");
        Assert.AreEqual("trailer-value", trailerHeaders.Single(h => h.Name == "x-trailer").Value);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Request_Trailers_From_RawClient_Are_Relayed_To_Origin()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());

        List<(string Name, string Value)> receivedTrailers = null;
        var trailersReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();

            var (streamId, _, requestEndStream) = await connection.ReadHeaderBlockAsync();
            Assert.IsFalse(requestEndStream, "The main request HEADERS should not have carried END_STREAM.");

            while (true)
            {
                var frame = await connection.ReadFrameAsync();
                if (frame.Type == Http2FrameType.Data)
                {
                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    {
                        break;
                    }
                }
                else if (frame.Type == Http2FrameType.Headers && frame.StreamId == streamId)
                {
                    receivedTrailers = connection.DecodeHeaders(frame.Payload);
                    break;
                }
            }

            trailersReceived.TrySetResult(true);

            var responseHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, responseHeaders, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "POST"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);

        var requestBody = Encoding.ASCII.GetBytes("request-body");
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, 0, requestBody);

        var requestTrailers = rawClient.Connection.EncodeHeaders(Array.Empty<(string, string)>(),
            new[] { ("x-request-trailer", "req-trailer-value") });
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestTrailers, true);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);

        await Task.WhenAny(trailersReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(trailersReceived.Task.IsCompleted,
            "The origin server never received the client's trailer HEADERS block.");
        Assert.IsNotNull(receivedTrailers);
        Assert.AreEqual("req-trailer-value",
            receivedTrailers.Single(h => h.Name == "x-request-trailer").Value);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Interim_1xx_Response_Is_Relayed_Before_Final_Response()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var interimHeaders = connection.EncodeHeaders(new[] { (":status", "103") },
                new[] { ("link", "</style.css>; rel=preload") });
            await connection.WriteHeaderBlockAsync(streamId, interimHeaders, false);

            var finalHeaders = connection.EncodeHeaders(new[] { (":status", "200") },
                Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, finalHeaders, false);

            var body = Encoding.ASCII.GetBytes("final-body");
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, body);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient =
            await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, interimResponse, interimEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("103", interimResponse.Single(h => h.Name == ":status").Value);
        Assert.AreEqual("</style.css>; rel=preload", interimResponse.Single(h => h.Name == "link").Value);
        Assert.IsFalse(interimEndStream, "An interim response must never end the stream.");

        var (_, finalResponse, finalEndStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", finalResponse.Single(h => h.Name == ":status").Value);
        Assert.IsFalse(finalEndStream);

        var dataFrame = await rawClient.Connection.ReadFrameAsync();
        Assert.AreEqual(Http2FrameType.Data, dataFrame.Type);
        Assert.AreEqual("final-body", Encoding.ASCII.GetString(dataFrame.Payload));
        Assert.IsTrue((dataFrame.Flags & Http2FrameFlag.EndStream) != 0);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Large_Response_Header_Split_Across_Continuation_Is_Reassembled_And_Relayed()
    {
        // Comfortably under the decoder's fixed 8192-byte max-decompressed-header-block size (see
        // Http2Helper.CopyHttp2FrameAsync's `new Decoder(8192, ...)`) - large enough that, combined with
        // forcing the origin to fragment it at a small frame size below, the proxy's HEADERS/CONTINUATION
        // reassembly path is exercised on the way in, but small enough it is not silently truncated by
        // that unrelated, pre-existing size cap.
        var largeValue = string.Concat(Enumerable.Range(0, 190).Select(_ => Guid.NewGuid().ToString("N")));

        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();

            var headers = connection.EncodeHeaders(new[] { (":status", "200") },
                new[] { ("x-large", largeValue) });

            // Force fragmentation at the origin regardless of the actual encoded size.
            await connection.WriteHeaderBlockAsync(streamId, headers, true, 1024);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(rawServer.Url);
        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "GET"), (":scheme", "https"), (":authority", $"{uri.Host}:{uri.Port}"),
                (":path", "/")
            },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        Assert.IsTrue(endStream);
        Assert.AreEqual(largeValue, responseHeaders.Single(h => h.Name == "x-large").Value,
            "The large header split across CONTINUATION frames by the origin was not relayed intact.");
    }
}
ParseOptions.0.jsonÂ
bD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\HttpsTests.csÆusing System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class HttpsTests
{
    [TestMethod]
    public async Task Can_Handle_Https_Request()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        var client = testSuite.GetClient(proxy);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Can_Handle_Https_Fake_Tunnel_Request()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PostAsync(new Uri($"https://{Guid.NewGuid().ToString()}.com"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Can_Handle_Https_Mutual_Tls_Request()
    {
        using var testSuite = new TestSuite(true);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        var clientCert = proxy.CertificateManager.CreateCertificate("client.com", false);

        proxy.ClientCertificateSelectionCallback += async (sender, e) =>
        {
            e.ClientCertificate = clientCert;
            await Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }
}
ParseOptions.0.jsonµ.
iD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\InterceptionTests.cs²-using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class InterceptionTests
{
    [TestMethod]
    public async Task Can_Intercept_Get_Requests()
    {
        using var testSuite = new TestSuite();

        var serverCalled = false;

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            serverCalled = true;
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("localhost"))
            {
                e.Ok("<html><body>TitaniumWebProxy-Stopped!!</body></html>");
                return;
            }

            await Task.FromResult(0);
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));

        Assert.IsFalse(serverCalled, "Server should not be called.");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
    }

    [TestMethod]
    public async Task Can_Intercept_Post_Requests()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("localhost"))
            {
                e.Ok("<html><body>TitaniumWebProxy-Stopped!!</body></html>");
                return;
            }

            await Task.FromResult(0);
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PostAsync(new Uri(server.ListeningHttpUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
    }

    [TestMethod]
    public async Task Can_Intercept_Put_Requests()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("localhost"))
            {
                e.Ok("<html><body>TitaniumWebProxy-Stopped!!</body></html>");
                return;
            }

            await Task.FromResult(0);
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PutAsync(new Uri(server.ListeningHttpUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
    }


    [TestMethod]
    public async Task Can_Intercept_Patch_Requests()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("localhost"))
            {
                e.Ok("<html><body>TitaniumWebProxy-Stopped!!</body></html>");
                return;
            }

            await Task.FromResult(0);
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PatchAsync(new Uri(server.ListeningHttpUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
    }

    [TestMethod]
    public async Task Can_Intercept_Delete_Requests()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            if (e.HttpClient.Request.Url.Contains("localhost"))
            {
                e.Ok("<html><body>TitaniumWebProxy-Stopped!!</body></html>");
                return;
            }

            await Task.FromResult(0);
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.DeleteAsync(new Uri(server.ListeningHttpUrl));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("TitaniumWebProxy-Stopped!!"));
    }
}
ParseOptions.0.json‹Y
lD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\InterimResponseTests.cs…Xusing System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for interim (1xx) response handling in
///     <c>ResponseHandler.HandleHttpSessionResponse</c>. Previously only 100 Continue was recognized;
///     any other 1xx (e.g. 103 Early Hints) was mistakenly treated as the final response - forwarded to the
///     client as-is, with the proxy then trying to read the *next* HTTP message on the connection as if it
///     were a brand new request/response pair. These tests assert the corrected behavior: every interim
///     response is relayed (100 is still discarded, matching the documented "client can simply discard this
///     interim response" behavior) and the proxy keeps reading interim responses on the connection until
///     the true final response arrives. 101 Switching Protocols is verified separately, since it is
///     deliberately excluded from the loop (it *is* the final message of the exchange).
///     <para>
///         Uses raw <see cref="TcpClient" />/<see cref="Setup.TestServer.HandleTcpRequest" /> on both sides
///         because .NET's <c>HttpClient</c> does not reliably surface arbitrary 1xx responses (other than
///         100/101, which it has dedicated handling for) to application code.
///     </para>
/// </summary>
[TestClass]
public class InterimResponseTests
{
    private static readonly Encoding MsgEncoding = HttpHelper.GetEncodingFromContentType(null);

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Interim_103_EarlyHints_Response_Is_Relayed_Before_Final_Response()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 103 Early Hints\r\n" +
                "Link: </style.css>; rel=preload\r\n" +
                "\r\n" +
                "HTTP/1.1 200 OK\r\n" +
                "Content-Length: 5\r\n" +
                "\r\n" +
                "hello");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", TimeSpan.FromSeconds(8));

        var firstStatusIndex = responseText.IndexOf("HTTP/1.1 103", StringComparison.Ordinal);
        var finalStatusIndex = responseText.IndexOf("HTTP/1.1 200", StringComparison.Ordinal);

        Assert.IsTrue(firstStatusIndex >= 0,
            "The 103 Early Hints interim response should have been relayed to the client.");
        Assert.IsTrue(finalStatusIndex > firstStatusIndex,
            "The final 200 response should follow the interim response.");
        Assert.IsTrue(responseText.Contains("Link: </style.css>; rel=preload", StringComparison.Ordinal),
            "The interim response's own headers should have been relayed too.");
        Assert.IsTrue(responseText.EndsWith("hello", StringComparison.Ordinal),
            "The final response's body should still be relayed correctly.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Interim_Multiple_1xx_Responses_Are_All_Relayed_In_Order_Before_Final_Response()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 103 Early Hints\r\nX-Seq: 1\r\n\r\n" +
                "HTTP/1.1 103 Early Hints\r\nX-Seq: 2\r\n\r\n" +
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", TimeSpan.FromSeconds(8));

        var seq1Index = responseText.IndexOf("X-Seq: 1", StringComparison.Ordinal);
        var seq2Index = responseText.IndexOf("X-Seq: 2", StringComparison.Ordinal);
        var finalIndex = responseText.IndexOf("HTTP/1.1 200", StringComparison.Ordinal);

        Assert.IsTrue(seq1Index >= 0 && seq2Index > seq1Index && finalIndex > seq2Index,
            $"Expected both interim responses relayed in order before the final response. Got:\n{responseText}");
        Assert.IsTrue(responseText.EndsWith("ok", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Interim_100Continue_From_Server_Is_Discarded_Not_Relayed_To_Client()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 100 Continue\r\n\r\n" +
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n", TimeSpan.FromSeconds(8));

        Assert.IsFalse(responseText.Contains("100 Continue", StringComparison.Ordinal),
            "100 Continue must still be discarded rather than relayed to the client (per spec, it is safe to discard).");
        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 200", StringComparison.Ordinal));
        Assert.IsTrue(responseText.EndsWith("hello", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task SwitchingProtocols_101_Response_Is_Relayed_Exactly_Once_Not_Looped_As_Interim()
    {
        // Regression guard for the loop's exclusion of 101: 100-199 broadly includes 101, so a naive
        // interim-response loop would incorrectly try to read yet another response after it (hanging, since
        // the "connection" is about to become a raw tunnel and nothing else will arrive framed as HTTP).
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            await DrainRequestHeaders(context);

            var raw = MsgEncoding.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: custom-protocol\r\n" +
                "Connection: Upgrade\r\n" +
                "\r\n");

            await context.Transport.Output.WriteAsync(raw);
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var responseText = await SendRawRequestAndReadResponse(proxy,
            "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: custom-protocol\r\nConnection: Upgrade\r\n\r\n",
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(responseText.StartsWith("HTTP/1.1 101", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(responseText, "HTTP/1.1 101"),
            "The 101 response must be relayed exactly once, never looped as if it were a discardable interim response.");
        Assert.AreEqual(1, CountOccurrences(responseText, "HTTP/1.1"),
            "Nothing else should have been read/relayed as a second message after the 101.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static async Task DrainRequestHeaders(Microsoft.AspNetCore.Connections.ConnectionContext context)
    {
        var requestText = string.Empty;
        while (!requestText.Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var result = await context.Transport.Input.ReadAsync();
            foreach (var seg in result.Buffer) requestText += MsgEncoding.GetString(seg.Span);
            context.Transport.Input.AdvanceTo(result.Buffer.End);
        }
    }

    /// <summary>
    ///     Sends a hand-built raw HTTP request directly to the proxy and accumulates whatever bytes come
    ///     back within <paramref name="readTimeout" />. A bounded timeout (rather than reading to end-of-stream)
    ///     is used deliberately so a test can't hang forever if a scenario leaves the connection open (e.g.
    ///     after switching protocols) instead of closing it.
    /// </summary>
    private static async Task<string> SendRawRequestAndReadResponse(ProxyServer proxy, string rawRequest,
        TimeSpan readTimeout)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, proxy.ProxyEndPoints[0].Port);
        var stream = tcpClient.GetStream();

        await stream.WriteAsync(MsgEncoding.GetBytes(rawRequest));

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(readTimeout);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) break;
                ms.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for more data - the connection was likely kept open (e.g. pooled, or
            // switched to a raw tunnel after a 101). Return whatever was received so far.
        }

        return MsgEncoding.GetString(ms.ToArray());
    }
}
ParseOptions.0.jsonîK
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\NestedProxyTests.csìJusing System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class NestedProxyTests
{
    [TestMethod]
    public async Task Smoke_Test_Nested_Proxy()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy1 = testSuite.GetProxy();
        var proxy2 = testSuite.GetProxy(proxy1);

        var client = testSuite.GetClient(proxy2);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Nested_Proxy_UserData()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy1 = testSuite.GetProxy();
        proxy1.ProxyBasicAuthenticateFunc = async (session, username, password) =>
        {
            session.UserData = "Test";
            return await Task.FromResult(true);
        };

        var proxy2 = testSuite.GetProxy();

        proxy1.GetCustomUpStreamProxyFunc = async session =>
        {
            Assert.AreEqual("Test", session.UserData);

            return await Task.FromResult(new ExternalProxy("localhost", proxy2.ProxyEndPoints[0].Port));
        };

        var client = testSuite.GetClient(proxy1, true);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Upstream_Proxy_Failure_Fails_Over_To_New_Proxy()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("failover ok"));

        // a working upstream proxy the failover callback will switch to
        var workingUpstream = testSuite.GetProxy();

        var proxy = testSuite.GetProxy();
        var failoverInvoked = false;

        // initial upstream points at a closed port so the first connection attempt fails
        proxy.GetCustomUpStreamProxyFunc = _ =>
            Task.FromResult<IExternalProxy>(new ExternalProxy("localhost", 1) { ProxyType = ExternalProxyType.Http });

        proxy.CustomUpStreamProxyFailureFunc = _ =>
        {
            failoverInvoked = true;
            return Task.FromResult<IExternalProxy>(
                new ExternalProxy("localhost", workingUpstream.ProxyEndPoints[0].Port)
                    { ProxyType = ExternalProxyType.Http });
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl),
            new StringContent("hello"));

        Assert.IsTrue(failoverInvoked, "the failover callback should have been invoked");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("failover ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(2 * 60 * 1000)]
    public async Task Nested_Proxy_Farm_Without_Connection_Cache_Should_Not_Hang()
    {
        var rnd = new Random();

        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxies2 = new List<ProxyServer>();

        //create a level 2 upstream proxy farm that forwards to server
        for (var i = 0; i < 10; i++)
        {
            var proxy2 = testSuite.GetProxy();
            proxy2.ProxyBasicAuthenticateFunc += (_, _, _) =>
            {
                return Task.FromResult(true);
            };

            proxies2.Add(proxy2);
        }

        var proxies1 = new List<ProxyServer>();

        //create a level 1 upstream proxy farm that forwards to level 2 farm
        for (var i = 0; i < 10; i++)
        {
            var proxy1 = testSuite.GetProxy();
            proxy1.EnableConnectionPool = false;
            var proxy2 = proxies2[rnd.Next() % proxies2.Count];

            proxy1.GetCustomUpStreamProxyFunc += async _ =>
            {
                var proxy = new ExternalProxy
                {
                    HostName = "localhost",
                    Port = proxy2.ProxyEndPoints[0].Port,
                    ProxyType = ExternalProxyType.Http,
                    UserName = "test_user",
                    Password = "test_password"
                };

                return await Task.FromResult(proxy);
            };

            proxies1.Add(proxy1);
        }

        var tasks = new List<Task>();

        //send multiple concurrent requests from client => proxy farm 1 => proxy farm 2 => server
        for (var j = 0; j < 10_000; j++)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var proxy = proxies1[rnd.Next() % proxies1.Count];
                    using var client = testSuite.GetClient(proxy);

                    //tests should not keep hanging for 30 mins.
                    client.Timeout = TimeSpan.FromMinutes(30);
                    await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                        new StringContent("hello server. I am a client."));
                }
                //if error is thrown because of server getting overloaded its okay.
                //But client.PostAsync should'nt hang in all cases.
                catch { }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
    }


    //Reproduce bug reported so that we can fix it.
    //https://github.com/justcoding121/titanium-web-proxy/issues/826
    [TestMethod]
    [Timeout(2 * 60 * 1000)]
    public async Task Nested_Proxy_Farm_With_Connection_Cache_Should_Not_Hang()
    {
        var rnd = new Random();

        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxies2 = new List<ProxyServer>();

        //create a level 2 upstream proxy farm that forwards to server
        for (var i = 0; i < 10; i++)
        {
            var proxy2 = testSuite.GetProxy();
            proxy2.ProxyBasicAuthenticateFunc += (_, _, _) =>
            {
                return Task.FromResult(true);
            };
            proxies2.Add(proxy2);
        }

        var proxies1 = new List<ProxyServer>();

        //create a level 1 upstream proxy farm that forwards to level 2 farm
        for (var i = 0; i < 10; i++)
        {
            var proxy1 = testSuite.GetProxy();
            var proxy2 = proxies2[rnd.Next() % proxies2.Count];

            proxy1.GetCustomUpStreamProxyFunc += async _ =>
            {
                var proxy = new ExternalProxy
                {
                    HostName = "localhost",
                    Port = proxy2.ProxyEndPoints[0].Port,
                    ProxyType = ExternalProxyType.Http,
                    UserName = "test_user",
                    Password = "test_password"
                };

                return await Task.FromResult(proxy);
            };

            proxies1.Add(proxy1);
        }

        var tasks = new List<Task>();

        //send multiple concurrent requests from client => proxy farm 1 => proxy farm 2 => server
        for (var j = 0; j < 10_000; j++)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var proxy = proxies1[rnd.Next() % proxies1.Count];
                    using var client = testSuite.GetClient(proxy);

                    //tests should not keep hanging for 30 mins.
                    client.Timeout = TimeSpan.FromMinutes(30);
                    await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                        new StringContent("hello server. I am a client."));
                }
                //if error is thrown because of server getting overloaded its okay.
                //But client.PostAsync should'nt hang in all cases.
                catch { }
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
    }
}
ParseOptions.0.json³J
iD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\ReverseProxyTests.cs°Iusing System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ReverseProxyTests
{
    [TestMethod]
    public async Task Smoke_Test_Http_To_Http_Reverse_Proxy()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_To_Http_Reverse_Proxy()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Http_To_Https_Reverse_Proxy()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_To_Https_Reverse_Proxy()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += async (sender, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            await Task.FromResult(0);
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_To_Https_Reverse_Proxy_Tunnel_Without_Decryption()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint =
            proxy.ProxyEndPoints.Where(x => x is TransparentProxyEndPoint).First() as TransparentProxyEndPoint;

        endpoint.BeforeSslAuthenticate += async (sender, e) =>
        {
            e.DecryptSsl = false;
            e.ForwardHttpsPort = server.HttpsListeningPort;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Http_Reverse_Proxy_With_Fixed_Forward_Endpoint()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // forward everything to the fixed backend without rewriting the request in BeforeRequest.
        endpoint.ForwardHost = "localhost";
        endpoint.ForwardPort = server.HttpListeningPort;

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"http://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_Reverse_Proxy_With_Fixed_Forward_Endpoint()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // decrypt and forward to the fixed backend; the original "localhost" host is still
        // used for TLS SNI/certificate validation while only the connection port changes.
        endpoint.ForwardHost = "localhost";
        endpoint.ForwardPort = server.HttpsListeningPort;

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }

    [TestMethod]
    public async Task Smoke_Test_Https_Reverse_Proxy_Tunnel_With_Fixed_Forward_Endpoint()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // configure the fixed forward on the endpoint; the tunnel path should pick it up
        // as the default forward target without a BeforeSslAuthenticate handler.
        endpoint.ForwardPort = server.HttpsListeningPort;
        endpoint.BeforeSslAuthenticate += async (sender, e) =>
        {
            e.DecryptSsl = false;
            await Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();

        var response = await client.PostAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}"),
            new StringContent("hello server. I am a client."));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual("I am server. I received your greetings.", body);
    }
}
ParseOptions.0.jsonå
vD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Setup\TestCertificateAuthority.csÕusing System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

internal static class TestCertificateAuthority
{
    private static readonly Lazy<X509Certificate2> rootCertificate = new(CreateRootCertificate);

    public static X509Certificate2 RootCertificate => rootCertificate.Value;

    public static bool Validate(X509Certificate certificate, SslPolicyErrors sslPolicyErrors)
    {
        const SslPolicyErrors fatalErrors =
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable;

        if (certificate == null || (sslPolicyErrors & fatalErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        var loadedCertificate = certificate as X509Certificate2;
        var disposeCertificate = loadedCertificate == null;
        loadedCertificate ??= X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(RootCertificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(loadedCertificate);
        }
        finally
        {
            if (disposeCertificate)
            {
                loadedCertificate.Dispose();
            }
        }
    }

    private static X509Certificate2 CreateRootCertificate()
    {
        using var proxy = new ProxyServer(false, false, false);
        if (!proxy.CertificateManager.CreateRootCertificate(false) ||
            proxy.CertificateManager.RootCertificate == null)
        {
            throw new InvalidOperationException("Could not create the integration test root certificate.");
        }

        return proxy.CertificateManager.RootCertificate;
    }
}
ParseOptions.0.jsonÒ
mD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Setup\TestProxyServer.csËusing System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

public class TestProxyServer : IDisposable
{
    public TestProxyServer(bool isReverseProxy, ProxyServer upStreamProxy = null)
    {
        ProxyServer = new ProxyServer(false, false, false);
        ProxyServer.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        ProxyServer.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        var explicitEndPoint = isReverseProxy
            ? (ProxyEndPoint)new TransparentProxyEndPoint(IPAddress.Any, 0)
            : new ExplicitProxyEndPoint(IPAddress.Any, 0);

        ProxyServer.AddEndPoint(explicitEndPoint);

        if (upStreamProxy != null)
        {
            ProxyServer.UpStreamHttpProxy = new ExternalProxy("localhost", upStreamProxy.ProxyEndPoints[0].Port);
            ProxyServer.UpStreamHttpsProxy = new ExternalProxy("localhost", upStreamProxy.ProxyEndPoints[0].Port);
        }

        ProxyServer.Start();
    }

    public ProxyServer ProxyServer { get; }

    public int ListeningPort => ProxyServer.ProxyEndPoints[0].Port;

    public CertificateManager CertificateManager => ProxyServer.CertificateManager;

    public void Dispose()
    {
        ProxyServer.Stop();
        ProxyServer.Dispose();
    }
}
ParseOptions.0.json˜%
hD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Setup\TestServer.cs–$using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

// set up a kestrel test server
public class TestServer : IDisposable
{
    private readonly IHost host;

    private Func<HttpContext, Task> requestHandler;
    private Func<ConnectionContext, Task> tcpRequestHandler;

    public TestServer(X509Certificate2 serverCertificate, bool requireMutualTls)
    {
        host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.SetMinimumLevel(LogLevel.Trace);
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup(x => new Startup(() => requestHandler));
                webBuilder.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0);
                    if (requireMutualTls)
                    {
                        options.ConfigureHttpsDefaults(options =>
                        {
                            options.ClientCertificateValidation = (certificate, chain, errors) =>
                            {
                                return true;
                            };
                            options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        });
                    }

                    options.Listen(IPAddress.Loopback, 0, listenOptions =>
                    {
                        listenOptions.UseHttps(serverCertificate);
                    });
                    options.Listen(IPAddress.Loopback, 0, listenOptions =>
                    {
                        listenOptions.Run(context =>
                        {
                            if (tcpRequestHandler == null)
                            {
                                throw new Exception("Test server not configured to handle tcp request.");
                            }

                            return tcpRequestHandler(context);
                        });
                    });
                });
            })
            .Build();

        host.Start();

        var addresses = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            .Addresses.ToArray();

        HttpListeningPort = new Uri(addresses[0]).Port;
        HttpsListeningPort = new Uri(addresses[1]).Port;
        TcpListeningPort = new Uri(addresses[2]).Port;
    }

    public string ListeningHttpUrl => $"http://localhost:{HttpListeningPort}";
    public string ListeningHttpsUrl => $"https://localhost:{HttpsListeningPort}";
    public string ListeningTcpUrl => $"http://localhost:{TcpListeningPort}";

    public int HttpListeningPort { get; }
    public int HttpsListeningPort { get; }
    public int TcpListeningPort { get; }

    public void Dispose()
    {
        host.StopAsync().Wait();
        host.Dispose();
    }

    public void HandleRequest(Func<HttpContext, Task> requestHandler)
    {
        this.requestHandler = requestHandler;
    }

    public void HandleTcpRequest(Func<ConnectionContext, Task> tcpRequestHandler)
    {
        this.tcpRequestHandler = tcpRequestHandler;
    }

    private class Startup
    {
        private readonly Func<Func<HttpContext, Task>> requestHandler;

        public Startup(Func<Func<HttpContext, Task>> requestHandler)
        {
            this.requestHandler = requestHandler;
        }

        public void Configure(IApplicationBuilder app)
        {
            app.Run(context =>
            {
                if (requestHandler == null)
                {
                    throw new Exception("Test server not configured to handle request.");
                }

                return requestHandler()(context);
            });
        }

        public void ConfigureServices(IServiceCollection services)
        {
        }
    }
}
ParseOptions.0.jsonå
gD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Setup\TestSuite.csäusing System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

public class TestSuite : IDisposable
{
    private readonly TestServer server;
    private readonly ConcurrentBag<HttpClient> clients = new();
    private readonly List<ProxyServer> proxyServers = new();
    private bool disposed;

    public TestSuite(bool requireMutualTls = false)
    {
        using var dummyProxy = new ProxyServer(false, false, false);
        dummyProxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        var serverCertificate = dummyProxy.CertificateManager.CreateServerCertificate("localhost").Result;
        server = new TestServer(serverCertificate, requireMutualTls);
    }

    public TestServer GetServer()
    {
        return server;
    }

    public ProxyServer GetProxy(ProxyServer upStreamProxy = null)
    {
        var proxyServer = new TestProxyServer(false, upStreamProxy).ProxyServer;
        proxyServers.Add(proxyServer);
        return proxyServer;
    }

    public ProxyServer GetReverseProxy(ProxyServer upStreamProxy = null)
    {
        var proxyServer = new TestProxyServer(true, upStreamProxy).ProxyServer;
        proxyServers.Add(proxyServer);
        return proxyServer;
    }

    public HttpClient GetClient(ProxyServer proxyServer, bool enableBasicProxyAuthorization = false)
    {
        var client = TestHelper.GetHttpClient(proxyServer.ProxyEndPoints[0].Port, enableBasicProxyAuthorization);
        clients.Add(client);
        return client;
    }

    public HttpClient GetReverseProxyClient()
    {
        var client = TestHelper.GetHttpClient();
        clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        foreach (var client in clients)
        {
            client.Dispose();
        }

        for (var i = proxyServers.Count - 1; i >= 0; i--)
        {
            proxyServers[i].Dispose();
        }

        server.Dispose();
    }
}
ParseOptions.0.json²m
jD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\StreamingBodyTests.cs®lusing System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class StreamingBodyTests
{
    // The per-chunk body-write hook only runs for plain (non-TLS) HTTP, because it operates directly on the
    // network stream. These tests therefore go over http:// so the hook is exercised.

    [TestMethod]
    public async Task OnResponseBodyWrite_Passthrough_Is_Byte_For_Byte()
    {
        using var testSuite = new TestSuite();

        const string expected = "I am server. I received your greetings.";

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync(expected));

        var proxy = testSuite.GetProxy();

        var callbackCount = 0;
        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            callbackCount++;
            // leave the bytes unchanged
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected, body);
        Assert.IsTrue(callbackCount > 0, "The response body write hook should have been invoked.");
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Can_Rewrite_Body()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("hello world"));

        var proxy = testSuite.GetProxy();

        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("HELLO WORLD", body);
    }

    [TestMethod]
    public async Task Large_Response_Streams_Incrementally_Without_Full_Buffering()
    {
        using var testSuite = new TestSuite();

        // ~1 MB body so it spans many buffer-sized reads.
        const int totalSize = 1024 * 1024;
        var payload = new byte[totalSize];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.Body.WriteAsync(payload, 0, payload.Length));

        var proxy = testSuite.GetProxy();

        var callbackCount = 0;
        long observedBytes = 0;
        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            Interlocked.Increment(ref callbackCount);
            observedBytes += e.BodyBytes.Length;
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(totalSize, body.Length);
        CollectionAssert.AreEqual(payload, body);

        // The hook must have fired multiple times (proving the body was streamed in pieces, not buffered whole).
        Assert.IsTrue(callbackCount > 1,
            $"Expected the body to stream in multiple pieces but the hook fired {callbackCount} time(s).");
        Assert.AreEqual(totalSize, observedBytes);
    }

    [TestMethod]
    public async Task RespondStreaming_Chunked_Generates_Body_Without_Contacting_Server()
    {
        using var testSuite = new TestSuite();

        var serverCalled = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            serverCalled = true;
            return context.Response.WriteAsync("from server");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };

            e.RespondStreaming(response, async (stream, ct) =>
            {
                foreach (var part in new[] { "chunk1", "chunk2", "chunk3" })
                {
                    var bytes = Encoding.ASCII.GetBytes(part);
                    await stream.WriteAsync(bytes, 0, bytes.Length, ct);
                }
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("chunk1chunk2chunk3", body);
        Assert.IsFalse(serverCalled, "Server should not be contacted for a synthetic streamed response.");
    }

    [TestMethod]
    public async Task RespondStreaming_FixedLength_Writes_Raw_With_ContentLength()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("from server"));

        var payload = Encoding.ASCII.GetBytes("0123456789ABCDEF");

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };
            response.Headers.AddHeader(KnownHeaders.ContentLength, payload.Length.ToString());

            e.RespondStreaming(response, async (stream, ct) =>
            {
                // write in two pieces to prove streaming
                await stream.WriteAsync(payload, 0, 8, ct);
                await stream.WriteAsync(payload, 8, payload.Length - 8, ct);
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(payload, body);
        Assert.AreEqual(payload.Length, response.Content.Headers.ContentLength);
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Tls_Decrypted_Http11_Body_Relays_Correctly_And_Hook_Fires()
    {
        // The per-chunk body-write hook gate in HttpStream.CopyBodyAsync checks the internal
        // ITransportCapableStream.SupportsBodyWriteHook capability instead of the old IsNetworkStream flag,
        // and HttpStream reports that capability as true whenever its backing stream is either a plain
        // NetworkStream or a decrypted SslStream. So OnResponseBodyWrite must fire with parity for a
        // TLS-decrypted HTTP/1.x connection, exactly as it already does for plain HTTP.
        using var testSuite = new TestSuite();

        const string expected = "I am server. I received your greetings.";

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync(expected));

        var proxy = testSuite.GetProxy();

        var callbackCount = 0;
        var observedBytes = new List<byte>();
        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            callbackCount++;
            observedBytes.AddRange(e.BodyBytes);
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected, body);
        Assert.IsTrue(callbackCount > 0,
            "The response body write hook should now fire for TLS-decrypted HTTP/1.x connections too.");
        Assert.AreEqual(expected, Encoding.ASCII.GetString(observedBytes.ToArray()),
            "The hook should observe the same bytes that were relayed to the client.");
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Tls_Decrypted_Http11_Can_Rewrite_Body()
    {
        // Companion to the read-only test above: proves the hook is not just invoked but its mutation of
        // e.BodyBytes is actually relayed to the client for a TLS-decrypted HTTP/1.x connection, matching
        // the plain-HTTP behavior in OnResponseBodyWrite_Can_Rewrite_Body.
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("hello world"));

        var proxy = testSuite.GetProxy();

        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("HELLO WORLD", body);
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Http2_Can_Rewrite_Body()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("hello world"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        using var client = CreateHttp2Client(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual("HELLO WORLD", body);
    }

    [TestMethod]
    public async Task RespondStreaming_Http2_Generates_Body_Without_Contacting_Server()
    {
        using var testSuite = new TestSuite();

        var serverCalled = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            serverCalled = true;
            return context.Response.WriteAsync("from server");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        proxy.BeforeRequest += (sender, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };

            e.RespondStreaming(response, async (stream, ct) =>
            {
                foreach (var part in new[] { "chunk1", "chunk2", "chunk3" })
                {
                    var bytes = Encoding.ASCII.GetBytes(part);
                    await stream.WriteAsync(bytes, 0, bytes.Length, ct);
                }
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        using var client = CreateHttp2Client(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual("chunk1chunk2chunk3", body);
        Assert.IsFalse(serverCalled, "Server should not be contacted for a synthetic streamed response.");
    }

    private static HttpClient CreateHttp2Client(ProxyServer proxy)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };

        return new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }
}
ParseOptions.0.jsonì

cD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\StressTests.csï	using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class StressTests
{
    [TestMethod]
    [Timeout(2 * 60 * 1000)]
    public async Task Stress_Test_With_One_Server_And_Many_Clients()
    {
        using var testSuite = new TestSuite();

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            return context.Response.WriteAsync("I am server. I received your greetings.");
        });

        using var proxy = testSuite.GetProxy();

        await Task.Delay(1000);

        var tasks = new List<Task>();

        //send 100 requests to server
        for (var j = 0; j < 100; j++)
        {
            var task = Task.Run(async () =>
            {
                using var client = testSuite.GetClient(proxy);

                await client.PostAsync(new Uri(server.ListeningHttpsUrl),
                    new StringContent("hello server. I am a client."));
            });

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
    }
}
ParseOptions.0.json…
nD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\UpstreamProxyAuthTests.csýusing System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class UpstreamProxyAuthTests
{
    [TestMethod]
    public async Task Authenticates_Https_Connect_To_Upstream_Proxy()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("secure target response"));

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreateProxy(testSuite, upstreamProxy, useForHttps: true);
        using var client = testSuite.GetClient(proxy);

        var body = await client.GetStringAsync(server.ListeningHttpsUrl);

        Assert.AreEqual("secure target response", body);
        CollectionAssert.AreEqual(new[] { string.Empty, "NTLM t1", "NTLM t2" },
            upstreamProxy.ProxyAuthorizationValues.ToArray());
    }

    [TestMethod]
    public async Task Authenticates_Plain_Http_Request_To_Upstream_Proxy()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();

        using var upstreamProxy = new FakeUpstreamProxy(server.HttpsListeningPort);
        using var proxy = CreateProxy(testSuite, upstreamProxy, useForHttps: false);
        using var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(server.ListeningHttpUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode,
            string.Join(", ", upstreamProxy.ProxyAuthorizationValues));
        Assert.AreEqual("authenticated plain HTTP", body);
        CollectionAssert.AreEqual(new[] { string.Empty, "NTLM t1", "NTLM t2" },
            upstreamProxy.ProxyAuthorizationValues.ToArray());
    }

    private static ProxyServer CreateProxy(TestSuite testSuite, FakeUpstreamProxy upstreamProxy, bool useForHttps)
    {
        var proxy = testSuite.GetProxy();
        var externalProxy = new ExternalProxy("localhost", upstreamProxy.Port)
        {
            UseDefaultCredentials = true
        };

        if (useForHttps)
            proxy.UpStreamHttpsProxy = externalProxy;
        else
            proxy.UpStreamHttpProxy = externalProxy;

        // EnableWinAuth must not corrupt the upstream proxy authentication state on a 407.
        proxy.EnableWinAuth = true;

        proxy.UpstreamProxyWinAuthTokenGenerator = (_, _, challenge, _) =>
            challenge == null ? " t1" : " t2";
        return proxy;
    }
}
ParseOptions.0.jsonã
rC:\Users\runneradmin\.nuget\packages\microsoft.net.test.sdk\17.14.1\build\net8.0\Microsoft.NET.Test.Sdk.Program.cs×// <auto-generated> This file has been auto generated. </auto-generated>
using System;
[Microsoft.VisualStudio.TestPlatform.TestSDKAutoGeneratedCode]
class AutoGeneratedProgram {static void Main(string[] args){}}ParseOptions.0.jsonû
˜D:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\obj\Release\net10.0\.NETCoreApp,Version=v10.0.AssemblyAttributes.csÈ// <autogenerated />
using System;
using System.Reflection;
[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]
ParseOptions.0.jsonð	
œD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\obj\Release\net10.0\Titanium.Web.Proxy.IntegrationTests.AssemblyInfo.cs¹//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

using System;
using System.Reflection;

[assembly: System.Reflection.AssemblyCompanyAttribute("Titanium.Web.Proxy.IntegrationTests")]
[assembly: System.Reflection.AssemblyConfigurationAttribute("Release")]
[assembly: System.Reflection.AssemblyFileVersionAttribute("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0+c2a21211b4a7a84a0ed9d585154ffb3535e0a2a7")]
[assembly: System.Reflection.AssemblyProductAttribute("Titanium.Web.Proxy.IntegrationTests")]
[assembly: System.Reflection.AssemblyTitleAttribute("Titanium.Web.Proxy.IntegrationTests")]
[assembly: System.Reflection.AssemblyVersionAttribute("1.0.0.0")]

// Generated by the MSBuild WriteCodeFragment class.

ParseOptions.0.json
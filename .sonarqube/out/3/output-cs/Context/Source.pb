ƒ
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
ParseOptions.0.jsonµ
jD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\Helpers\TestHelper.cs±using System;
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
ParseOptions.0.jsonžU
jD:\a\titanium-web-proxy\titanium-web-proxy\tests\Titanium.Web.Proxy.IntegrationTests\StreamingBodyTests.csšTusing System;
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
[assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0+d3f1bf609a3eb2e6e273820f305bb4f6cb5ddb25")]
[assembly: System.Reflection.AssemblyProductAttribute("Titanium.Web.Proxy.IntegrationTests")]
[assembly: System.Reflection.AssemblyTitleAttribute("Titanium.Web.Proxy.IntegrationTests")]
[assembly: System.Reflection.AssemblyVersionAttribute("1.0.0.0")]

// Generated by the MSBuild WriteCodeFragment class.

ParseOptions.0.json
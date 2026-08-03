using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Phase 0 characterization tests that lock in observable proxy behavior before later
///     milestones change request/response handling. Failing tests here indicate a regression
///     in baseline protocol compliance.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Phase0CharacterizationTests
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

    /// <summary>
    ///     A strict Expect: 100-continue client that waits 500 ms for the 100 response before
    ///     sending the body will deadlock when the proxy has Enable100ContinueBehaviour disabled
    ///     (the default) because the proxy never forwards the origin's 100 Continue to the client.
    /// </summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task ExpectContinue_StrictClient_Deadlocks_When_100ContinueBehaviour_Disabled()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer
        {
            ExpectationResponse = HttpStatusCode.Continue,
            ResponseBody = "hello"
        };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        // Enable100ContinueBehaviour is false by default - do not enable it.
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "body text");

        Assert.IsNull(response,
            "Strict Expect:100-continue client must time out when proxy does not forward 100 Continue " +
            "(Enable100ContinueBehaviour is disabled by default).");
    }

    /// <summary>
    ///     A permissive HttpClient that sends Expect: 100-continue but does not strictly gate the
    ///     body on receiving 100 will still get a successful response even when the proxy has
    ///     Enable100ContinueBehaviour disabled, because it sends the body after a brief delay
    ///     regardless of whether a 100 arrives.
    /// </summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task ExpectContinue_PermissiveClient_Succeeds_When_100ContinueBehaviour_Disabled()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            _ = await new System.IO.StreamReader(context.Request.Body).ReadToEndAsync();
            await context.Response.WriteAsync("hello from server");
        });

        var proxy = testSuite.GetReverseProxy();
        // Enable100ContinueBehaviour is false by default - do not enable it.
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        var response = await client.PostAsync(
            $"http://localhost:{proxy.ProxyEndPoints[0].Port}/",
            new StringContent("hello server"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "A permissive HttpClient must complete the POST even when the proxy does not forward 100 Continue.");
    }

    /// <summary>
    ///     When the origin responds to a HEAD request with Content-Length: 1000 but sends no body
    ///     bytes, the proxy must not attempt to read 1000 bytes from the origin connection. The
    ///     client must receive the response headers only (no body), and a subsequent request over
    ///     a new connection must still succeed, confirming the proxy is not poisoned.
    /// </summary>
    [TestMethod]
    [Timeout(15000)]
    public async Task Head_Response_ContentLength_Does_Not_Cause_Body_Read()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        server.HandleTcpRequest(async context =>
        {
            var encoding = Encoding.ASCII;
            var requestMsg = string.Empty;
            Request? request = null;
            while ((request = HttpMessageParsing.ParseRequest(requestMsg, false)) == null)
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var segment in result.Buffer)
                    requestMsg += encoding.GetString(segment.Span);
                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            string responseText;
            if (request.Method == "HEAD")
            {
                // Advertise a 1000-byte body but send no bytes - HEAD semantics per RFC 7231 4.3.2.
                responseText = "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n";
            }
            else
            {
                const string body = "second-ok";
                responseText =
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            }

            await context.Transport.Output.WriteAsync(encoding.GetBytes(responseText));
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        int proxyPort = proxy.ProxyEndPoints[0].Port;

        // First request: HEAD via raw TcpClient.
        using (var tcpClient = new TcpClient())
        {
            await tcpClient.ConnectAsync("localhost", proxyPort);
            var stream = tcpClient.GetStream();
            stream.ReadTimeout = 5000;

            var headRequest = encoding.GetBytes("HEAD / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headRequest);

            var received = new List<byte>();
            var buf = new byte[1024];
            string? headersSection = null;

            while (headersSection == null)
            {
                int read = await stream.ReadAsync(buf, 0, buf.Length);
                if (read == 0) break;
                received.AddRange(buf.Take(read));
                var text = Encoding.ASCII.GetString(received.ToArray());
                int idx = text.IndexOf("\r\n\r\n");
                if (idx >= 0)
                {
                    headersSection = text.Substring(0, idx);
                    var bodyBytes = text.Substring(idx + 4);
                    Assert.AreEqual(0, bodyBytes.Length,
                        "HEAD response must not carry any body bytes through the proxy.");
                }
            }

            Assert.IsNotNull(headersSection, "Proxy did not return a response to the HEAD request.");
            Assert.IsTrue(headersSection.StartsWith("HTTP/1.1 200"),
                $"Expected HTTP/1.1 200, got: {headersSection}");
        }

        // Second request: GET via a new connection - confirms the proxy connection state is clean.
        using (var tcpClient2 = new TcpClient())
        {
            await tcpClient2.ConnectAsync("localhost", proxyPort);
            var stream2 = tcpClient2.GetStream();
            stream2.ReadTimeout = 5000;

            var getRequest = encoding.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            await stream2.WriteAsync(getRequest);

            var received2 = new List<byte>();
            var buf2 = new byte[1024];
            while (true)
            {
                int read;
                try { read = await stream2.ReadAsync(buf2, 0, buf2.Length); }
                catch { break; }
                if (read == 0) break;
                received2.AddRange(buf2.Take(read));
                var text = Encoding.ASCII.GetString(received2.ToArray());
                if (HttpMessageParsing.ParseResponse(text) != null)
                    break;
            }

            var fullResponse = Encoding.ASCII.GetString(received2.ToArray());
            Assert.IsTrue(fullResponse.StartsWith("HTTP/1.1 200"),
                $"Second request after HEAD must still succeed; got: {fullResponse}");
        }
    }

    /// <summary>
    ///     Positive control: when Enable100ContinueBehaviour is true the proxy relays the 100
    ///     Continue to the strict client, which then sends its body and receives a final 200 OK.
    /// </summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task Enable100Continue_True_StrictClient_Succeeds()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var continueServer = new HttpContinueServer
        {
            ExpectationResponse = HttpStatusCode.Continue,
            ResponseBody = "ok"
        };
        server.HandleTcpRequest(continueServer.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "hello");

        Assert.IsNotNull(response, "Enable100ContinueBehaviour=true must relay 100 Continue and return a final response.");
        Assert.AreEqual((int)HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     Characterization: when the origin never responds (silence), the strict client also
    ///     times out because no 100 Continue is ever relayed to it by the proxy. This isolates
    ///     the proxy-to-client half of the deadlock independently of origin behaviour.
    /// </summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task Enable100Continue_False_OriginSilence_TimesOut()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleTcpRequest(async context =>
        {
            try { await context.Transport.Input.ReadAsync(context.ConnectionClosed); } catch { }
            await Task.Delay(Timeout.Infinite, context.ConnectionClosed).ConfigureAwait(false);
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var client = new HttpContinueClient();
        var response = await client.Post("localhost", proxy.ProxyEndPoints[0].Port, "hello");

        Assert.IsNull(response,
            "Origin silence + strict client must time out when Enable100ContinueBehaviour=false.");
    }

    /// <summary>
    ///     Characterization: documents and locks in the default values for the new protocol
    ///     policy properties added in Phase 0. These become the known baseline that later phases
    ///     wire up to actual enforcement logic.
    /// </summary>
    [TestMethod]
    public void ProxyServer_Has_Protocol_Policy_Defaults()
    {
        using var proxy = new ProxyServer();

        Assert.AreEqual(64 * 1024, proxy.MaxDecodedHeaderListBytes,
            "Default decoded header list limit must be 64 KiB.");
        Assert.AreEqual(4 * 1024 * 1024, proxy.MaxBufferedBodyBytes,
            "Default buffered body limit must be 4 MiB.");
        Assert.AreEqual(16 * 1024 * 1024, proxy.MaxWebSocketFramePayloadBytes,
            "Default WebSocket frame payload limit must be 16 MiB.");
        Assert.AreEqual("titanium-web-proxy", proxy.ViaHeaderPseudonym,
            "Via header pseudonym must default to 'titanium-web-proxy'.");
    }

    private static readonly Encoding encoding = Encoding.ASCII;

    /// <summary>
    ///     HttpClient must receive the declared Content-Length from a HEAD response but read
    ///     zero body bytes, even when the response travels through the reverse proxy.
    /// </summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task Head_Response_Via_HttpClient_Returns_No_Body()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.ContentLength = 42;
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningHttpsUrl;
            return Task.CompletedTask;
        };

        var client = testSuite.GetReverseProxyClient();
        var request = new HttpRequestMessage(HttpMethod.Head,
            $"http://localhost:{proxy.ProxyEndPoints[0].Port}/");
        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(42L, response.Content.Headers.ContentLength,
            "Proxy must preserve Content-Length from a HEAD response.");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(0, body.Length,
            "HEAD response body must be empty even though Content-Length is declared.");
    }
}

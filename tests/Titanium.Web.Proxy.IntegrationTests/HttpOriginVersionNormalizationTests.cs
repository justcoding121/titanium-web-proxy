using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Covers <see cref="OriginHttpVersionPolicy" />: the proxy's ability to declare a different HTTP version to
///     the origin than the one the client itself declared, so a compliant origin can be pooled/reused as a
///     persistent HTTP/1.1 connection regardless of whether individual clients are still speaking HTTP/1.0.
/// </summary>
[DoNotParallelize]
[TestClass]
public class HttpOriginVersionNormalizationTests
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

    private const int SendReceiveTimeoutMs = 2000;

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Default_PreserveClientVersion_DeclaresClientVersionToOrigin_AndDoesNotPoolHttp10WithoutKeepAlive()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var origin = new HttpVersionMirroringOriginServer();
        server.HandleTcpRequest(origin.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        // OriginHttpVersionPolicy left at its default (PreserveClientVersion) - existing pass-through behavior.
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var response1 = await SendRawRequestAsync(proxy.ProxyEndPoints[0].Port, new Version(1, 0), null);
        Assert.IsNotNull(response1, "No response to the first HTTP/1.0 request.");
        Assert.AreEqual(200, response1.StatusCode);
        Assert.AreEqual("1.0", origin.LastObservedRequestVersion,
            "PreserveClientVersion must declare the client's own HTTP/1.0 version to the origin verbatim.");

        var response2 = await SendRawRequestAsync(proxy.ProxyEndPoints[0].Port, new Version(1, 0), null);
        Assert.IsNotNull(response2, "No response to the second HTTP/1.0 request.");

        Assert.AreEqual(2, origin.AcceptedConnectionCount,
            "An HTTP/1.0 request with no explicit 'Connection: keep-alive' must not be pooled/reused at the " +
            "origin under the default PreserveClientVersion policy - this is unchanged, pre-existing behavior.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task NormalizeToHttp11_DeclaresHttp11ToOrigin_AndPoolsAcrossHttp10AndHttp11Clients()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var origin = new HttpVersionMirroringOriginServer();
        server.HandleTcpRequest(origin.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        proxy.OriginHttpVersionPolicy = OriginHttpVersionPolicy.NormalizeToHttp11;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        // An HTTP/1.0 client, declaring no Connection header of its own, is the scenario that could never be
        // pooled under PreserveClientVersion.
        var response1 = await SendRawRequestAsync(proxy.ProxyEndPoints[0].Port, new Version(1, 0), null);
        Assert.IsNotNull(response1, "No response to the HTTP/1.0 client request.");
        Assert.AreEqual(200, response1.StatusCode);
        Assert.AreEqual("1.1", origin.LastObservedRequestVersion,
            "NormalizeToHttp11 must always declare HTTP/1.1 to the origin, regardless of the client's own " +
            "declared version.");

        // A second, independent HTTP/1.1 client request to the same origin must be able to reuse the exact same
        // pooled origin connection the HTTP/1.0 client's request left behind.
        var response2 = await SendRawRequestAsync(proxy.ProxyEndPoints[0].Port, new Version(1, 1), null);
        Assert.IsNotNull(response2, "No response to the HTTP/1.1 client request.");
        Assert.AreEqual(200, response2.StatusCode);
        Assert.AreEqual("1.1", origin.LastObservedRequestVersion);

        Assert.AreEqual(1, origin.AcceptedConnectionCount,
            "Under NormalizeToHttp11, an HTTP/1.0 client and an HTTP/1.1 client hitting the same origin must " +
            "share one pooled, persistent origin connection instead of opening a new one per client.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http10ClientWithExplicitKeepAlive_DefaultPolicy_StillPoolsAtOrigin()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var origin = new HttpVersionMirroringOriginServer();
        server.HandleTcpRequest(origin.HandleRequest);

        var proxy = testSuite.GetReverseProxy();
        // Default policy (PreserveClientVersion): this is pre-existing, unaffected-by-this-feature behavior -
        // an HTTP/1.0 client that explicitly opts in with "Connection: keep-alive" was already poolable.
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        var response1 = await SendRawRequestAsync(proxy.ProxyEndPoints[0].Port, new Version(1, 0),
            KnownHeaders.ConnectionKeepAlive.String);
        Assert.IsNotNull(response1, "No response to the first HTTP/1.0 keep-alive request.");
        Assert.AreEqual("1.0", origin.LastObservedRequestVersion);

        var response2 = await SendRawRequestAsync(proxy.ProxyEndPoints[0].Port, new Version(1, 0),
            KnownHeaders.ConnectionKeepAlive.String);
        Assert.IsNotNull(response2, "No response to the second HTTP/1.0 keep-alive request.");

        Assert.AreEqual(1, origin.AcceptedConnectionCount,
            "An HTTP/1.0 client that explicitly declares 'Connection: keep-alive' must still be pooled at the " +
            "origin, exactly as before this feature existed.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http10OriginResponse_WithoutContentLength_IsReadUntilConnectionClose()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();

        const string responseBody = "no-content-length-body-read-until-close";

        server.HandleTcpRequest(async context =>
        {
            var requestMsg = string.Empty;
            Request? request;
            while ((request = HttpMessageParsing.ParseRequest(requestMsg, false)) == null)
            {
                var result = await context.Transport.Input.ReadAsync();
                foreach (var seg in result.Buffer)
                    requestMsg += HttpHelper.GetEncodingFromContentType(null).GetString(seg.Span);
                context.Transport.Input.AdvanceTo(result.Buffer.End);
            }

            // No Content-Length and no Transfer-Encoding: per HTTP/1.0 semantics, the client must read the body
            // until the connection is closed.
            var responseText = "HTTP/1.0 200 OK\r\n\r\n" + responseBody;
            await context.Transport.Output.WriteAsync(Encoding.ASCII.GetBytes(responseText));
            context.Transport.Output.Complete();
        });

        var proxy = testSuite.GetReverseProxy();
        // Default policy: the proxy also declares HTTP/1.0 to this origin, matching the origin's own response
        // version - exercising the pre-existing read-until-close path this feature must not regress.
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Url = server.ListeningTcpUrl;
            return Task.CompletedTask;
        };

        using var client = new TcpClient("localhost", proxy.ProxyEndPoints[0].Port)
        {
            SendTimeout = SendReceiveTimeoutMs, ReceiveTimeout = SendReceiveTimeoutMs
        };

        var request = new Request { Method = "GET", RequestUriString = "/", HttpVersion = new Version(1, 0) };
        request.Headers.AddHeader(KnownHeaders.Host, "localhost");

        var encoding = HttpHelper.GetEncodingFromContentType(null);
        var headerBytes = encoding.GetBytes(request.HeaderText);

        var stream = client.GetStream();
        await stream.WriteAsync(headerBytes);

        // No Content-Length was declared on this response (by either the origin or the proxy, since the client
        // itself is also HTTP/1.0 under the default PreserveClientVersion policy), so the only correct way to
        // know the body is complete is to read until the proxy closes this client-facing connection too.
        var received = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) received.Write(buffer, 0, read);

        var rawResponse = encoding.GetString(received.ToArray());
        var headerEnd = rawResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.IsTrue(headerEnd >= 0, $"Malformed response, no header/body separator found: '{rawResponse}'.");

        var statusLine = rawResponse.Substring(0, rawResponse.IndexOf("\r\n", StringComparison.Ordinal));
        Assert.IsTrue(statusLine.Contains("200"), $"Expected a 200 status line, got: '{statusLine}'.");

        var body = rawResponse.Substring(headerEnd + 4);
        Assert.AreEqual(responseBody, body);
    }

    /// <summary>
    ///     Sends one raw plain-HTTP GET request, declaring the given version on the request line, to the proxy's
    ///     endpoint over a brand new TCP connection (simulating an independent client), and returns the parsed
    ///     response. <paramref name="connectionHeaderValue" /> is included as an explicit "Connection" request
    ///     header when non-null.
    /// </summary>
    private static async Task<Response?> SendRawRequestAsync(int proxyPort, Version version,
        string? connectionHeaderValue)
    {
        using var client = new TcpClient("localhost", proxyPort)
        {
            SendTimeout = SendReceiveTimeoutMs, ReceiveTimeout = SendReceiveTimeoutMs
        };

        var request = new Request { Method = "GET", RequestUriString = "/", HttpVersion = version };
        request.Headers.AddHeader(KnownHeaders.Host, "localhost");
        if (connectionHeaderValue != null)
            request.Headers.AddHeader(KnownHeaders.Connection, connectionHeaderValue);

        var encoding = HttpHelper.GetEncodingFromContentType(null);
        var headerBytes = encoding.GetBytes(request.HeaderText);

        var stream = client.GetStream();
        await stream.WriteAsync(headerBytes);

        var buffer = new byte[4096];
        var responseMsg = string.Empty;
        Response? response;
        var deadline = DateTime.UtcNow.AddMilliseconds(SendReceiveTimeoutMs * 5);

        while ((response = HttpMessageParsing.ParseResponse(responseMsg)) == null)
        {
            if (DateTime.UtcNow > deadline) return null;

            int read;
            try
            {
                read = await stream.ReadAsync(buffer);
            }
            catch (IOException)
            {
                return null;
            }

            if (read == 0) return null;

            responseMsg += encoding.GetString(buffer, 0, read);
        }

        return response;
    }
}

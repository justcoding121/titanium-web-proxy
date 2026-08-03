using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.Models;

using Titanium.Web.Proxy.IntegrationTests.Setup;
namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Tests for the HTTP/1.1-to-HTTP/2 origin bridge: 1xx interim-response relay infrastructure and
///     bounded NTLM/Kerberos auth retry rounds.
/// </summary>
[DoNotParallelize]
[TestClass]
public class InterimResponseBridgeTests
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

    private static readonly Encoding Ascii = Encoding.ASCII;

    /// <summary>
    ///     Baseline: the h1?h2 bridge completes a plain round trip and returns the correct body.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H11ToH2Bridge_Final_Response_Is_Delivered_Correctly()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        var requestBytes = Ascii.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new StreamReader(tunnel.SslStream, Ascii, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected HTTP/1.1 200, got: '{statusLine}'.");

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
            // drain response headers
        }

        var body = await reader.ReadToEndAsync();
        Assert.AreEqual("ok", body);
    }

    /// <summary>
    ///     Multiple sequential keep-alive requests on the same h1?h2 bridge tunnel all complete with
    ///     correct status codes, exercising the persistent <see cref="Http2OriginConnection" /> stream reuse.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H11ToH2Bridge_Multiple_Sequential_Requests_All_Complete()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.WriteAsync("pong"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var tunnel = await Http2RawClient.ConnectTunnelWithAlpnAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort,
            new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 });

        // A single StreamReader shared across all requests so that bytes buffered from one response
        // are not lost when parsing the next (StreamReader maintains an internal look-ahead buffer).
        using var reader = new StreamReader(tunnel.SslStream, Ascii, false, 4096, leaveOpen: true);

        var requestBytes = Ascii.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
        for (var i = 0; i < 3; i++)
        {
            await tunnel.SslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

            // Read status line and headers.
            var statusLine = await reader.ReadLineAsync();
            Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
                $"Request {i + 1}: expected HTTP/1.1 200 but got: '{statusLine}'.");

            var contentLength = 0;
            string? headerLine;
            while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync()))
                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(headerLine.Substring("Content-Length:".Length).Trim());

            // Read exactly contentLength chars (ASCII: 1 byte = 1 char).
            var bodyChars = new char[contentLength];
            var bodyRead = 0;
            while (bodyRead < contentLength)
            {
                var n = await reader.ReadAsync(bodyChars, bodyRead, contentLength - bodyRead);
                if (n == 0) break;
                bodyRead += n;
            }

            Assert.AreEqual("pong", new string(bodyChars, 0, bodyRead),
                $"Request {i + 1}: unexpected body.");
        }
    }

    /// <summary>
    ///     A server that always returns 401 with a non-NTLM scheme (Basic) should be relayed to the client
    ///     without retrying. The proxy only performs automatic retries for NTLM/Kerberos/Negotiate;
    ///     Basic auth challenges are passed through unchanged.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Auth_Challenge_With_Basic_Scheme_Is_Relayed_Without_Retry()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var hitCount = 0;
        server.HandleRequest(context =>
        {
            Interlocked.Increment(ref hitCount);
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"test\"";
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetProxy();

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));

        // The proxy should pass through the 401 without retrying (Basic is not NTLM/Negotiate/Kerberos).
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "Proxy must relay the 401 response without automatically retrying Basic auth challenges.");

        // The origin should have been hit exactly once (no proxy-side retry loop).
        Assert.AreEqual(1, hitCount,
            $"The origin should have been contacted exactly once for a Basic auth 401; actual: {hitCount}.");
    }
}

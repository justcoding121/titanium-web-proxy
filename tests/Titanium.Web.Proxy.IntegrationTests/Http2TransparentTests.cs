using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Coverage for HTTP/2 routed through the transparent/reverse-proxy endpoint via the same shared
///     negotiation coordinator (<c>NegotiateHttp2Async</c>/<c>AdoptRetainedConnectionAsync</c>) the explicit
///     CONNECT handler uses, rather than a separate, duplicated implementation.
/// </summary>
[TestClass]
public class Http2TransparentTests
{
    private static X509Certificate2 CreateOriginCertificate()
    {
        using var dummyProxy = new ProxyServer(false, false, false);
        dummyProxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        return dummyProxy.CertificateManager.CreateServerCertificate("localhost").Result;
    }

    private static HttpClient GetHttp2DirectClient()
    {
        var handler = new SocketsHttpHandler
        {
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

    /// <summary>
    ///     A minimal raw TLS/HTTP-1.1-only origin - unlike <see cref="Http2RawOriginServer" />, whose ALPN
    ///     offer is always exactly "h2", this never advertises "h2" at all, so it can stand in for a
    ///     real-world origin that genuinely does not support HTTP/2.
    /// </summary>
    private sealed class Http11OnlyOriginServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private bool disposed;

        public Http11OnlyOriginServer(X509Certificate2 certificate)
        {
            this.certificate = certificate;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

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
                            ApplicationProtocols =
                                new System.Collections.Generic.List<SslApplicationProtocol>
                                    { SslApplicationProtocol.Http11 },
                            EnabledSslProtocols = SslProtocols.None
                        });

                        using var reader = new System.IO.StreamReader(sslStream, Encoding.ASCII, false, 4096, true);
                        string line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                        {
                            // drain request headers
                        }

                        var body = "h1-only-origin-ok";
                        var response = "HTTP/1.1 200 OK\r\n" +
                                       $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n" +
                                       "Connection: close\r\n\r\n" + body;
                        var responseBytes = Encoding.ASCII.GetBytes(response);
                        await sslStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    }
                    catch
                    {
                        // best-effort test double; failures surface via the test's own assertions.
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

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Fixed_Forward_To_Http2_Origin_Succeeds()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        // fixed forward target: the actual TCP destination ("127.0.0.1") differs from the client's SNI/the
        // origin certificate's name ("localhost"), exercising the connect-host-override path (identity used
        // for SNI/cert validation stays "localhost"; only the wire-level TCP destination changes).
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = rawServer.Port;

        using var rawClient = await Http2RawClient.ConnectDirectAsync(proxy.ProxyEndPoints[0].Port, "localhost");

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", System.Linq.Enumerable.Single(responseHeaders, h => h.Name == ":status").Value);

        await Task.Delay(300);
        Assert.AreEqual(1, rawServer.AcceptedConnectionCount,
            "Expected exactly one origin connection: the cold-cache discovery connection, retained and " +
            "adopted as the session connection, exactly like the explicit-handler path.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Event_Overridden_Forward_Target_Is_Used_For_Negotiation_And_Connection()
    {
        using var rawServer = new Http2RawOriginServer(CreateOriginCertificate());
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();

        // No static ForwardHost/ForwardPort at all - the forward target is resolved dynamically, purely
        // from the BeforeSslAuthenticate event, exercising "invoke BeforeSslAuthenticate before origin
        // selection and apply its final forward target" directly.
        endpoint.BeforeSslAuthenticate += (sender, e) =>
        {
            e.ForwardHttpsPort = rawServer.Port;
            return Task.CompletedTask;
        };

        using var rawClient = await Http2RawClient.ConnectDirectAsync(proxy.ProxyEndPoints[0].Port, "localhost");

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

        var (_, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", System.Linq.Enumerable.Single(responseHeaders, h => h.Name == ":status").Value,
            "The dynamically event-set forward target should have been used to reach the real h2 origin.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Falls_Back_To_Http11_When_Origin_Lacks_Http2()
    {
        using var rawServer = new Http11OnlyOriginServer(CreateOriginCertificate());

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        // both host and port set (the conventional "fixed forward target" shape), so the pre-existing
        // static-forward mechanism used by the HTTP/1.1 fallback path picks it up too.
        endpoint.ForwardHost = "localhost";
        endpoint.ForwardPort = rawServer.Port;

        // A raw client offering h2 (mirrors a real h2-capable browser) must still end up negotiating
        // http/1.1 with the proxy, because the proxy discovered the real origin cannot speak h2.
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", proxy.ProxyEndPoints[0].Port);
        using var _ = tcpClient;

        var sslStream = new SslStream(tcpClient.GetStream(), false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        using var __ = sslStream;
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols =
                new System.Collections.Generic.List<SslApplicationProtocol>
                    { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
            EnabledSslProtocols = SslProtocols.None
        });

        Assert.AreNotEqual(SslApplicationProtocol.Http2, sslStream.NegotiatedApplicationProtocol,
            "The origin does not support h2, so the proxy must not have negotiated h2 with the client either.");

        var requestBytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await sslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new System.IO.StreamReader(sslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response from the h1.1 fallback path, got: '{statusLine}'.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Completed_Stream_Fires_BeforeRequest_BeforeResponse_AfterResponse_Exactly_Once()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardPort = server.HttpsListeningPort;

        var beforeRequestCount = 0;
        var beforeResponseCount = 0;
        var afterResponseCount = 0;
        proxy.BeforeRequest += (_, _) => { Interlocked.Increment(ref beforeRequestCount); return Task.CompletedTask; };
        proxy.BeforeResponse += (_, _) => { Interlocked.Increment(ref beforeResponseCount); return Task.CompletedTask; };
        proxy.AfterResponse += (_, _) => { Interlocked.Increment(ref afterResponseCount); return Task.CompletedTask; };

        using var client = GetHttp2DirectClient();
        var response = await client.GetAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}/"));
        await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);

        for (var i = 0; i < 100 && afterResponseCount < 1; i++)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(1, beforeRequestCount);
        Assert.AreEqual(1, beforeResponseCount);
        Assert.AreEqual(1, afterResponseCount);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Many_Concurrent_Streams_With_Distinct_Headers_Do_Not_Cross_Contaminate()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            var echo = context.Request.Headers["X-Stream-Id"].ToString();
            context.Response.Headers["X-Stream-Id-Echo"] = echo;
            return context.Response.WriteAsync(echo);
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardPort = server.HttpsListeningPort;

        using var client = GetHttp2DirectClient();
        var baseUri = new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}/");

        const int concurrency = 20;
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUri) { Version = new Version(2, 0) };
            request.Headers.Add("X-Stream-Id", i.ToString());

            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(new Version(2, 0), response.Version);
            Assert.AreEqual(i.ToString(), body, $"Stream #{i}'s response body was cross-contaminated.");
            Assert.AreEqual(i.ToString(), response.Headers.GetValues("X-Stream-Id-Echo").Single(),
                $"Stream #{i}'s response header was cross-contaminated.");
        });

        await Task.WhenAll(tasks);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Ok_From_BeforeRequest_Answers_Client_And_Origin_Never_Sees_Request()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        var originContacted = false;
        server.HandleRequest(context =>
        {
            originContacted = true;
            return context.Response.WriteAsync("origin-should-not-be-reached");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-ok-body");
            return Task.CompletedTask;
        };

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardPort = server.HttpsListeningPort;

        using var client = GetHttp2DirectClient();
        var response = await client.GetAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}/"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("synthetic-ok-body", body);
        Assert.IsFalse(originContacted, "The request was forwarded upstream despite being answered by Ok().");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_RespondStreaming_Generates_Body_Without_Contacting_Server()
    {
        using var testSuite = new TestSuite();
        var serverCalled = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            serverCalled = true;
            return context.Response.WriteAsync("from server");
        });

        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardPort = server.HttpsListeningPort;

        proxy.BeforeRequest += (_, e) =>
        {
            var response = new Titanium.Web.Proxy.Http.Response
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

        using var client = GetHttp2DirectClient();
        var response = await client.GetAsync(new Uri($"https://localhost:{proxy.ProxyEndPoints[0].Port}/"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual("chunk1chunk2chunk3", body);
        Assert.IsFalse(serverCalled, "Server should not be contacted for a synthetic streamed response.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task Http2_Transparent_Capability_Cache_Isolated_By_Forward_Target()
    {
        // Two forward targets behind the same SNI/certificate identity ("localhost") on the same proxy
        // instance (and therefore the same Http2OriginCapabilityCache): one a real h2 origin, the other
        // h1.1-only. The capability cache key must include the forward target, or the second endpoint's
        // negotiation would incorrectly reuse the first endpoint's cached result (or vice versa).
        using var h2OriginCert = CreateOriginCertificate();
        using var rawServer = new Http2RawOriginServer(h2OriginCert);
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders(new[] { (":status", "200") }, Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, true);
        });

        using var h11OriginCert = CreateOriginCertificate();
        using var h11Server = new Http11OnlyOriginServer(h11OriginCert);

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetReverseProxy();
        proxy.EnableHttp2 = true;

        var h2Endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        h2Endpoint.ForwardPort = rawServer.Port;

        var h11Endpoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "localhost",
            ForwardPort = h11Server.Port
        };
        proxy.AddEndPoint(h11Endpoint);

        using var h2Client = await Http2RawClient.ConnectDirectAsync(h2Endpoint.Port, "localhost");
        var h2RequestHeaders = h2Client.Connection.EncodeHeaders(
            new[] { (":method", "GET"), (":scheme", "https"), (":authority", "localhost"), (":path", "/") },
            Array.Empty<(string, string)>());
        await h2Client.Connection.WriteHeaderBlockAsync(1, h2RequestHeaders, true);
        var (_, h2ResponseHeaders, _) = await h2Client.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", h2ResponseHeaders.Single(h => h.Name == ":status").Value,
            "The h2-capable forward target should have served a real h2 response.");

        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("localhost", h11Endpoint.Port);
        using var _ = tcpClient;
        var sslStream = new SslStream(tcpClient.GetStream(), false,
            (_, certificate, chain, errors) => TestCertificateAuthority.Validate(certificate, errors));
        using var __ = sslStream;
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols =
                new System.Collections.Generic.List<SslApplicationProtocol>
                    { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
            EnabledSslProtocols = SslProtocols.None
        });

        Assert.AreNotEqual(SslApplicationProtocol.Http2, sslStream.NegotiatedApplicationProtocol,
            "The h1.1-only forward target's endpoint must not have been contaminated by the h2 endpoint's " +
            "cached capability result.");

        var requestBytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await sslStream.WriteAsync(requestBytes, 0, requestBytes.Length);

        using var reader = new System.IO.StreamReader(sslStream, Encoding.ASCII, false, 4096, true);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsTrue(statusLine != null && statusLine.StartsWith("HTTP/1.1 200"),
            $"Expected an HTTP/1.1 200 response, got: '{statusLine}'.");
    }
}

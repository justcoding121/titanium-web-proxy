using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
[DoNotParallelize]
public class HttpInterceptionFastPathTests
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

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Reverse_NoHandlers_DoesNotCallBeforeRequest_AndProxies()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("fast-path-ok"));

        var proxy = testSuite.GetReverseProxy();
        Assert.IsFalse(proxy.NeedsHttpInterception());

        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = new Uri(server.ListeningHttpUrl).Port;

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("fast-path-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Reverse_WithBeforeRequest_CallsHandler()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("intercept-ok"));

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = new Uri(server.ListeningHttpUrl).Port;

        var beforeCalled = 0;
        proxy.BeforeRequest += (_, _) =>
        {
            Interlocked.Increment(ref beforeCalled);
            return Task.CompletedTask;
        };

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("intercept-ok", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, beforeCalled);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Reverse_PredicateFalse_SkipsBeforeRequest_ForThatHost()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("predicate-ok"));

        var proxy = testSuite.GetReverseProxy();
        var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
        endpoint.ForwardHost = "127.0.0.1";
        endpoint.ForwardPort = new Uri(server.ListeningHttpUrl).Port;

        var beforeCalled = 0;
        proxy.BeforeRequest += (_, _) =>
        {
            Interlocked.Increment(ref beforeCalled);
            return Task.CompletedTask;
        };
        // Gate is on (handler subscribed) but predicate returns false for this reverse host.
        proxy.ShouldInterceptHttp = _ => false;

        using var client = testSuite.GetReverseProxyClient();
        var response = await client.GetAsync($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("predicate-ok", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, beforeCalled);
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2c_Reverse_NoHandlers_ProxiesWithoutBeforeRequest()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, endStream: false);
            await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                "h2c-fast"u8.ToArray());
        });

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = true
        };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        try
        {
            Assert.IsFalse(proxy.NeedsHttpInterception());
            var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
            endpoint.ForwardHost = "127.0.0.1";
            endpoint.ForwardPort = rawServer.Port;
            endpoint.ForwardCleartext = true;
            endpoint.BeforeHttpAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
                return Task.CompletedTask;
            };

            using var rawClient = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
            var requestHeaders = rawClient.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "http"), (":authority", "localhost"), (":path", "/")],
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

            var (streamId, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
            Assert.AreEqual(1, streamId);
            Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
            if (!endStream)
            {
                while (true)
                {
                    var frame = await rawClient.Connection.ReadFrameAsync();
                    if (frame.Type == Http2FrameType.Data)
                    {
                        Assert.AreEqual("h2c-fast", System.Text.Encoding.UTF8.GetString(frame.Payload));
                        break;
                    }
                }
            }
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H2c_Reverse_PredicateFalse_SkipsBeforeRequest()
    {
        using var rawServer = Http2RawOriginServer.CreateCleartext();
        rawServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            var (streamId, _, _) = await connection.ReadRequestAsync();
            var headers = connection.EncodeHeaders([(":status", "200")], Array.Empty<(string, string)>());
            await connection.WriteHeaderBlockAsync(streamId, headers, endStream: true);
        });

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false));
        proxy.Start();
        try
        {
            var beforeCalled = 0;
            proxy.BeforeRequest += (_, _) =>
            {
                Interlocked.Increment(ref beforeCalled);
                return Task.CompletedTask;
            };
            proxy.ShouldInterceptHttp = _ => false;

            var endpoint = proxy.ProxyEndPoints.OfType<TransparentProxyEndPoint>().First();
            endpoint.ForwardHost = "127.0.0.1";
            endpoint.ForwardPort = rawServer.Port;
            endpoint.ForwardCleartext = true;
            endpoint.BeforeHttpAuthenticate += (_, e) =>
            {
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
                return Task.CompletedTask;
            };

            using var rawClient = await Http2RawClient.ConnectCleartextDirectAsync(endpoint.Port);
            var requestHeaders = rawClient.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "http"), (":authority", "localhost"), (":path", "/")],
                Array.Empty<(string, string)>());
            await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, true);

            var (streamId, responseHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
            Assert.AreEqual(1, streamId);
            Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
            Assert.AreEqual(0, beforeCalled);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Cleartext_Reverse_To_Https_Origin_Succeeds()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("h1-to-https-ok"));

        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "localhost",
            ForwardPort = new Uri(server.ListeningHttpsUrl).Port,
            ForwardCleartext = false,
            GenericCertificateName = "localhost"
        });
        proxy.Start();
        try
        {
            using var client = testSuite.GetReverseProxyClient();
            var response = await client.GetAsync($"http://localhost:{proxy.ProxyEndPoints[0].Port}/");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("h1-to-https-ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    /// Session-lite must not prefetch headers when a fast-path SessionEventArgs is recycled:
    /// otherwise the second keep-alive POST body is parsed as headers (RPS ≈ concurrency).
    /// </summary>
    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task H1_Cleartext_Reverse_KeepAlive_Post_Twice_Succeeds()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var posts = 0;
        server.HandleRequest(async context =>
        {
            Interlocked.Increment(ref posts);
            using var ms = new System.IO.MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync($"ok-{ms.Length}");
        });

        var proxy = new ProxyServer(false, false, false);
        proxy.AddEndPoint(new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = new Uri(server.ListeningHttpUrl).Port,
            ForwardCleartext = true
        });
        proxy.Start();
        try
        {
            Assert.IsFalse(proxy.NeedsHttpInterception());
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}/post";
            for (var i = 0; i < 2; i++)
            {
                using var content = new ByteArrayContent(new byte[100]);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var response = await client.PostAsync(url, content);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("ok-100", await response.Content.ReadAsStringAsync());
            }

            Assert.AreEqual(2, posts);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}

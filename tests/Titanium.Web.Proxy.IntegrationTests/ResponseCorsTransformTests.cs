using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     The <c>ResponseCors</c> transform on a real request: the proxy reads the request <c>Origin</c> and adds the
///     allow header to the actual response, which Ingress <c>cors-allow-origin</c> relies on.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ResponseCorsTransformTests
{
    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ListedOrigin_IsEchoed_AndAnUnlistedOneGetsNoAllowHeader()
    {
        var (origin, originPort) = BindLoopbackHttpListener();
        using var cts = new CancellationTokenSource();
        var accept = Task.Run(() => ServeAsync(origin, cts.Token), cts.Token);

        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = false,
            // Route transforms run on the session path (the CLI turns this on whenever a route has transforms).
            EnableHttpInterception = true,
        };
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(endPoint);
        await AttachRouteAsync(proxy, originPort, "https://app.example.com,https://b.example.com");
        proxy.Start();

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            using var listed = await GetAsync(client, endPoint.Port, "https://b.example.com");
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode);
            Assert.AreEqual("https://b.example.com", string.Join(",", listed.Headers.GetValues("Access-Control-Allow-Origin")));
            StringAssert.Contains(string.Join(",", listed.Headers.GetValues("Vary")), "Origin");
            StringAssert.Contains(string.Join(",", listed.Headers.GetValues("Vary")), "Accept-Encoding");

            using var unlisted = await GetAsync(client, endPoint.Port, "https://evil.example.com");
            Assert.AreEqual(HttpStatusCode.OK, unlisted.StatusCode);
            Assert.IsFalse(unlisted.Headers.Contains("Access-Control-Allow-Origin"));
            StringAssert.Contains(string.Join(",", unlisted.Headers.GetValues("Vary")), "Origin");

            using var noOrigin = await GetAsync(client, endPoint.Port, null);
            Assert.IsFalse(noOrigin.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            cts.Cancel();
            proxy.Stop();
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Preflight_IsAnsweredLocally_AndAPlainOptionsReachesTheOrigin()
    {
        var (origin, originPort) = BindLoopbackHttpListener();
        using var cts = new CancellationTokenSource();
        var hits = 0;
        var accept = Task.Run(() => ServeAsync(origin, cts.Token, () => Interlocked.Increment(ref hits)), cts.Token);

        using var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = false,
            EnableHttpInterception = true,
        };
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            GenericCertificateName = "localhost",
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(endPoint);
        await AttachRouteAsync(
            proxy,
            originPort,
            "https://app.example.com",
            new RoutePolicyConfig
            {
                Name = "cors",
                Kind = "cors",
                Origins = "https://app.example.com",
                AllowCredentials = true,
                MaxAgeSeconds = 600,
                ExposeHeaders = "X-Request-Id",
            },
            new Dictionary<string, string>
            {
                ["allowCredentials"] = "true",
                ["exposeHeaders"] = "X-Request-Id",
            });
        proxy.Start();

        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            using var preflight = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{endPoint.Port}/data");
            preflight.Headers.TryAddWithoutValidation("Origin", "https://app.example.com");
            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
            using var preflightResponse = await client.SendAsync(preflight);
            Assert.AreEqual(HttpStatusCode.NoContent, preflightResponse.StatusCode);
            Assert.AreEqual("https://app.example.com", string.Join(",", preflightResponse.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.AreEqual("true", string.Join(",", preflightResponse.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.AreEqual("600", string.Join(",", preflightResponse.Headers.GetValues("Access-Control-Max-Age")));
            Assert.AreEqual(0, Volatile.Read(ref hits), "a preflight is answered without calling the origin");

            using var plain = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{endPoint.Port}/data");
            using var plainResponse = await client.SendAsync(plain);
            Assert.AreEqual(HttpStatusCode.OK, plainResponse.StatusCode);
            Assert.AreEqual("ok", await plainResponse.Content.ReadAsStringAsync());
            Assert.AreEqual(1, Volatile.Read(ref hits), "a plain OPTIONS reaches the origin");

            using var actual = await GetAsync(client, endPoint.Port, "https://app.example.com");
            Assert.AreEqual(HttpStatusCode.OK, actual.StatusCode);
            Assert.AreEqual("https://app.example.com", string.Join(",", actual.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.AreEqual("true", string.Join(",", actual.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.AreEqual("X-Request-Id", string.Join(",", actual.Headers.GetValues("Access-Control-Expose-Headers")));
            Assert.AreEqual(2, Volatile.Read(ref hits));
        }
        finally
        {
            cts.Cancel();
            proxy.Stop();
            try { origin.Stop(); origin.Close(); } catch { /* ignore */ }
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, int port, string? requestOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/data");
        if (requestOrigin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", requestOrigin);
        }

        return client.SendAsync(request);
    }

    private static async Task AttachRouteAsync(
        ProxyServer proxy,
        int originPort,
        string origins,
        RoutePolicyConfig? policy = null,
        Dictionary<string, string>? extraCors = null)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations =
                [
                    new DestinationConfig
                    {
                        Id = "d",
                        Address = "127.0.0.1",
                        Port = originPort,
                        UseDestinationHost = true,
                    },
                ],
            },
        ]);
        var parameters = new Dictionary<string, string> { ["origins"] = origins };
        if (extraCors is not null)
        {
            foreach (var (key, value) in extraCors)
            {
                parameters[key] = value;
            }
        }

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Policies = policy is null ? null : [policy],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Policies = policy is null ? null : [policy.Name],
                    Transforms =
                    [
                        new TransformConfig
                        {
                            Kind = TransformKinds.ResponseCors,
                            Parameters = parameters,
                        },
                    ],
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };
    }

    private static (HttpListener Listener, int Port) BindLoopbackHttpListener()
    {
        Exception? last = null;
        for (var i = 0; i < 8; i++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Failed to bind HttpListener.", last);
    }

    /// <summary>Answers 200 with its own <c>Vary</c>, so the test can see the proxy join rather than replace it.</summary>
    private static async Task ServeAsync(HttpListener listener, CancellationToken ct, Action? onRequest = null)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch { return; }

            try
            {
                onRequest?.Invoke();
                var body = Encoding.UTF8.GetBytes("ok");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.Headers["Vary"] = "Accept-Encoding";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }
}
